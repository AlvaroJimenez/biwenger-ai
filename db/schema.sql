-- =============================================================================
-- Biwenger AI — Fase 1: esquema de ingesta
--
-- Tres capas:
--   1. raw_payload            JSON íntegro tal como llega. Nunca se borra.
--   2. team / player          dimensiones, SCD tipo 1 (se sobrescriben).
--   3. player_daily           hecho diario, clave (player_id, as_of).
--
-- Idempotente: se puede reejecutar sobre una base ya creada.
-- Todos los valores monetarios son BIGINT en euros. Nunca float para dinero.
-- =============================================================================

BEGIN;

-- -----------------------------------------------------------------------------
-- Utilidad: mantener updated_at en las dimensiones
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION set_updated_at() RETURNS trigger AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;


-- =============================================================================
-- CAPA 1 — RAW
-- El JSON se guarda ANTES de parsearlo. Como el mapeo está inferido, esto
-- convierte un error de mapeo en un bug reparable en vez de datos perdidos.
-- =============================================================================
CREATE TABLE IF NOT EXISTS raw_payload (
    id              BIGSERIAL   PRIMARY KEY,
    as_of           DATE        NOT NULL,
    fetched_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    source          TEXT        NOT NULL,          -- URL del endpoint
    score_mode      SMALLINT    NOT NULL DEFAULT 1,-- score=1 (Biwenger). No cambiar.
    http_status     INT,
    payload         JSONB       NOT NULL,
    payload_sha256  TEXT        NOT NULL,

    -- Reejecutar el mismo día con el mismo contenido no duplica.
    -- Si el contenido cambió, se guarda como fila nueva: es historia, no ruido.
    CONSTRAINT raw_payload_uniq UNIQUE (as_of, source, score_mode, payload_sha256)
);

CREATE INDEX IF NOT EXISTS ix_raw_payload_as_of ON raw_payload (as_of DESC);
CREATE INDEX IF NOT EXISTS ix_raw_payload_gin   ON raw_payload USING GIN (payload jsonb_path_ops);

COMMENT ON TABLE  raw_payload IS 'Payload crudo sin parsear. Append-only, nunca se borra.';
COMMENT ON COLUMN raw_payload.score_mode IS 'Sistema de puntuación fijado. Cambiarlo rompe la comparabilidad del histórico.';


