# Biwenger AI — estado del proyecto

Documento vivo. Actualizar al cerrar cada fase.
Última actualización: 13 de agosto de 2026.

---

## 1. Qué es esto y por qué

Asistente de decisiones para Biwenger (fantasy de LaLiga): qué jugadores fichar
o vender, cómo salir de deuda, qué alineación maximiza puntos.

**El objetivo real no es el juego.** Es aprender a llevar features de IA a
producción como backend senior .NET: RAG, tool calling, agentes, MCP,
evaluación de sistemas no deterministas, y coste/latencia como restricciones de
diseño. Biwenger es la excusa, elegida porque:

- Es un problema de **decisión bajo restricciones**, no de cálculo directo
  (presupuesto, deuda, límite de plantilla, cláusulas, precios que se mueven a
  diario, puntuaciones con ruido). Ahí un agente aporta y una hoja de cálculo no.
- Hay **datos temporales de verdad**, con volumen e historial.
- El mundo da la **respuesta correcta cada semana**: predices, se juega la
  jornada, comparas. Es un lujo enorme para aprender evaluación.
- Lo usaría de verdad, cada jornada, durante nueve meses.

Ideas descartadas por el camino: asistente documental sobre papeles propios
(buen ejercicio técnico, menos motivador) y analizador de cartera de inversión
(dominio demasiado pequeño: cuatro posiciones dan para pocas preguntas
interesantes).

---

## 2. Restricciones no negociables

**Independencia laboral.** Repo propio, cuenta cloud propia, datos propios.
Nada de código, documentación ni datos de la empresa ni de sus clientes, ni
siquiera anonimizado. El proyecto tiene que poder enseñarse en una entrevista
sin pedir permiso a nadie.

**El LLM no decide.** La optimización y los cálculos van en C# determinista y
con tests. El modelo interpreta la pregunta, elige qué tools llamar y en qué
orden, incorpora contexto blando (lesiones, rotaciones, rivales) y explica el
porqué. No elige el equipo.

**Solo lectura.** Nada de pujas ni alineaciones automáticas. Además de rozar los
términos de uso, automatizar en una liga de amigos cambia el juego al que se
está jugando. **Advisory, no automático.**

**Seguridad.** Credenciales en user-secrets o Key Vault, nunca en el repo — que
es público. Solo cuenta propia y liga propia. Caché agresiva y respeto a los
rate limits.

**Ingerir todo, filtrar tarde.** Filtrar en la ingesta destruye información de
forma irreversible; filtrar en la consulta no. Aunque la liga no use
entrenadores, se capturan igual.

---

## 3. Fases

| Fase | Contenido | IA | Estado |
|---|---|---|---|
| 1 | Ingesta diaria → Postgres | Ninguna | **Ejecutada y verificada.** Falta cron |
| 2 | Proyección de puntos + optimización de plantilla | Ninguna | Pendiente |
| 3 | Capa de agente: tools, orquestación, evals | Aquí sí | Pendiente |

Nota deliberada: en un sistema de IA en producción la IA suele ser el ~20% del
código. El resto es esto — datos, contratos, cálculo determinista, evaluación.
El rodeo *es* el trabajo; ahí es donde falla la gente que pone LLMs en
producción, no en el prompt.

---

## 4. Fase 1 — Ingesta ✅

### Urgencia

La serie temporal de precios **solo se construye hacia delante**. Biwenger no
expone histórico profundo y la liga arranca ya. Cada día sin capturar es un día
perdido para siempre. Esto va a cron aunque no haya nada más construido.

Hora fija (p. ej. 03:30). Los valores de mercado se recalculan de madrugada; si
un día capturas a las 03:00 y otro a las 22:00, la serie deja de ser comparable.

### Fuente de datos

API interna de Biwenger, **no documentada y sin compromiso de estabilidad**.
Puede cambiar sin aviso a mitad de temporada. Endpoint de datos de competición:
`cf.biwenger.com/api/v2/competitions/la-liga/data` — público, sin auth, es el
CDN que consume la propia web. Fijar `score=1` y no cambiarlo, o el histórico
deja de ser comparable.

