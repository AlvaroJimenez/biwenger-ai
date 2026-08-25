-- =============================================================================
-- schema_patch_04.sql — Vista enriquecida para el asesor de ventas
--
-- Amplía v_user_squad_today con tendencia de valor y rendimiento deportivo.
-- La vista anterior solo tenía datos económicos, e inducía al modelo a razonar
-- exclusivamente sobre plusvalía, dejando fuera a los jugadores sin precio de
-- compra.
--
-- Idempotente.
-- =============================================================================

CREATE OR REPLACE VIEW public.v_squad_analysis AS
WITH ventana AS (
    SELECT
        MAX(as_of)                                   AS hoy,
        MAX(as_of) - INTERVAL '7 days'               AS hace_7d,
        MIN(as_of)                                   AS inicio
    FROM public.player_daily
),
valores AS (
    SELECT
        us.player_id,

        -- Valor actual.
        MAX(pd.market_value) FILTER (WHERE pd.as_of = v.hoy)              AS valor_hoy,

        -- Referencias para las dos ventanas de tendencia.
        MAX(pd.market_value) FILTER (WHERE pd.as_of = v.hace_7d::date)    AS valor_7d,
        MAX(pd.market_value) FILTER (WHERE pd.as_of = v.inicio)           AS valor_inicio

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

    -- 1 portero, 2 defensa, 3 centrocampista, 4 delantero, 5 entrenador.
    pd.position,
    pd.status,

    -- ── Económico ────────────────────────────────────────────────────────
    pd.market_value,
    us.purchase_price,
    us.clause,
    pd.market_value - us.purchase_price             AS gain_abs,
    ROUND(100.0 * (pd.market_value - us.purchase_price)
          / NULLIF(us.purchase_price, 0), 2)        AS gain_pct,

    -- Etiqueta explícita: un NULL en gain_pct se confunde con cero al
    -- serializarse a JSON. La frase no admite esa lectura.
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
    -- OJO: la temporada 2026-27 acaba de empezar. 'points' viene de muy pocos
    -- partidos y no es representativo por sí solo.
    pd.points                                       AS points_season,
    pd.points_last_season,
    COALESCE(pd.played_home, 0) + COALESCE(pd.played_away, 0) AS matches_played,

    -- ── Restricciones ────────────────────────────────────────────────────
    us.locked_until,
    (us.locked_until IS NOT NULL AND us.locked_until > now()) AS is_locked

FROM public.user_squad us
JOIN public.player_daily pd
       ON pd.player_id = us.player_id
      AND pd.as_of     = us.as_of
LEFT JOIN valores val ON val.player_id = us.player_id
LEFT JOIN public.player p ON p.player_id = us.player_id
LEFT JOIN public.team   t ON t.team_id   = pd.team_id
WHERE us.as_of = (SELECT MAX(as_of) FROM public.user_squad);

COMMENT ON VIEW public.v_squad_analysis IS
    'Plantilla con señales económicas, de tendencia y de rendimiento. Base del asesor de ventas.';