-- =============================================================================
-- CAPA 2 — DIMENSIONES (SCD tipo 1: sin historial, se sobrescriben)
-- El historial de verdad vive en player_daily.
-- =============================================================================
CREATE TABLE IF NOT EXISTS team (
    team_id        INT         PRIMARY KEY,        -- id de Biwenger
    name           TEXT        NOT NULL,
    slug           TEXT,
    short_name     TEXT,
    first_seen_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

DROP TRIGGER IF EXISTS trg_team_updated_at ON team;
CREATE TRIGGER trg_team_updated_at
    BEFORE UPDATE ON team
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();


CREATE TABLE IF NOT EXISTS player (
    player_id      BIGINT      PRIMARY KEY,        -- id de Biwenger
    team_id        INT         REFERENCES team (team_id),
    name           TEXT        NOT NULL,
    slug           TEXT,
    position       SMALLINT,                       -- 1 POR · 2 DEF · 3 MED · 4 DEL
    first_seen_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- Mapeo inferido: si aparecen otros códigos, relajar o eliminar este CHECK.
    CONSTRAINT player_position_chk CHECK (position IS NULL OR position BETWEEN 1 AND 5)
);

CREATE INDEX IF NOT EXISTS ix_player_team ON player (team_id);

DROP TRIGGER IF EXISTS trg_player_updated_at ON player;
CREATE TRIGGER trg_player_updated_at
    BEFORE UPDATE ON player
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON COLUMN player.team_id IS 'Equipo ACTUAL. Un traspaso sobrescribe. El equipo histórico está en player_daily.';


-- =============================================================================
-- CAPA 3 — HECHO DIARIO
-- Append-only en el sentido de que no se borra nada nunca. Reejecutar el mismo
-- día hace UPSERT sobre la fila de ese día (no duplica). Eso es lo que hace
-- seguros el cron y el backfill.
--
-- Casi todo nullable a propósito: el mapeo está inferido y un campo que no
-- cuadre no debe tumbar la ingesta.
-- =============================================================================
CREATE TABLE IF NOT EXISTS player_daily (
    player_id        BIGINT      NOT NULL REFERENCES player (player_id),
    as_of            DATE        NOT NULL,

    team_id          INT         REFERENCES team (team_id),  -- equipo EN esa fecha
    position         SMALLINT,

    market_value     BIGINT,                                 -- euros
    price_increment  BIGINT,                                 -- variación según la API
    points           INT,
    points_home      INT,
    points_away      INT,
    played_home      INT,
    played_away      INT,
    status           TEXT,                                   -- ok / injured / doubt / ...
    fitness          JSONB,                                  -- últimas puntuaciones, admite nulls

    raw_payload_id   BIGINT      REFERENCES raw_payload (id),
    ingested_at      TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (player_id, as_of)
);

CREATE INDEX IF NOT EXISTS ix_player_daily_as_of ON player_daily (as_of DESC);
CREATE INDEX IF NOT EXISTS ix_player_daily_value ON player_daily (as_of DESC, market_value DESC);

COMMENT ON COLUMN player_daily.raw_payload_id IS 'Trazabilidad: de qué payload crudo salió esta fila.';


-- =============================================================================
-- CANARIO / OBSERVABILIDAD
-- Si la liga entera cabe en menos de 100 jugadores, algo ha cambiado.
-- =============================================================================
CREATE TABLE IF NOT EXISTS ingest_run (
    id                BIGSERIAL   PRIMARY KEY,
    as_of             DATE        NOT NULL,
    started_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    finished_at       TIMESTAMPTZ,
    players_seen      INT,
    players_written   INT,
    players_skipped   INT,
    ok                BOOLEAN,
    notes             TEXT
);

CREATE INDEX IF NOT EXISTS ix_ingest_run_as_of ON ingest_run (as_of DESC);


-- =============================================================================
-- VISTA — TENDENCIA DE VALOR
-- Deltas a 1 y 7 días por fecha EXACTA (no por fila anterior): si falta un día
-- de captura, el delta sale NULL en vez de mentir comparando contra otra fecha.
-- =============================================================================
CREATE OR REPLACE VIEW v_player_value_trend AS
SELECT
    d.player_id,
    p.name                                       AS player_name,
    d.position,
    t.name                                       AS team_name,
    d.as_of,
    d.market_value,
    d1.market_value                              AS value_1d_ago,
    d7.market_value                              AS value_7d_ago,
    d.market_value - d1.market_value             AS delta_1d,
    d.market_value - d7.market_value             AS delta_7d,
    ROUND(100.0 * (d.market_value - d7.market_value)
          / NULLIF(d7.market_value, 0), 2)       AS pct_7d,
    d.status
FROM       player_daily d
JOIN       player       p  ON p.player_id  = d.player_id
LEFT JOIN  team         t  ON t.team_id    = d.team_id
LEFT JOIN  player_daily d1 ON d1.player_id = d.player_id AND d1.as_of = d.as_of - 1
LEFT JOIN  player_daily d7 ON d7.player_id = d.player_id AND d7.as_of = d.as_of - 7;


COMMIT;


-- =============================================================================
-- QUERIES DE VERIFICACIÓN DEL MAPEO
-- Ejecutar tras la PRIMERA ingesta, antes de poner nada en cron.
-- =============================================================================

-- 1. Qué claves trae de verdad un jugador del payload crudo.
--    Comparar contra los DTOs de Models.cs.
-- SELECT DISTINCT jsonb_object_keys(pl)
-- FROM raw_payload r,
--      LATERAL jsonb_array_elements(r.payload -> 'data' -> 'players') AS pl
-- WHERE r.as_of = CURRENT_DATE;

-- 2. Canario de volumen.
-- SELECT as_of, COUNT(*) FROM player_daily GROUP BY as_of ORDER BY as_of DESC;

-- 3. Campos que se están perdiendo (columna vacía = mapeo mal inferido).
-- SELECT
--     COUNT(*)                                       AS filas,
--     COUNT(market_value)                            AS con_valor,
--     COUNT(points)                                  AS con_puntos,
--     COUNT(status)                                  AS con_status,
--     COUNT(*) FILTER (WHERE position IS NULL)       AS sin_posicion
-- FROM player_daily WHERE as_of = CURRENT_DATE;

-- 4. Sanidad de valores de mercado (detecta unidades mal interpretadas:
--    céntimos vs euros, miles vs unidades).
-- SELECT MIN(market_value), MAX(market_value), ROUND(AVG(market_value))
-- FROM player_daily WHERE as_of = CURRENT_DATE;