> Corrección: `score=1` es **Diario AS**, no Biwenger. El payload lista los
> sistemas disponibles en `data.scores` (1 AS, 2 SofaScore, 7 Feeberse, etc.).
> La decisión de fijarlo sigue siendo correcta; el nombre estaba mal.

Existen también endpoints autenticados (login con bearer token, cabeceras
`X-League` / `X-User` / `X-Version`) para mercado y escritura. Fuera de alcance
por ahora.

### Infraestructura levantada

- Postgres 16 en Docker: contenedor `biwenger-pg`, puerto 5432, volumen con
  nombre `biwenger_pgdata`, `--restart unless-stopped`.
- Proyecto `Biwenger.Ingest` (.NET, consola) dentro de la solución `Biwenger`.
- Cadena de conexión en user-secrets. Nunca en `appsettings.json`.

### Piezas escritas

- `db/schema.sql` — Postgres, tres capas: `raw_payload` (JSON íntegro, nunca se
  borra), `team` / `player` (dimensiones, SCD tipo 1), `player_daily` (hecho
  diario). Más `ingest_run` (canario y observabilidad) y la vista
  `v_player_value_trend` con deltas a 1 y 7 días.
- `db/schema_patch_01.sql` — ajustes tras verificar el payload real.
- `Models.cs` — DTOs del borde exterior + records de dominio.
- `BiwengerClient.cs` — cliente HTTP + `BiwengerMapper` (capa anticorrupción).
- `SnapshotRepository.cs` — Dapper/Npgsql, upserts idempotentes.
- `Program.cs` — orquestación; admite argumento de fecha.

### Decisiones de diseño

**Capa raw.** Se guarda el JSON crudo *antes* de parsearlo. Si mañana descubres
que un campo estaba mal, es un bug reparable en lugar de datos perdidos. Clave
única por `(as_of, source, score_mode, sha256)`: mismo contenido no duplica,
contenido distinto se guarda como fila nueva.

**Anti-corruption layer.** `BiwengerMapper` es el único punto que conoce la
forma del JSON externo. Si Biwenger cambia el contrato, se toca un fichero.

**Idempotencia.** Clave `(player_id, as_of)`. Reejecutar el mismo día
sobrescribe, no duplica.

**Todo en una transacción.** Raw, dimensiones y hechos entran juntos o no entra
nada. Un día a medias es peor que un día perdido: parece legítimo después.
`ingest_run` queda fuera a propósito, para que el registro de un fallo
sobreviva al rollback.

**Tolerancia a fallos, con matiz.** Un jugador sin id o sin nombre se descarta y
se registra. Una posición fuera de rango se anula pero la fila se guarda: perder
el precio del día es peor que perder un campo. El crudo conserva la verdad.

**Canario.** Si el mapeo produce menos de 100 jugadores, lanza excepción y no
escribe nada.

**Deltas por fecha exacta, no por fila anterior.** `LAG()` compararía contra la
captura previa; si falla un día, el "delta a 1 día" mentiría en silencio. Con
self-join sobre `as_of - 1` sale NULL, que es la verdad.

**Códigos de salida.** 0 correcto, 1 fallo. El cron los necesita para alertar.

### Verificación del mapeo ✅

El riesgo abierto de la versión anterior **queda cerrado**: el mapeo está
verificado contra la API real, no inferido de wrappers de la comunidad.

Cinco correcciones que salieron al mirar el payload:

1. **`players` y `teams` son diccionarios** indexados por id como clave de
   texto, no arrays. Deserializar como `List<>` habría fallado en la primera
   ejecución.
2. **`teamID` es nullable.** Hay jugadores sin equipo.
3. **La posición 5 es "Entrenador".** Vienen mezclados en la colección de
   jugadores, con precio y puntuación propios.
