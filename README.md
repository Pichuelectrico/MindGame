# MindGame

**Combate táctico 3D con IA local** · prototipo de investigación para categoría **CMP** (inteligencia artificial, endpoints y videojuegos).

Unity **6000.6.5f1** · **URP** · LLM local vía **Ollama** (`qwen2.5:7b`) · Input System.

---

## ¿Qué es MindGame?

MindGame es un videojuego de combate en tiempo real (no por turnos) donde un jefe (boss) puede tomar decisiones con un modelo de lenguaje local. El jugador pelea con controles simples equivalentes a un catálogo cerrado de acciones (golpear, esquivar, rodar, lanzar arma, lasers del boss, etc.).

El objetivo no es “un NPC que chatea”: es medir, en un entorno seguro y divertido, si un LLM puede sostener **decisiones críticas bajo presión temporal** frente a un humano.

Arquitectura en dos capas:

1. **Plan** — interpreta texto / rueda de cartas / respuesta del LLM y produce acciones estructuradas.
2. **Ejecución** — traduce esa cola a Animator, movimiento, colliders y física.

Flujo: `Input o LLM → PlanParser → ActionValidator → ActionQueue → ActionExecutor → Animator / Física`.

---

## Tesis de investigación (con telemetría real)

**Pregunta.** ¿Puede un LLM local ser la primera opción, por sí solo, para decidir cada acción de un agente en combate continuo?

**Hallazgo (octubre 2026).** El modelo **predice** planes útiles, pero **llega tarde** respecto al ciclo de combate. En telemetría de sesión del prototipo (`Logs/llm-decisions.jsonl`):

| Fuente de decisión | n (aprox.) | Latencia |
|---|---|---|
| LLM (inferencia local) | ≈ 46 | mediana ≈ **426 ms** · p90 ≈ **578 ms** |
| BotBrain (heurístico local) | ≈ 438 | ≈ **0 ms** (mismo tick) |

Estos valores son de sesión de desarrollo; **no sustituyen un banco formal**, pero ilustran el gap.

**Conclusión operativa.** A octubre 2026, la IA generativa **aún no es, por sí sola, la primera opción fiable** para eventos que exigen acción en tiempo real en videojuegos. El valor práctico aparece cuando el LLM elige **estrategia** a baja frecuencia y un **algoritmo** ejecuta la **táctica** a alta frecuencia (modo **Estratega**).

---

## Modos de juego

### Jugador

| Modo | Descripción |
|---|---|
| **Manual** | Movimiento y acciones inmediatas (teclado / ratón). |
| **Comandos** | Pausa lógica activa: plan por **texto** (string → acciones) y/o **rueda radial de cartas** (hasta 6 acciones). |
| **Estratégico** (boss) | Híbrido: `BotBrain` gestiona la táctica en tiempo real; el LLM elige la estrategia/persona (**Distance / Fighter / Normal**), con rotación acotada (consulta ~6 s; tope de retención ≤ 20 s). |

### Boss — variantes de decisión

| Modo | Comportamiento |
|---|---|
| **LLM directo** | Snapshot del combate → prompt → Ollama → texto → parser → `ActionExecutor`. |
| **Heurístico** | `BotBrain` local, sin red; línea base de latencia y fallback. |
| **Estratega** | LLM elige personalidad; algoritmo ejecuta la táctica cada tick (ver diagrama abajo). |

También: **Furia** del boss por debajo del ~30 % de vida; ataques **láser recto** y **láser tornado** con telegrafía; orbes de vida; log JSONL de decisiones en `Logs/llm-decisions.jsonl`.

---

## Arquitectura Estratega (diagrama)

Dos niveles con latencias muy distintas:

```
┌─────────────────────────────────────────────────────────────────┐
│  NIVEL ESTRATÉGICO  (lento · LLM cada ~6 s)                     │
│                                                                 │
│  Estado del combate ──► Prompt ──► Ollama qwen2.5:7b            │
│                              (HTTP local, ≈ 426 ms mediana)     │
│                                   │                             │
│                                   ▼                             │
│                     Persona vigente: Distance | Fighter | Normal│
└───────────────────────────────┬─────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│  NIVEL TÁCTICO  (rápido · BotBrain cada tick ≈ 0 ms)            │
│                                                                 │
│  Persona vigente ──► Heurística local ──► acción concreta       │
│       (perseguir / esquivar / laser / orbes / furia / …)        │
│                                   │                             │
│                                   ▼                             │
│                            ActionExecutor                       │
└─────────────────────────────────────────────────────────────────┘
```

**LLM directo** (para contraste): sensores → prompt → Ollama (~426 ms) → texto → parser → `ActionExecutor`. Ahí la inferencia es el cuello de botella en **cada** decisión.

---

## Requisitos

- **Unity Hub** + editor **6000.6.5f1** (URP; el proyecto ya trae paquetes en `Packages/`).
- **Ollama** (opcional, solo para modos LLM / Estratega) con el modelo `qwen2.5:7b`:

  ```bash
  ollama pull qwen2.5:7b
  ollama serve
  ```

- Endpoint HTTP OpenAI-compatible por defecto en local:

  ```text
  http://localhost:11434/v1/chat/completions
  ```

  Configurable en el asset `LlmConfig` (Inspector: `endpointUrl`, `modelName`, timeout/reintentos). **No subas API keys** al repositorio.

---

## Cómo ejecutar (paso a paso)

