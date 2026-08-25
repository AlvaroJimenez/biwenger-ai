-- =============================================================================
-- schema_patch_03.sql — Fase 1c: mercado diario
--
-- Dos hechos distintos:
--   market_sale  → jugadores en venta (sistema o manager)
--   market_offer → pujas visibles (dirigidas a ti o hechas por ti)
--
-- market_offer tiene id estable en la API, así que se sigue su ciclo de vida
-- con SCD Tipo 1: una fila por oferta, actualizada cuando cambia el estado.
-- market_sale no tiene id, así que se modela como snapshot diario.
--
-- Idempotente.
-- =============================================================================

-- Ventas: snapshot diario -----------------------------------------------------

CREATE TABLE IF NOT EXISTS public.market_sale (
    as_of               date        NOT NULL,
    player_id           bigint      NOT NULL,

    -- Sin dueño: lo vende el sistema, no un manager. En el JSON, user = null.
    -- Las del sistema comparten date/until y rotan cada 24 h.
    is_system           boolean     NOT NULL,
    seller_user_id      bigint,
    seller_name         text,

    price               bigint      NOT NULL,
    listed_at           timestamptz,
    until               timestamptz,

    -- El campo "expired" solo aparece en el JSON cuando es true.
    expired             boolean     NOT NULL DEFAULT false,

    -- Datos de propiedad, presentes solo en ventas de manager.
    -- OJO: aquí "price" llega como 0 en jugadores de draft, no ausente como en
    -- /user. Se normaliza a NULL al mapear: 0 significaría plusvalía completa.
    owner_purchase_price bigint,
    owner_clause         bigint,
    owner_since          timestamptz,
    clause_locked_until  timestamptz,

    ingested_at         timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT pk_market_sale PRIMARY KEY (as_of, player_id)
);

COMMENT ON TABLE public.market_sale IS
    'Snapshot diario del mercado. PK (as_of, player_id): un jugador no puede estar dos veces en venta el mismo día.';
COMMENT ON COLUMN public.market_sale.owner_purchase_price IS
    'NULL en jugadores de draft. La API envía 0; se normaliza al mapear.';

CREATE INDEX IF NOT EXISTS ix_market_sale_player
    ON public.market_sale (player_id, as_of DESC);

CREATE INDEX IF NOT EXISTS ix_market_sale_system
    ON public.market_sale (as_of, is_system) WHERE NOT expired;


-- Ofertas: entidad con identidad propia ---------------------------------------

CREATE TABLE IF NOT EXISTS public.market_offer (
    offer_id            bigint      NOT NULL,

    amount              bigint      NOT NULL,
    status              text        NOT NULL,   -- waiting, y lo que aparezca
    offer_type          text,                   -- purchase, ...

    -- from = null → la oferta viene del sistema.
    -- to   = null → la oferta la hemos hecho nosotros al sistema.
    from_user_id        bigint,
    from_name           text,
    to_user_id          bigint,
    to_name             text,

    -- Array: hoy siempre un elemento, pero el nombre del campo en la API
    -- ("requestedPlayers") sugiere que los intercambios múltiples son posibles.
    requested_players   bigint[]    NOT NULL,

    created_at          timestamptz,
    until               timestamptz,

    -- Trazabilidad del ciclo de vida, calculada por nosotros.
    first_seen_at       timestamptz NOT NULL DEFAULT now(),
    last_seen_at        timestamptz NOT NULL DEFAULT now(),
    last_seen_as_of     date        NOT NULL,

    CONSTRAINT pk_market_offer PRIMARY KEY (offer_id)
);

COMMENT ON TABLE public.market_offer IS
    'Ofertas con id estable de la API. Se actualizan en sitio; last_seen_as_of marca el último día que seguían vivas.';
COMMENT ON COLUMN public.market_offer.requested_players IS
    'Array por si Biwenger admite intercambios de varios jugadores. Hoy siempre longitud 1.';

CREATE INDEX IF NOT EXISTS ix_market_offer_last_seen
    ON public.market_offer (last_seen_as_of DESC);

CREATE INDEX IF NOT EXISTS ix_market_offer_players
    ON public.market_offer USING GIN (requested_players);


-- Estado de la cuenta en el mercado -------------------------------------------
-- maximumBid es mayor que el saldo; presumiblemente cuenta el valor liquidable
-- de la plantilla. Se guarda para contrastarlo a lo largo de varios días.

ALTER TABLE public.user_account_daily
    ADD COLUMN IF NOT EXISTS maximum_bid bigint;

COMMENT ON COLUMN public.user_account_daily.maximum_bid IS
    'Capacidad máxima de puja según la API. Su fórmula no está confirmada.';


-- Vista: ofertas activas por mis jugadores ------------------------------------
-- Base de la herramienta "¿acepto esta oferta?".

CREATE OR REPLACE VIEW public.v_incoming_offers AS
WITH ultimo AS (
    SELECT MAX(last_seen_as_of) AS d FROM public.market_offer
)
SELECT
    o.offer_id,
    o.amount,
    o.status,
    COALESCE(o.from_name, 'Sistema')        AS ofertante,
    p.player_id,
    p.name                                  AS player_name,
    us.purchase_price,
    pd.market_value,

    -- Lo que ganas o pierdes respecto a lo que pagaste. NULL en jugadores de
    -- draft: sin precio de compra, la ganancia no está definida.
    o.amount - us.purchase_price            AS profit_vs_purchase,

    -- Cómo se compara la oferta con el valor de mercado actual.
    ROUND(100.0 * o.amount / NULLIF(pd.market_value, 0), 1) AS pct_of_value,

    o.until

FROM public.market_offer o
JOIN ultimo ON o.last_seen_as_of = ultimo.d
CROSS JOIN LATERAL unnest(o.requested_players) AS rp(player_id)
JOIN public.player p ON p.player_id = rp.player_id
LEFT JOIN public.user_squad us
       ON us.player_id = rp.player_id
      AND us.as_of = (SELECT MAX(as_of) FROM public.user_squad)
LEFT JOIN public.player_daily pd
       ON pd.player_id = rp.player_id
      AND pd.as_of = (SELECT MAX(as_of) FROM public.player_daily)
WHERE o.to_user_id IS NOT NULL      -- dirigidas a nosotros
  AND o.status = 'waiting';

COMMENT ON VIEW public.v_incoming_offers IS
    'Ofertas activas por jugadores propios, con ganancia frente al precio de compra.';
