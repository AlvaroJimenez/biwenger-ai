-- =============================================================================
-- schema_patch_06.sql — Vías de adquisición en el mercado
--
-- En Biwenger hay tres formas distintas de conseguir un jugador, y no son
-- equivalentes:
--
--   1. Subasta del sistema     → jugador libre. Se puja a ciegas contra otros
--                                managers y se resuelve al cierre.
--   2. Puja a un manager       → el dueño DECIDE si acepta. Puede rechazarla,
--                                aceptar otra o retirar al jugador. Incierta.
--   3. Cláusula de rescisión   → pago directo, el dueño no puede impedirlo.
--                                Segura, pero ~25-50% más cara.
--
-- La vista anterior las mezclaba todas bajo 'price', induciendo a tratar una
-- puja incierta como si fuera una compra cerrada.
--
-- CUIDADO CON LOS NULL: en las ventas del sistema, seller_user_id es NULL.
-- Comparar NULL con cualquier valor devuelve NULL, no false, y un filtro
-- 'WHERE NOT is_own_sale' descarta esas filas en silencio. De ahí el COALESCE
-- y el orden del CASE, que evalúa primero la condición no ambigua.
--
-- Idempotente. DROP previo porque CREATE OR REPLACE VIEW no admite cambios de
-- nombre ni de orden en las columnas ya existentes.
-- =============================================================================

DROP VIEW IF EXISTS public.v_market_analysis;

CREATE VIEW public.v_market_analysis AS
WITH ventana AS (
    SELECT MAX(as_of) AS hoy,
           MAX(as_of) - INTERVAL '7 days' AS hace_7d
    FROM public.player_daily
),
yo AS (
    -- Identificador del usuario, tomado de sus propios datos de cuenta.
    -- Evita hardcodear el id en la vista.
    SELECT user_id
    FROM public.user_account_daily
    ORDER BY as_of DESC
    LIMIT 1
)
SELECT
    ms.as_of,
    ms.player_id,
    p.name                              AS player_name,
    t.name                              AS team_name,
    pd.position,
    pd.status,

    -- ── Vía de adquisición ───────────────────────────────────────────────
    -- is_system va primero: es la única condición que nunca es NULL.
    CASE
        WHEN ms.is_system                                 THEN 'subasta_sistema'
        WHEN ms.seller_user_id = (SELECT user_id FROM yo) THEN 'venta_propia'
        ELSE                                                   'puja_a_manager'
    END                                 AS acquisition_route,

    -- Un jugador que vende el propio usuario no es una oportunidad de fichaje.
    -- COALESCE porque seller_user_id es NULL en las ventas del sistema.
    COALESCE(ms.seller_user_id = (SELECT user_id FROM yo), false) AS is_own_sale,

    ms.is_system,
    ms.seller_name,

    -- ── Precios ──────────────────────────────────────────────────────────
    ms.price,
    pd.market_value,
    ROUND(100.0 * ms.price / NULLIF(pd.market_value, 0), 1) AS price_pct_of_value,

    ms.owner_clause,
    ms.clause_locked_until,

    -- La cláusula está disponible si existe y no está blindada.
    COALESCE(
        ms.owner_clause IS NOT NULL
        AND (ms.clause_locked_until IS NULL OR ms.clause_locked_until <= now()),
        false)                          AS clause_available,

    -- Cuánto más cuesta la certeza frente a la puja.
    ROUND(100.0 * (ms.owner_clause - ms.price)
          / NULLIF(ms.price, 0), 1)     AS clause_premium_pct,

    pd.market_value - prev.market_value AS delta_7d,

    -- ── Rendimiento y titularidad ────────────────────────────────────────
    pd.points                           AS points_season,
    pd.points_last_season,
    CASE WHEN pd.points_last_season IS NULL
         THEN 'no jugó en LaLiga la temporada pasada: no hay histórico'
         ELSE NULL END                  AS last_season_note,

    fs.matchdays,
    fs.matches_played,
    fs.avg_points,
    fs.starter_ratio,

    -- 150.000 € es el suelo de Biwenger: no es una ganga, es irrelevancia.
    COALESCE(pd.market_value <= 150000, false) AS at_price_floor

FROM public.market_sale ms
CROSS JOIN ventana v
JOIN public.player p ON p.player_id = ms.player_id
LEFT JOIN public.player_daily pd
       ON pd.player_id = ms.player_id AND pd.as_of = v.hoy
LEFT JOIN public.player_daily prev
       ON prev.player_id = ms.player_id AND prev.as_of = v.hace_7d::date
CROSS JOIN LATERAL public.fitness_stats(pd.fitness) fs
LEFT JOIN public.team t ON t.team_id = pd.team_id
WHERE NOT ms.expired
  AND pd.position <> 5;

COMMENT ON VIEW public.v_market_analysis IS
    'Mercado con vía de adquisición, precios de puja y cláusula, y señales de titularidad.';