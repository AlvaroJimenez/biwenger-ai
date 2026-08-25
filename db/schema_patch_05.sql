-- =============================================================================
-- schema_patch_05.sql — Señales de titularidad a partir de 'fitness'
--
-- Hallazgo: player_daily.fitness es un array con una entrada por jornada
-- disputada. Un número es la puntuación de ese partido; null significa que el
-- jugador NO jugó esa jornada.
--
-- Eso permite distinguir dos casos que hasta ahora eran idénticos:
--   - jugó y sacó 0 puntos      → fitness [0]
--   - no jugó                   → fitness [null]
--
-- Ambos aparecían como points = 0.
--
-- Idempotente.
-- =============================================================================

-- Métricas derivadas del array -----------------------------------------------
-- Se encapsulan en una función para no repetir la lógica en cada vista y para
-- poder testearla de forma aislada.

CREATE OR REPLACE FUNCTION public.fitness_stats(fitness jsonb)
RETURNS TABLE (
    matchdays          integer,   -- jornadas con dato
    matches_played     integer,   -- jornadas en las que sí jugó
    avg_points         numeric,   -- media por partido jugado
    starter_ratio      numeric    -- proporción de jornadas jugadas
)
LANGUAGE sql
IMMUTABLE
AS $$
    WITH elems AS (
        SELECT jsonb_array_elements(COALESCE(fitness, '[]'::jsonb)) AS e
    ),
    vals AS (
        -- 'null' JSON y ausencia se tratan igual: no jugó.
        SELECT CASE WHEN jsonb_typeof(e) = 'number'
                    THEN (e #>> '{}')::numeric
                    ELSE NULL END AS pts
        FROM elems
    )
    SELECT
        COUNT(*)::integer                                        AS matchdays,
        COUNT(pts)::integer                                      AS matches_played,
        ROUND(AVG(pts), 2)                                       AS avg_points,
        ROUND(COUNT(pts)::numeric / NULLIF(COUNT(*), 0), 2)      AS starter_ratio
    FROM vals;
$$;

COMMENT ON FUNCTION public.fitness_stats(jsonb) IS
    'Deriva jornadas, partidos jugados, media de puntos y ratio de titularidad del array fitness.';


-- Vista de plantilla, ampliada ------------------------------------------------

CREATE OR REPLACE VIEW public.v_squad_analysis AS
WITH ventana AS (
    SELECT
        MAX(as_of)                       AS hoy,
        MAX(as_of) - INTERVAL '7 days'   AS hace_7d,
        MIN(as_of)                       AS inicio
    FROM public.player_daily
),
valores AS (
    SELECT
        us.player_id,
        MAX(pd.market_value) FILTER (WHERE pd.as_of = v.hoy)            AS valor_hoy,
        MAX(pd.market_value) FILTER (WHERE pd.as_of = v.hace_7d::date)  AS valor_7d,
        MAX(pd.market_value) FILTER (WHERE pd.as_of = v.inicio)         AS valor_inicio
    FROM public.user_squad us
    CROSS JOIN ventana v
    JOIN public.player_daily pd ON pd.player_id = us.player_id
    WHERE us.as_of = (SELECT MAX(as_of) FROM public.user_squad)
    GROUP BY us.player_id
)
SELECT
    us.player_id,
    p.name                                          AS player_name,
    t.name                                          AS team_name,
    pd.position,
    pd.status,

    -- ── Económico ────────────────────────────────────────────────────────
    pd.market_value,
    us.purchase_price,
    us.clause,
    pd.market_value - us.purchase_price             AS gain_abs,
    ROUND(100.0 * (pd.market_value - us.purchase_price)
          / NULLIF(us.purchase_price, 0), 2)        AS gain_pct,
    CASE WHEN us.purchase_price IS NULL
         THEN 'jugador del sorteo inicial: no se compró, su plusvalía no es calculable'
         ELSE 'comprado en el mercado'
    END                                             AS purchase_note,

    -- ── Tendencia de valor ───────────────────────────────────────────────
    pd.price_increment                              AS delta_1d,
    val.valor_hoy - val.valor_7d                    AS delta_7d,
    ROUND(100.0 * (val.valor_hoy - val.valor_7d)
          / NULLIF(val.valor_7d, 0), 2)             AS delta_pct_7d,
    val.valor_hoy - val.valor_inicio                AS delta_total,

    -- ── Rendimiento ──────────────────────────────────────────────────────
    pd.points                                       AS points_season,
    pd.points_last_season,
    CASE WHEN pd.points_last_season IS NULL
         THEN 'no jugó en LaLiga la temporada pasada: no hay histórico'
         ELSE NULL END                              AS last_season_note,

    -- ── Titularidad ──────────────────────────────────────────────────────
    fs.matchdays,
    fs.matches_played,
    fs.avg_points,
    fs.starter_ratio,

    -- ── Restricciones ────────────────────────────────────────────────────
    us.locked_until,
    (us.locked_until IS NOT NULL AND us.locked_until > now()) AS is_locked

FROM public.user_squad us
JOIN public.player_daily pd
       ON pd.player_id = us.player_id
      AND pd.as_of     = us.as_of
CROSS JOIN LATERAL public.fitness_stats(pd.fitness) fs
LEFT JOIN valores val ON val.player_id = us.player_id
LEFT JOIN public.player p ON p.player_id = us.player_id
LEFT JOIN public.team   t ON t.team_id   = pd.team_id
WHERE us.as_of = (SELECT MAX(as_of) FROM public.user_squad);

COMMENT ON VIEW public.v_squad_analysis IS
    'Plantilla con señales económicas, de tendencia, de rendimiento y de titularidad.';


-- Vista de mercado ------------------------------------------------------------
-- Mismas señales que la plantilla, para que sean comparables.

CREATE OR REPLACE VIEW public.v_market_analysis AS
WITH ventana AS (
    SELECT MAX(as_of) AS hoy,
           MAX(as_of) - INTERVAL '7 days' AS hace_7d
    FROM public.player_daily
)
SELECT
    ms.as_of,
    ms.player_id,
    p.name                              AS player_name,
    t.name                              AS team_name,
    pd.position,
    pd.status,

    ms.price,
    ms.is_system,
    ms.seller_name,
    pd.market_value,
    ROUND(100.0 * ms.price / NULLIF(pd.market_value, 0), 1) AS price_pct_of_value,
    pd.market_value - prev.market_value  AS delta_7d,

    pd.points                            AS points_season,
    pd.points_last_season,
    CASE WHEN pd.points_last_season IS NULL
         THEN 'no jugó en LaLiga la temporada pasada: no hay histórico'
         ELSE NULL END                   AS last_season_note,

    fs.matchdays,
    fs.matches_played,
    fs.avg_points,
    fs.starter_ratio,

    -- 150.000 € es el valor mínimo de Biwenger. Un jugador ahí no está barato:
    -- está en el suelo porque el mercado no le ve utilidad deportiva.
    (pd.market_value <= 150000)          AS at_price_floor

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
    'Mercado con las mismas señales que v_squad_analysis, para comparación directa.';
