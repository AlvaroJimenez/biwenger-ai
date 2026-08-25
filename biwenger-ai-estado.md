# Biwenger AI — Estado del proyecto

_Última actualización: 24 de agosto de 2026_

---

## Objetivo

Sistema asesor para Biwenger (fantasy de LaLiga) construido como proyecto de
aprendizaje de ingeniería de IA: tool calling, RAG, agentes, evaluación de
sistemas no deterministas, y coste/latencia como restricciones de diseño.

**Principio arquitectónico central:** el LLM aconseja, nunca decide. Todas las
restricciones, cálculos y optimizaciones viven en C# y SQL deterministas y
testeables. La capa de IA es exclusivamente consultiva.

---

## Estructura

```
biwenger-ai/
├── db/
│   ├── schema.sql
│   ├── schema_patch_01.sql
│   ├── schema_patch_02.sql   ← plantilla y cuenta
│   ├── schema_patch_03.sql   ← mercado
│   ├── schema_patch_04.sql   ← vista de análisis de plantilla
│   ├── schema_patch_05.sql   ← titularidad desde 'fitness'
│   └── schema_patch_06.sql   ← vías de adquisición
├── Biwenger.Ingest/
│   ├── BiwengerClient.cs          (CDN público)
│   ├── BiwengerAuthClient.cs      (autenticado)
│   ├── Models.cs / SquadModels.cs / MarketModels.cs
│   ├── SnapshotRepository.cs / SquadRepository.cs / MarketRepository.cs
│   ├── DapperSetup.cs             ([ModuleInitializer] para DateOnly)
│   └── Program.cs
├── Biwenger.Advisor/
│   ├── Anthropicclient.cs         ⚠️ nombre con minúscula
│   ├── AccountTools.cs / SquadTools.cs / MarketTools.cs / PlanTools.cs
│   └── AdvisorProgram.cs
└── biwenger-ai-estado.md
```

---

## FASE 1 — Ingestión pública ✅

Cron diario en GitHub Actions (`30 5 * * *` UTC) contra el CDN público de
Biwenger. Sin credenciales.

**Esquema en tres capas:** `raw_payload` (append-only, con sha256),
dimensiones `team` / `player` (SCD tipo 1), hecho `player_daily` con upsert
idempotente, `ingest_run` como canario, y vistas de tendencia.

**Verificación (24 ago, 12 días de serie):**

| Comprobación | Resultado |
|---|---|
| Duplicados | 0 en toda la serie |
| Huecos | Ninguno (13 → 24 ago) |
| Valores congelados | 63 % con cambio diario |
| `price_increment` vs delta calculado | 562/562 coinciden |

Ese último contraste confirma que `price_increment` puede leerse directamente
de la API en lugar de calcularse con ventanas `LAG`. **Sigue pendiente
repetirlo en un día posterior a jornada.**

---

## FASE 1b — Acceso autenticado ✅

`BiwengerAuthClient` con login, caché de token, `SemaphoreSlim` con doble
comprobación y reintento único ante 401.

**Endpoints:** `POST /api/v2/auth/login`, `/api/v2/account`,
`/api/v2/user?fields=*,players(id,owner,price)`, `/api/v2/market`.

**Tablas:** `user_squad` y `user_account_daily`, ambas con snapshot diario y PK
compuesta. `SquadRepository` borra y reinserta el día para que las bajas
desaparezcan en lugar de quedarse pegadas.

Modo `dotnet run -- ingest-squad`. **Manual a propósito:** automatizarlo
exigiría meter las credenciales de Biwenger en GitHub Actions.

---

## FASE 1c — Mercado ✅

Tablas `market_sale` (snapshot diario, sin id en la API) y `market_offer`
(entidad con id estable, upsert en sitio con `first_seen_at` / `last_seen_as_of`
para seguir el ciclo de vida de cada puja).

Modo `dotnet run -- ingest-market`. También manual.

---

## FASE 2 — Asesor 🔄

Consola `Biwenger.Advisor`, bucle de tool calling propio sobre `HttpClient`
contra `/v1/messages`. Modelo actual: **claude-sonnet-4-6**.

### Herramientas