4. **Hay dos precios.** `price` es el valor de mercado en euros y es la serie
   que importa (`priceIncrement` es su delta diario). `fantasyPrice` es otra
   valoración, mucho mayor; se guarda pero no se usa.
5. **Campos no previstos:** `fantasyPrice`, `altPositions`, `number`,
   `statusInfo`, `statusChange`, `pointsLastSeason`. Y `team` no expone
   `shortName`.

### Primera ejecución (13/08/2026)

558 jugadores, 20 equipos, **cero avisos de mapeo**.

| Comprobación | Resultado |
|---|---|
| Reparto por posición | 48 POR · 185 DEF · 162 MED · 143 DEL · 20 ENT |
| Entrenadores | 20 — uno por club, correcto |
| Valor de mercado | mín 150.000 € · máx 25.740.000 € · media 2.343.584 € |
| Precio no nulo | 558 / 558 |
| Estados | 508 ok · 32 injured · 11 doubt · 4 unknown · 2 sanctioned · 1 discarded |
| Partes médicos (`statusInfo`) | 45 — coincide exactamente con los 45 no-ok |
| Sin equipo | 26 |

### Incógnita abierta 🔶

**El campo `update` apunta al futuro.** En la primera captura marcaba +20 h.
No sabemos si es el próximo recálculo programado o si lleva un offset. **No usar
`source_updated_at` como detector de payload rancio** hasta aclararlo. Se
resuelve solo con unos días de datos:

```sql
SELECT as_of, source_updated_at,
       source_updated_at - LAG(source_updated_at) OVER (ORDER BY as_of) AS delta
FROM raw_payload ORDER BY as_of;
```

### Pendiente en esta fase

- Cron a hora fija.
- Contract tests contra `samples/payload.json` que avisen si cambia la forma.

---

## 5. Fase 2 — Motor determinista (pendiente)

- **Proyección de puntos** por jugador y jornada. Estadística, sin IA.
- **Optimización de plantilla** bajo restricciones: presupuesto, deuda, límite
  de jugadores, posiciones. Es una mochila con extras — programación dinámica o
  fuerza bruta acotada. En C#, con tests.

### Filtros de esta fase (no de la ingesta)

- **Entrenadores fuera.** La liga se juega sin entrenador, solo jugadores. Se
  excluye `position = 5` en proyección y optimización. Se siguen capturando: son
  20 filas al día y si la configuración de la liga cambiara, el histórico está.
- **Jugadores sin equipo fuera del universo de fichajes.** No aparecen en el
  mercado, así que no son candidatos a comprar. Como *tenencia* sí importan —
  ver la alerta de salida de la liga en la fase 3.

### Complicaciones que confirma el payload

**`altPositions`**: hay jugadores elegibles en más de una demarcación, así que
la asignación de posición es *parte de la decisión*, no un dato fijo.

La distribución de precios está muy sesgada (media 2,3M, máximo 25,7M). Ahí
vive lo interesante: la métrica útil es puntos esperados por euro, y el valor
está en la cola larga, no en las estrellas.

**La calidad del asistente la marca esta fase, no el modelo.** Con una
proyección mala, el agente venderá recomendaciones malas con excelente prosa.

Es la fase más larga (~2 semanas) y la que no enseña nada de IA. Ver la decisión
abierta más abajo.

---

## 6. Fase 3 — Capa de agente (pendiente, es el objetivo)

- Los cálculos de la fase 2 expuestos como **tools**. Con unas seis basta para
  las tres preguntas reales: plantilla actual, precio y tendencia, proyección,
  combinaciones de venta, restricciones de formación, alineación óptima.
- El modelo encadena llamadas: *"si vendo a X, ¿me llega para Y sin quedarme en
  negativo?"*, *"¿por qué me recomiendas vender a Z?"*.
- **Ingesta de texto no estructurado**: los 45 `statusInfo` del payload son
  exactamente esto — *"Lesión muscular. Retorno estimado: Principios de
  Septiembre"*. Los 11 `doubt` son el caso de uso canónico: ni disponible ni
  descartado, y la decisión depende de información que no está en ninguna
  columna numérica.