1. Clona el repositorio y ábrelo en Unity Hub (**Open** → carpeta raíz de este proyecto).
2. Espera a que Unity importe `Assets/` y compile (`Game.asmdef` + tests).
3. Abre la escena **`Assets/Scenes/Sandbox.unity`**.
4. (Opcional LLM) Arranca Ollama y asegúrate de que el `LlmConfig` del boss apunte a `http://localhost:11434/v1/chat/completions` con modelo `qwen2.5:7b`.
5. Pulsa **Play**.
6. Usa **Tab** para Manual/Comandos; **\\** para ciclar el modo del boss; observa el HUD de modo/persona.
7. Tras jugar, revisa telemetría en `Logs/llm-decisions.jsonl` (se crea al ejecutar).

> Si añades scripts `.cs` nuevos, en el editor usa **Assets → Refresh** antes de correr la suite: el recompile a ciegas puede no importar archivos nuevos y la suite correrá con el assembly viejo.

---

## Controles

Bindings reales del mapa **Player** en `Assets/InputSystem_Actions.inputactions` (teclado/ratón y gamepad):

| Acción | Teclado / ratón | Gamepad |
|---|---|---|
| **Move** | W · A · S · D · flechas | Stick izquierdo · D-pad |
| **Look** | Delta del puntero | Stick derecho |
| **Attack** | Clic izquierdo · Enter | Botón West · trigger (joystick) |
| **Interact** | E | Botón North |
| **Crouch** | C | Botón East |
| **Jump** | Espacio | Botón South |
| **Sprint** | Shift izquierdo | Click stick izquierdo |
| **Previous** | 1 | D-pad izquierda |
| **Next** | 2 | D-pad derecha |

También hay bindings de **XR** / joystick genérico en el mismo asset para Move, Look, Attack, Jump y Sprint. El mapa **UI** define Navigate (WASD/flechas/sticks), Submit, Cancel, Point, Click, ScrollWheel, etc.

---

## Estructura del proyecto

```text
.
├── Assets/
│   ├── Scenes/           # Sandbox.unity (juego), SampleScene.unity
│   ├── Scripts/
│   │   ├── AI/           # LlmBrain, BotBrain, BotController, LlmClient, DecisionLogger…
│   │   ├── Actions/      # Catálogo / definiciones de acciones
│   │   ├── Planning/     # Parser, validador, fallbacks
│   │   ├── Execution/    # ActionQueue, ActionExecutor, AnimatorDriver
│   │   ├── Combat/       # Boss lasers, grunts, health, spawner…
│   │   ├── GameMode/     # Manual / Comandos
│   │   ├── UI/           # Rueda, HUD de modo, health…
│   │   ├── Core/         # TimeController, GameOver…
│   │   ├── Camera/ · VFX/ · Debugging/
│   │   └── Game.asmdef
│   ├── Config/           # LlmConfig (endpoint configurable; sin secrets en Git)
│   ├── Data/             # ScriptableObjects de acciones
│   ├── Tests/EditMode/   # Suite EditMode (~233 tests)
│   └── InputSystem_Actions.inputactions
├── Packages/
├── ProjectSettings/
├── Docs/                 # Informe del proyecto para el jurado (PDF + HTML)
├── LICENSE               # MIT
└── Logs/, TestResults/   # telemetría y resultados de tests (locales, gitignored)
```

**Nota de assets de terceros:** el skybox usa el pack gratuito *SkySeries* (~374 MB), que no se incluye en el repo por peso y por su licencia de redistribución. Si tras clonar el cielo se ve con color plano, descarga el pack y colócalo en `Assets/SkySeries Freebie/`.

---

## Informe del proyecto

El informe técnico para el jurado está en **[`Docs/MindGame-Informe.pdf`](Docs/MindGame-Informe.pdf)** (3 páginas): el problema que aborda (IA y decisiones críticas en tiempo real), cómo funciona la arquitectura con diagramas, resultados actuales con telemetría real, y su relación con la categoría CMP. Fuente editable: [`Docs/MindGame-Informe.html`](Docs/MindGame-Informe.html).

---

## Cómo correr los tests EditMode (~233)

### Desde el Editor

1. Cierra cualquier otra instancia del mismo proyecto si vas a usar batchmode después.
2. **Window → General → Test Runner**.
3. Pestaña **EditMode** → **Run All**.
4. Si acabas de añadir `.cs` nuevos: **Assets → Refresh** y espera la recompilación antes de Run All.

### Desde línea de comandos (batchmode)

Con el editor **cerrado** (si existe `Temp/UnityLockfile`, ciérralo primero):

```bash
# Ajusta UNITY_BIN a tu instalación del editor 6000.6.5f1
UNITY_BIN="/Applications/Unity/Hub/Editor/6000.6.5f1/Unity.app/Contents/MacOS/Unity"

"$UNITY_BIN" -batchmode -quit \
  -projectPath "$(pwd)" \
  -runTests -testPlatform EditMode \
  -testResults "$(pwd)/TestResults/editmode.xml" \
  -logFile "$(pwd)/Logs/test-editmode.log"
```

La suite EditMode del proyecto está en `Assets/Tests/EditMode/` (`Game.Tests.asmdef`) y cubre parser, validador, BotBrain, LlmBrain, Estratega/variedad de personas, lasers, HUD de modo, cola de acciones, etc. La verificación reciente del equipo: **233/233** en verde.

---

## Integrantes

Joshua Reinoso

---

## Licencia

Distribuido bajo la licencia **MIT** — ver [LICENSE](LICENSE).