| Nombre | Fuente | Qué hace |
|---|---|---|
| `get_account` | Postgres | Saldo, `maximum_bid`, tamaño y reparto por posición |
| `get_squad` | `v_squad_analysis` | Plantilla con señales económicas, tendencia, rendimiento y titularidad |
| `get_market` | **Biwenger en vivo** | Mercado actual; persiste el snapshot como efecto secundario |
| `check_plan` | Postgres | Verifica presupuesto y composición de un plan propuesto |

`get_market` consulta en vivo porque el mercado rota a las 05:00 UTC y solo se
persiste cuando se lanza la ingesta a mano. Al guardar lo que ve, la serie
histórica se construye sola cada vez que se pregunta.

`check_plan` es la pieza que cierra el círculo del principio arquitectónico:
**el modelo propone, el C# verifica.** Lee los precios de la base, no del
modelo. Distingue `problems` (bloqueantes: sin dinero, sin once posible) de
`warnings` (riesgos: puja incierta, plantilla sin banquillo).

Mínimos: 1 portero, 3 defensas, 2 centrocampistas, 1 delantero, 11 en total.
Recomendado: 13 (once más dos suplentes) — por debajo avisa pero no bloquea.

---

## Trampas de datos descubiertas

Todas documentadas en las descripciones de las herramientas, que es donde el
modelo puede leerlas.

**`purchase_price` nulo ≠ cero.** Los jugadores del sorteo inicial no se
compraron. En `/user` el campo está ausente; en `/market` llega como `0`. Mismo
significado, dos representaciones. Se normaliza a NULL al mapear.

**`fitness` es un array por jornada.** Un número es la puntuación de ese
partido; **`null` significa que no jugó**. Es la señal de titularidad, y
distingue "jugó y sacó 0" de "no jugó" — indistinguibles mirando solo `points`.
Encapsulado en `fitness_stats(jsonb)`, que devuelve `matchdays`,
`matches_played`, `avg_points` y `starter_ratio`.

**`at_price_floor` (150.000 €) no es una ganga.** Es el suelo de Biwenger y
agrupa a los jugadores sin utilidad deportiva. Sin esta señal, el asesor
recomendaba porteros que no juegan.

**`points_last_season` es un TOTAL, no una media**, y NULL significa que no
jugó en LaLiga, no que rindiera mal.

**Tres vías de adquisición, no una.** Subasta del sistema (a ciegas contra
otros managers), puja a un manager (el dueño decide, incierta) y cláusula de
rescisión (segura, 25-50 % más cara). La cláusula está en los datos desde el
patch 03 pero no se exponía.

**`lockedUntil` ≠ `clauseLockedUntil`.** El primero bloquea la venta del
jugador; el segundo blinda su cláusula.

**`X-User` no es el `iss` del JWT.** Biwenger distingue cuenta global de
usuario dentro de cada liga.

**El campo "recalculó hace" es en realidad el próximo recálculo** — de ahí los
valores negativos en el log.

---

## Bugs corregidos (y su lección)

**`is_own_sale` sin `COALESCE`.** En las ventas del sistema `seller_user_id` es
NULL, y `NOT NULL` en SQL es NULL, no true. El filtro `WHERE NOT is_own_sale`
descartaba en silencio todo el mercado del sistema. Dmitrovic —el mejor portero
disponible— era invisible para el asesor.

**Aviso de puja en `problems` en lugar de `warnings`.** Como `viable` exigía
`problems.Count == 0`, cualquier plan que implicara negociar con otro manager
salía rechazado aunque el dinero sobrara. El modelo acabó recomendando
jugadores mediocres del sistema, y parecía criterio suyo.

**Lección común:** ambos producían respuestas impecables, bien razonadas y bien
presentadas, **sobre datos incompletos**. Ninguna revisión de la respuesta los
habría detectado. Es el argumento más fuerte a favor de la capa de evaluación.

**`CREATE OR REPLACE VIEW` no permite renombrar ni reordenar columnas.** Hay
que hacer `DROP VIEW IF EXISTS` primero.

---

## Coste — tabla comparativa

Misma pregunta, mismos datos, midiendo el efecto de modelo y descripciones:

