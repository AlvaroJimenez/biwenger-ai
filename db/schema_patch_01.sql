-- =============================================================================
-- Patch 01 — alinear el esquema con el payload real de Biwenger
-- Aplicar DESPUÉS de schema.sql. Idempotente.
--
-- Motivo: el mapeo original estaba inferido de wrappers de la comunidad.
-- Tras inspeccionar la respuesta real aparecen campos no previstos.
-- =============================================================================

BEGIN;

-- Campos que existen en el payload y no estaban contemplados -----------------
ALTER TABLE player_daily
    ADD COLUMN IF NOT EXISTS fantasy_price      BIGINT,
    ADD COLUMN IF NOT EXISTS alt_positions      SMALLINT[],
    ADD COLUMN IF NOT EXISTS shirt_number       SMALLINT,
    ADD COLUMN IF NOT EXISTS status_info        TEXT,
    ADD COLUMN IF NOT EXISTS status_changed_at  TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS points_last_season INT;

COMMENT ON COLUMN player_daily.market_value  IS 'price: valor de mercado en euros. Es la serie temporal que importa.';
COMMENT ON COLUMN player_daily.fantasy_price IS 'fantasyPrice: valoración distinta y mucho mayor. No es el precio de mercado.';
COMMENT ON COLUMN player_daily.status_info   IS 'Texto libre del parte médico o sanción. Materia prima para la fase 3.';

-- Los entrenadores llegan mezclados con los jugadores, posición 5 ------------
COMMENT ON COLUMN player.position IS '1 POR · 2 DEF · 3 MED · 4 DEL · 5 ENTRENADOR';

-- Este endpoint no expone shortName ------------------------------------------
ALTER TABLE team DROP COLUMN IF EXISTS short_name;

-- Trazabilidad del recálculo de Biwenger --------------------------------------
ALTER TABLE raw_payload
    ADD COLUMN IF NOT EXISTS source_updated_at TIMESTAMPTZ;

COMMENT ON COLUMN raw_payload.source_updated_at
    IS 'Campo update del payload. Si no avanza entre días, Biwenger no ha recalculado.';

COMMIT;


-- =============================================================================
-- COMPROBACIONES POST-INGESTA
-- =============================================================================

-- Reparto por posición. Debe salir ~20 entrenadores (posición 5), uno por club.
-- SELECT position, COUNT(*) FROM player_daily
-- WHERE as_of = CURRENT_DATE GROUP BY position ORDER BY position;

-- Jugadores sin equipo (teamID null). Es normal que haya unos cuantos.
-- SELECT COUNT(*) FROM player_daily WHERE as_of = CURRENT_DATE AND team_id IS NULL;

-- Estados presentes. Lista abierta: si aparece uno nuevo, conviene enterarse.
-- SELECT status, COUNT(*) FROM player_daily
-- WHERE as_of = CURRENT_DATE GROUP BY status ORDER BY 2 DESC;

-- Sanidad de unidades: el mínimo debe rondar 150.000 y el máximo las decenas
-- de millones. Si sale en miles, se está capturando otra escala.
-- SELECT MIN(market_value), MAX(market_value), ROUND(AVG(market_value))
-- FROM player_daily WHERE as_of = CURRENT_DATE;
