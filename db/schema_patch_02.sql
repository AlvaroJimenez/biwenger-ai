-- =============================================================================
-- schema_patch_02.sql — Fase 1b: plantilla y cuenta del usuario
--
-- Mismo patrón que player_daily: snapshot diario con PK compuesta, de modo que
-- reejecutar la ingesta del mismo día sobrescribe en lugar de duplicar.
--
-- Idempotente: se puede aplicar varias veces sin efecto.
-- =============================================================================

-- Plantilla del usuario, un snapshot por día ---------------------------------

CREATE TABLE IF NOT EXISTS public.user_squad (
    as_of           date        NOT NULL,
    player_id       bigint      NOT NULL,

    -- Cuándo pasó a ser tuyo. En los jugadores del draft inicial es la misma
    -- fecha para todos.
    acquired_at     timestamptz,

    -- Lo que pagaste. NULL en los jugadores del draft: no se compraron, así que
    -- no tienen precio de adquisición y la plusvalía no está definida para ellos.
    purchase_price  bigint,

    -- Cláusula de rescisión: lo que otro debe pagar para llevárselo.
    clause          bigint,

    -- Ventana de bloqueo tras el fichaje. Mientras no venza, el jugador NO se
    -- puede vender. Es una restricción dura: la aplica el código, no el LLM.
    locked_until    timestamptz,

    ingested_at     timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT pk_user_squad PRIMARY KEY (as_of, player_id)
);

COMMENT ON TABLE  public.user_squad IS
    'Snapshot diario de la plantilla del usuario. Cruza con player_daily por player_id.';
COMMENT ON COLUMN public.user_squad.purchase_price IS
    'NULL para jugadores del draft inicial (no comprados).';

CREATE INDEX IF NOT EXISTS ix_user_squad_player
    ON public.user_squad (player_id, as_of DESC);


-- Estado de la cuenta, un snapshot por día ------------------------------------

CREATE TABLE IF NOT EXISTS public.user_account_daily (
    as_of         date        NOT NULL,
    user_id       bigint      NOT NULL,
    league_id     bigint      NOT NULL,

    team_name     text,
    balance       bigint      NOT NULL,
    points        integer,

    -- Valor total de la plantilla ese día, calculado al ingerir contra
    -- player_daily. Se guarda porque es caro de recomputar hacia atrás.
    squad_value   bigint,
    squad_size    integer,

    ingested_at   timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT pk_user_account_daily PRIMARY KEY (as_of, user_id, league_id)
);

COMMENT ON TABLE public.user_account_daily IS
    'Snapshot diario de saldo y valor de plantilla. PK incluye liga: un usuario puede jugar varias.';


-- Vista de trabajo: plantilla enriquecida con datos de mercado ----------------
-- Es la base de la herramienta "¿a quién vendo?".

CREATE OR REPLACE VIEW public.v_user_squad_today AS
WITH ultimo AS (
    SELECT MAX(as_of) AS d FROM public.user_squad
)
SELECT
    us.as_of,
    us.player_id,
    p.name                                  AS player_name,
    t.name                                  AS team_name,
    COALESCE(pd.position, p.position)       AS position,
    pd.status,
    pd.market_value,
    pd.price_increment,
    us.purchase_price,
    us.clause,
    us.locked_until,

    pd.market_value - us.purchase_price     AS gain_abs,
    ROUND(100.0 * (pd.market_value - us.purchase_price)
          / NULLIF(us.purchase_price, 0), 2) AS gain_pct,

    (us.locked_until IS NOT NULL AND us.locked_until > now()) AS is_locked

FROM public.user_squad us
JOIN ultimo ON us.as_of = ultimo.d
LEFT JOIN public.player_daily pd
       ON pd.player_id = us.player_id
      AND pd.as_of     = us.as_of
LEFT JOIN public.player p ON p.player_id = us.player_id
LEFT JOIN public.team   t ON t.team_id   = pd.team_id;

COMMENT ON VIEW public.v_user_squad_today IS
    'Plantilla actual con valor, plusvalía y bloqueo. Base de la herramienta de venta.';