| Configuración | Llamadas a `get_market` | Tokens entrada | Calidad |
|---|---|---|---|
| Haiku + descripción v1 | 5 | 18.571 | Inventa cifras, salta pasos |
| Sonnet + descripción v1 | 5 | 31.124 | Correcta pero cara |
| Haiku + descripción v2 | 1 | 21.427 | No suma ventas al presupuesto |
| **Sonnet + descripción v2** | **1** | **19.870** | **Completa y correcta** |
| Sonnet + `check_plan` | 1 (+6 verificaciones) | 108.492 | Verificada, sin errores aritméticos |

**Resultado no obvio:** mejorar la descripción de la herramienta bajó el coste
de Sonnet un 36 % *y* mejoró la respuesta, al evitar llamadas redundantes.
Documentar mejor no fue solo calidad, fue eficiencia.

**Y el coste de la corrección:** `check_plan` multiplica los tokens por cinco,
porque cada iteración reenvía el historial completo. Es el compromiso entre
fiabilidad y coste, medido.

---

## Siguientes pasos

### 1. Evaluación (prioritario)

Casos en JSON más un runner con aserciones verificables:
- Todo jugador citado existe en los datos devueltos
- Las cifras del resumen coinciden con las de `check_plan`
- No se recomienda vender jugadores bloqueados
- No se recomiendan jugadores con `at_price_floor`

Es lo que convierte el proyecto de demo en ingeniería, y hace medibles los
otros dos frentes.

### 2. Reducir coste

Que `check_plan` devuelva mensajes accionables al rechazar ("faltan X €; con la
venta de Y cuadraría") para converger en dos iteraciones en vez de seis. Diseño
de mensajes de error, aplicado a un consumidor que razona en lenguaje natural.

### 3. "¿Qué alineación pongo?"

Optimizador con restricciones en C#, con el modelo explicando el resultado y
los descartes. Requiere que `avg_points` tenga muestra suficiente — con 2
jornadas es ruido.

---

## Backlog Fase 1

1. **Deriva del cron** — GitHub Actions arranca hasta +1h23m tarde
2. **Cambio horario del 25-oct-2026** — `30 5 * * *` pasará a 06:30 local
3. **Tests de integración en C#** — convertir las queries de verificación
4. **Duración del job** — de 1m57s a 3m0s; vigilar saturación de la B1ms
5. **`price_increment` post-jornada** — repetir el contraste tras una jornada
6. **Log mal etiquetado** — "recalculó hace" es el próximo recálculo
7. **Migraciones reproducibles** — aplicar patches a mano provocó que el 05
   quedara solo en local y fallara en Azure
8. **Renombrar `Anthropicclient.cs`** — la minúscula ya costó una depuración
9. **Regla de firewall temporal** — borrar las de viaje al volver

---

## Infraestructura

- **PostgreSQL 16** — local en Docker (`biwenger-pg`) y Azure Flexible Server
  B1ms en Spain Central (`biwenger-pg-ajs`)
- **GitHub Actions** — cron diario con `BIWENGER_TARGET=BiwengerAzure`
- **Secrets** — `dotnet user-secrets` en local, secrets de repo en CI. Cada
  proyecto tiene su propio store: `Biwenger.Advisor` necesita los suyos
- **Anthropic API** — saldo de 5 US$, clave `biwenger-advisor`

### Comandos

```powershell
# Ingesta pública (la que corre el cron)
dotnet run

# Plantilla y mercado — manuales
$env:BIWENGER_TARGET = "BiwengerAzure"
dotnet run -- ingest-squad
dotnet run -- ingest-market

# Asesor
cd ..\Biwenger.Advisor
dotnet run -- "¿a quién vendo y a quién ficho?"

# Aplicar un patch a Azure
docker cp schema_patch_NN.sql biwenger-pg:/tmp/patch.sql
docker exec -it biwenger-pg psql "host=biwenger-pg-ajs.postgres.database.azure.com port=5432 dbname=biwenger user=biwenger sslmode=require" -f /tmp/patch.sql
```

⚠️ `$env:BIWENGER_TARGET` persiste en la ventana de PowerShell. Cerrarla al
terminar, o volver a `"Biwenger"`.