- **Evals con feedback real**: predicción → jornada → comparación.
- **Versionar prompts y definiciones de tools desde el principio.** Comparar
  resultados entre ejecuciones no sirve de nada sin saber qué versión produjo
  cada uno. Barato ahora, caro de retrofitear.
- Lo difícil y lo que se aprende: qué hacer cuando el modelo llama mal a una
  tool, se inventa parámetros o hay que pararle los pies.

### Alerta de salida de la liga ⚡

Si un jugador de la plantilla propia ficha por un equipo extranjero, hay que
**venderlo cuanto antes**: su precio se hunde y deja de puntuar. Cada día de
retraso cuesta dinero.

Es detectable porque `player_daily` guarda el `team_id` de *cada día* — la
dimensión `player` es SCD tipo 1 y sobrescribe, así que el histórico solo vive
en la tabla de hechos:

```sql
SELECT p.name, d.as_of, prev.team_id AS equipo_anterior
FROM player_daily d
JOIN player p USING (player_id)
JOIN player_daily prev
  ON prev.player_id = d.player_id AND prev.as_of = d.as_of - 1
WHERE d.team_id IS NULL AND prev.team_id IS NOT NULL;
```

Encaja como tool de fase 3 (*"¿hay algo urgente en mi plantilla?"*) y como
comprobación diaria del propio job. Requiere al menos dos días de capturas
consecutivas: un motivo más para el cron.

### Interfaz elegida

**Servidor MCP en C#.** En vez de construir un frontend de chat, se expone el
motor como servidor MCP y se conecta a Claude Desktop — cliente, historial y UI
gratis. Como proyecto enseñable en entrevista suena mejor que otro chatbot.

---

## 7. Decisión abierta 🔶

**¿Orden lógico o invertido?**

- **Lógico:** ingesta → motor → agente. Se toca IA dentro de dos o tres semanas.
- **Invertido:** montar el agente ya contra tools mockeadas, validar el bucle
  completo de tool calling esta semana, y luego sustituir mocks por
  implementaciones reales.

Matiz sobre el invertido: no es peor ingeniería. Definir las tools primero y
darles una implementación falsa es diseño por contrato — fijar firmas, tipos de
retorno y errores antes de comprometerse con la implementación. Lo que sí sería
peor ingeniería es *quedarse* en los mocks.

Precaución si se toma ese camino: **que los mocks devuelvan datos con forma
realista, no constantes**. Una plantilla plausible, un caso de deuda, un caso
sin solución factible, y los errores desde el principio (*no hay solución dentro
del presupuesto*, *jugador inexistente*). Con mocks que devuelven `42` se
diseñan firmas que el optimizador real no puede cumplir.

El cron de ingesta es independiente y arranca igual.

---

## 8. Siguientes pasos inmediatos

1. ~~Levantar Postgres, aplicar `schema.sql`, ejecutar la ingesta una vez.~~ ✅
2. ~~Verificar el mapeo contra `raw_payload` y corregir los DTOs.~~ ✅
3. **Poner el job en cron a hora fija.**
4. Resolver la decisión del punto 7.
5. Contract tests contra `samples/payload.json`.

---

## 9. Perfil de quien lo construye

Backend senior .NET, ~10 años: microservicios, APIs REST, MongoDB, GCP/Azure,
Azure DevOps, gRPC, Azure Service Bus. Ya montó un e-commerce con DDD/CQRS/Clean
Architecture desplegado en Azure con CI/CD.

Aprende **construyendo proyectos, no con cursos** — validado con el e-commerce.
No hacen falta explicaciones de fundamentos de C#, inyección de dependencias,
SQL ni arquitectura. Sí hacen falta en: RAG, embeddings, vector stores,
evaluación de sistemas no deterministas y diseño de agentes.

Horizonte: crecer hacia Technical Lead, y tener algo demostrable para el próximo
proceso de selección.
