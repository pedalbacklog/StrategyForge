# Coral for Windows — plan de puesta en marcha (v1)

> Este documento es el resultado de analizar el código actual de `StrategyForge/`
> (la app macOS) y de `windows/README.md` (el scaffold ya existente) para proponer
> un plan concreto: stack, requisitos, orden de trabajo y revisión de seguridad.
> No sustituye a `windows/README.md` — lo desarrolla.
>
> **Stack confirmado** (§1) — el resto del plan asume esa elección. Progreso real
> por fase en §6; `windows/README.md` → "Status" tiene el estado más al día
> (qué hay portado, cuántos tests, qué falta) porque se actualiza en cada commit.

## 0. Qué NO cambia

- **Cero código compartido.** La app macOS es SwiftUI/AppKit; solo compila en
  Apple. El puerto a Windows es una implementación *desde cero*, en su propio
  stack, viviendo enteramente bajo `windows/` (ya establecido en el README de
  esa carpeta).
- **Lo que sí se comparte es el contrato de datos**, no el código:
  `models.json` y `skills.json` (raíz del repo) y los invariantes de
  `README.md` ("How multi-agent works here": un solo nivel de delegación, el
  modelo del orquestador es el de la sesión, los subagentes fijan modelo en
  frontmatter, el verificador es de solo lectura). Cualquier cambio de forma en
  esos JSON debe revisarse contra cómo los parsea `StrategyForge/Models/` **y**
  contra cómo los parseará el cliente Windows.
- **El gate de macOS no se toca.** `tests.yml` y `xcodebuild test` siguen siendo
  el gate de `StrategyForge/`; este trabajo no debe tocar esos archivos salvo
  para el propio `windows-tests.yml`.

## 1. Decisión de stack — recomendación (✅ confirmada)

`windows/README.md` dejaba el stack abierto: WinUI 3, MAUI o C++/WinRT. Mi
recomendación, confirmada por el founder:

**WinUI 3 + .NET 8 (C#), MVVM con `CommunityToolkit.Mvvm`, empaquetado MSIX
(con fallback a instalador firmado sin MSIX si el Store/sideload da fricción).**

| Opción | A favor | En contra | Veredicto |
|---|---|---|---|
| **WinUI 3 + .NET** | Framework nativo de Microsoft para Windows moderno; Fluent Design de fábrica; MVVM maduro (`CommunityToolkit.Mvvm`, muy parecido en espíritu a `@Observable`); `System.Diagnostics.Process` + `System.Text.Json` cubren exactamente lo que hoy hace `ClaudeRunner`/`ProviderRun` (spawn de subprocesos + streaming NDJSON); Visual Studio de primera clase; MSIX da instalación/actualización/firma con buen soporte de SmartScreen | Solo Windows (no es problema: es justo el objetivo) | **Elegido.** Es el equivalente más directo a "SwiftUI nativo en macOS" |
| MAUI | Cross-platform (Windows/Android/iOS/Mac) | Ese cross-platform no aporta nada aquí — no hay plan de iOS/Android para Coral, y el backend Windows de MAUI *es* WinUI 3 por debajo, con una capa de abstracción extra que solo añade fricción y bugs de "leaky abstraction" sin beneficio | Descartado |
| C++/WinRT | Máximo control, mínima huella | Coral no es CPU-bound (es I/O: parseo de streams NDJSON, spawn de procesos, UI) — pagar la velocidad de desarrollo de C++ sin necesitarla no se justifica | Descartado |
| Avalonia | Verdaderamente cross-platform, no depende de Microsoft, buen soporte de theming | No estaba en las tres opciones que planteaba el scaffold; añade una dependencia de terceros grande para un target que hoy es solo Windows | Anotado como alternativa si en el futuro se quisiera Linux, pero no v1 |

Puntos concretos que hacen de WinUI 3 un buen encaje mirando el código real:

- `Services/ClaudeRunner.swift` y `Services/ProviderRun.swift` spawean
  `claude`/`codex`/`gemini` como subprocesos y parsean
  `--output-format stream-json` línea a línea (NDJSON). Eso es
  `System.Diagnostics.Process` + `System.Text.Json.JsonDocument` leyendo
  `StandardOutput` línea a línea — sin gimmicks de plataforma.
- `ProviderInstaller.swift` usa `openpty` (Darwin) para capturar el login OAuth
  de la CLI en un pseudo-terminal oculto. El equivalente Windows es la
  **Pseudo Console API (ConPTY)** — bien soportada desde Windows 10 1809+, hay
  wrappers .NET (`Microsoft.Windows.ConPTY` / P/Invoke directo a
  `CreatePseudoConsole`).
- Los `Generators/*.swift` (generan `.claude/agents/*.md`, `CLAUDE.md`,
  `loop.sh`) son lógica pura de texto — se traducen casi 1:1 a C#, con
  cobertura de test igual de directa.
- `AuroraBackground.swift`, `WowShaders.metal`, `ParticleField.swift` (efectos
  visuales macOS/Metal) no tienen equivalente directo; en WinUI 3 se
  rehacen con **Win2D** o **Composition APIs** — o se recortan del alcance v1
  (ver sección "No-objetivos").

## 2. Inventario del código macOS (qué implica portar cada capa)

| Capa | Archivos | Naturaleza | Portabilidad |
|---|---|---|---|
| `Models/` | 25 | Structs/enums Codable, sin dependencias macOS (salvo detalles puntuales) | Alta — se traduce a `record`/POCO C# casi 1:1 |
| `Generators/` | 16 | Texto puro (genera archivos), sin UI ni red | Alta — la lógica es portable, buena candidata a portar primero con tests |
| `Services/` | ~50 | El grueso del trabajo: spawn de procesos, Keychain, OAuth, CloudKit, git, HTTP | Media — mismo *propósito*, implementación distinta por servicio (ver mapa §4) |
| `ViewModels/` | 8 | `@Observable @MainActor`, orquestan Services | Alta en diseño, se reescribe en `ObservableObject`/`[ObservableProperty]` |
| `Views/` | ~60 | SwiftUI puro | Ninguna — se rehacen en XAML/WinUI desde cero, pantalla por pantalla |
| `StrategyForgeTests/` | 165 tests / 31 suites | `Testing` framework, mayormente sobre Generators/Services puros | Alta para los tests de lógica pura; los de integración necesitan fakes nuevos |

## 3. Mapa de equivalencias macOS → Windows

| Necesidad | macOS (hoy) | Windows (propuesto) |
|---|---|---|
| Secretos (token de cuenta, refresh token) | `Services/KeychainStore.swift` (Keychain, `kSecAttrAccessibleWhenUnlocked`) | **Windows Credential Manager** vía `CredentialManagement` o P/Invoke a `CredWrite`/`CredRead`; alternativa más simple: DPAPI (`ProtectedData.Protect`, `CurrentUser` scope) cifrando un blob en `%LOCALAPPDATA%` |
| Sign in with Apple | `AuthService.swift` (`AuthenticationServices`) | No existe en Windows. Recortar del alcance v1, o sustituir por **Microsoft Account** / mantener solo Google si de verdad hace falta login (ver "No-objetivos") |
| Google OAuth (PKCE) | `ASWebAuthenticationSession` | `System.Net.Http` + navegador del sistema + **loopback HTTP listener local** (`HttpListener` en `127.0.0.1:<puerto>`) como redirect URI, patrón estándar de OAuth "installed app" |
| Sync entre máquinas | CloudKit (`ConfigSyncStore.swift`) | Sin equivalente 1:1 en Windows. **Fuera de alcance v1** (ver más abajo) — o evaluar un backend propio más adelante, lo cual es una decisión de producto, no técnica |
| Spawn de CLIs (`claude`/`codex`/`gemini`) | `Process` (Foundation) sin shell, PATH aumentado a mano | `System.Diagnostics.Process` con `UseShellExecute = false`, `ArgumentList` (evita el shell — mismo motivo que macOS: no citar mal un argumento) |
| Captura de login interactivo (paste de código OAuth) | `openpty` (Darwin) | ConPTY (`CreatePseudoConsole`) — hay wrappers .NET maduros |
| Streaming NDJSON del CLI | `FileHandle` + parseo línea a línea | `Process.StandardOutput` (`StreamReader.ReadLineAsync`) + `System.Text.Json` |
| Directorio de datos de la app | `~/Library/Application Support/Coral` | `%LOCALAPPDATA%\Coral` (o `%APPDATA%` si se quiere roaming — decisión: local, igual que macOS que no usa iCloud Drive para esto) |
| Crash/hang diagnostics | MetricKit (local, exportable, sin subida) | **Windows Error Reporting (WER)** local + un log propio a `%LOCALAPPDATA%\Coral\Logs`, misma política: nada se sube automáticamente |
| Firma + confianza de la distribución | Developer ID + notarización (Gatekeeper) | **Authenticode** (certificado de firma de código) + reputación SmartScreen; considerar un **certificado EV** para minimizar el "Windows protegió tu PC" en los primeros lanzamientos (SmartScreen construye reputación con el tiempo salvo EV) |
| Instalación de las CLIs (`npm install -g`) | `npm` resuelto en PATH, prefix `~/.npm-global` | Igual en espíritu: resolver `npm.cmd` en PATH, prefix de usuario (`%APPDATA%\npm` es ya el default global de npm en Windows — no hace falta el workaround de macOS) |
| Apple Events / control de Terminal | `com.apple.security.automation.apple-events` + `osascript` | No hace falta — en Windows todo el flujo de instalación/login puede vivir en el propio proceso vía ConPTY, sin depender de automatizar una terminal externa |
| Efectos visuales (Metal shaders, partículas) | `WowShaders.metal`, `ParticleField.swift` | Win2D / Composition, **o recortar del v1** (cosmético, no bloqueante) |

## 4. Requisitos

### Funcionales (paridad de producto, priorizado)

**P0 — MVP funcional (sin esto no es Coral, es un shell vacío)**
1. Chat con una estrategia/equipo, ejecutando `claude` headless y mostrando el
   activity panel en vivo (streaming NDJSON → eventos → UI).
2. Selección/edición de estrategias (los 13 templates), Advisor básico.
3. Instalación guiada + login de al menos **Claude Code** (el proveedor
   principal); Codex y Gemini pueden ir en P1 si el adapter tarda.
4. Generación de archivos (`.claude/agents/*.md`, `CLAUDE.md`) — portar
   `Generators/` casi 1:1, con sus tests.
5. Persistencia local (chats, equipos, config) en `%LOCALAPPDATA%\Coral`.
6. Uso de `models.json`/`skills.json` de la raíz del repo tal cual los
   consume hoy la app macOS (mismo esquema).

**P1 — antes de una beta pública en Windows**
7. Code mode (diff, git actions, commit/PR) — `CodeGit.swift`/`GitHubCLI.swift`
   equivalentes con `LibGit2Sharp` o invocando `git.exe` directamente (más
   simple, y coherente con "usa tus propias herramientas" que ya es la
   filosofía del proyecto).
8. Loops (turn/goal/time/proactive) — **igual que en macOS, esto necesita
   revisión humana del diff**, no solo tests en verde (ver §7 seguridad).
9. Multi-proveedor (Codex, Gemini) si no entró en P0.
10. Usage view (tokens/plan).

**P2 — paridad completa**
11. Arena, Map/CodeGraph, Sync entre dispositivos (si se decide implementar
    algo — ver "No-objetivos"), efectos visuales.

### No funcionales

- **Sin privilegios de administrador** para instalar ni ejecutar — MSIX o un
  instalador de usuario (`%LOCALAPPDATA%`), igual que macOS no pide elevación.
- **Arranque en frío rápido** (nativo, no Electron — es literalmente parte del
  posicionamiento del producto: "Native, not Electron").
- **Accesibilidad**: WinUI 3 trae soporte de Narrator/UIA razonable por
  defecto; no añadir controles custom que lo rompan sin revisar.
- **Sin telemetría por defecto**, opt-in y local si se implementa — mismo
  compromiso que ya está en `SECURITY.md` para macOS; no crear un backend de
  analytics nuevo "porque en Windows es más fácil".
- **Mismo allowlist de red** que macOS: endpoints propios de cada CLI,
  `api.github.com`, endpoints OAuth solo si el usuario inicia sesión. Nada
  más, y documentarlo igual de explícito que hoy en `SECURITY.md`.

## 5. Revisión de seguridad (Windows-específica)

Esto es lo más importante de este documento porque la app **spawea procesos y
maneja login flows** — exactamente el patrón que gatilla falsos positivos de
antivirus y es, a la vez, una superficie real si se implementa mal.

1. **Nunca uses el shell para spawear las CLIs.** `Process.Start` con
   `UseShellExecute = false` y `ArgumentList` (no una sola string
   concatenada) — el mismo motivo por el que macOS ya evita
   `Process`+shell: una ruta de repo o un prompt de usuario con comillas/`&`/`|`
   no debe poder inyectar comandos. Auditar esto explícitamente en el
   equivalente de `ClaudeRunner`/`ProviderRun` antes de mergear.
2. **PATH augmentado, no PATH reemplazado**, y nunca antepongas un directorio
   escribible por el usuario *antes* que las rutas del sistema si eso puede
   dar lugar a "PATH hijacking" (un binario malicioso llamado `git.exe` en un
   directorio de usuario que se ejecuta en vez del real). Resolver binarios
   por ruta completa cuando se pueda, igual que hace `resolveBinary()` hoy.
3. **Secretos**: Credential Manager/DPAPI, nunca en texto plano en
   `%LOCALAPPDATA%\Coral\data.json` ni en logs. Igual que hoy KeychainStore
   separa el token de auth del resto de la config sincronizable.
4. **Firma de código (Authenticode) desde el primer build distribuible.** Un
   .exe sin firmar que spawea subprocesos y hace red es el perfil exacto que
   Windows Defender SmartScreen bloquea agresivamente. Sin firma, cualquier
   beta temprana va a generar fricción y desconfianza justificada del
   usuario — priorizar esto igual de alto que "release.yml" lo fue para
   macOS (ítem P0 #2 del backlog).
5. **Actualizaciones (equivalente a `UpdateChecker.swift`)**: si el updater
   descarga un instalador, **verificar su firma Authenticode antes de
   ejecutarlo**, no solo el HTTPS del transporte — un TLS interception local
   (proxy corporativo, malware) no debe poder colar un binario no firmado por
   ti como si fuera una actualización legítima.
6. **ConPTY para login OAuth de las CLIs**: el código que lee lo que la CLI
   imprime en el pseudo-terminal no debe ejecutar nada de lo que lee como
   comando — es solo texto a mostrar/capturar (el código de auth), igual que
   `LoginInput.submit()` hoy solo escribe una línea a stdin, no interpreta.
7. **Sin sandbox forzado por el SO** (Windows no tiene un equivalente directo
   al App Sandbox de macOS para este patrón), así que la disciplina recae en
   el código: documentar en el `SECURITY.md` de Windows exactamente qué
   procesos puede lanzar la app y qué hosts toca, igual de explícito que ya
   lo hace el `SECURITY.md` raíz. Evaluar **MSIX + AppContainer** como
   opción de *defensa en profundidad* si no complica demasiado el spawn de
   subprocesos (algunas capacidades de AppContainer restringen lanzar
   procesos fuera del paquete — hay que probarlo temprano, no asumirlo).
8. **Supply chain de las CLIs instaladas vía npm**: mismo mecanismo que ya
   existe en macOS (`AIProvider.pinnedCLIVersions`) — pinear versión por
   release en vez de resolver siempre `@latest`, y en Windows además
   considerar que `npm install -g` en un prefix de usuario es más seguro que
   escribir en `Program Files` (privilegios).
9. **Datos que cruzan la frontera de confianza**: el output de las CLIs
   (incluido lo que un modelo "dice" que hizo) se parsea como **datos**, no
   como comandos — el parser NDJSON debe seguir siendo puro/tolerante como
   hoy (`ClaudeStreamParser`), nunca `eval`-ar ni ejecutar nada de lo que
   llega por stdout salvo a través de las rutas explícitas ya validadas
   (tool_use conocidos).
10. **Reporting de vulnerabilidades**: extender la política ya existente en
    `SECURITY.md` (contacto, no issues públicos) al repo/carpeta Windows sin
    crear un proceso paralelo — un único punto de contacto para todo Coral.

## 6. Orden de trabajo (fases)

```
Fase 0 — Decisión y setup            ✅ hecha (esta doc + confirmación del stack)
Fase 1 — Scaffolding                 ✅ hecha: Coral.sln, Coral (WinUI3, unpackaged),
                                      Coral.Core, Coral.Tests, windows-tests.yml real
Fase 2 — Núcleo portable             ✅ cerrada del todo — MissionReport.
                                      AgentLines() (el último cabo suelto)
                                      portado junto a AgentNameMatcher
Fase 3 — Runner de procesos          ✅ cerrada — ClaudeRunner.Stream() y la
                                      primitiva ConPTY confirmados en Windows
                                      real (ver §10); ambos encontraron y
                                      arreglaron bugs reales en el camino
                                      (formato culture-sensitive; DLL_INIT_
                                      FAILED por indirección de más en
                                      UpdateProcThreadAttribute)
Fase 4 — Secretos + auth             🔶 ProviderAuth.cs portada (login-freshness
                                      de claude/codex/gemini, ver §6); Credential
                                      Manager/DPAPI + Google OAuth deliberadamente
                                      diferidos — solo sirven para CloudKit sync,
                                      no-objetivo de v1 (ver §6/§8)
Fase 5 — Chat MVP                    ✅ cerrada — confirmado funcionando en
                                      Windows real de punta a punta: prompt
                                      real, streaming en vivo, UTF-8 correcto
                                      (tildes/ñ), coste bien formateado, panel
                                      de Activity poblándose con pasos reales,
                                      scroll automático, texto seleccionable,
                                      y Stop cancelando un turno en curso.
                                      Cinco bugs reales encontrados y
                                      arreglados en el camino (ver §10) — todo
                                      el trabajo de UI de este alcance mínimo
                                      quedó verificado por el founder, no solo
                                      revisado o compilado
                                      (ver §10)
Fase 6 — Instalación de CLIs         🔶 ProviderInstaller.cs + ConnectViewModel.cs
                                      + botón "Connect Claude" en MainPage,
                                      todo portado/construido, probado con
                                      fakes, y CONFIRMADO compilando en
                                      windows-latest CI (ver §10); AÚN sin
                                      correr contra npm/claude reales en
                                      Windows — lo único que falta para
                                      cerrar esta fase
Fase 7 — Code mode                   🔶 Capa de servicio completa + UI real
                                      (git panel, diff viewer, flujo de PR,
                                      selector de repo) — CONFIRMADO
                                      compilando en windows-latest CI (ver
                                      §10); falta terminal y verificación
                                      visual real en Windows
Fase 8 — Loops                       ⚠️ requiere revisión humana del diff, igual que
                                      en macOS — no se merge solo con CI en verde
Fase 9 — Empaquetado                 MSIX, firma Authenticode, updater con
                                      verificación de firma
Fase 10 — Beta                       paridad P1 cerrada, docs de Windows en
                                      CONTRIBUTING/README actualizados
```

**Fase 2, detalle** (Models/ + Generators/ → `Coral.Core`, traducidos con tests,
la capa de más ROI porque es lógica pura sin UI):
- ✅ `Services/ModelCatalog.swift` → `ModelCatalog.cs`
- ✅ `Models/AgentRole.swift`, `Models/Strategy.swift` (+ `Validate()`/`IsValid`
  completo) → `AgentRole.cs`, `Strategy.cs`
- ✅ `Models/StrategyLibrary.swift` (los 15 templates) → `StrategyLibrary.cs`
- ✅ `Generators/AgentFileGenerator.swift`, `ClaudeMdGenerator.swift`,
  `LaunchCommandGenerator.swift`, `FileDiff.swift`/`GeneratedFile.swift` →
  puertos 1:1 en `Coral.Core/Generators/`
- ✅ `Models/EvalSuite.swift`, `Models/ToolCheck.swift` → `EvalSuite.cs`,
  `ToolCheck.cs` (solo la lógica pura: scoring/gate de `EvalRun`, regresión,
  y el motor de aserciones `ToolCheckEngine`; el juez que produce un `EvalRun`
  real y el runner que ejecuta un `ToolCheck` de verdad son `Services/`, no
  portados — spawnean procesos o llaman al modelo)
- ✅ `Generators/WorkflowGenerator.swift` → `WorkflowGenerator.cs` (topología del
  equipo → programa `.mjs` ejecutable: fan-out por rol o fan-out por ítem según
  el equipo, fases Plan/Work/Verify/Synthesize, escaping JS)
- ✅ `Generators/McpConfigGenerator.swift` → `McpConfigGenerator.cs` (`.mcp.json`
  con merge preservando servidores del usuario, `.gemini/settings.json`
  reutilizando el mismo merge, `.codex/config.toml` sin merge)
- ✅ `Generators/StrategyWriter.swift` → `StrategyWriter.cs` — primer puerto que
  toca disco de verdad (`System.IO`): escribe subagentes + CLAUDE.md fusionado +
  semillas de memoria + workflow dinámico + `.mcp.json`/Gemini/Codex, podando
  SOLO los archivos con firma gestionada que ya no corresponden a la estrategia
  actual (nunca toca archivos escritos a mano)
- ✅ `Strategy.autoFixed()`/`hasAutoFixableIssues` → `Strategy.AutoFixed()`/
  `HasAutoFixableIssues` (más `AgentRole.Clone()`, nuevo, para la semántica de
  copia que el struct de Swift tenía gratis)
- ✅ `Generators/CostEstimationHooks.swift` → `CostEstimationHooks.cs`
  (`StrategyCost`, `CostEffort`, `CostEstimator`) + tabla de precios/constantes
  de `Constants.swift` → `Constants.cs`
- ✅ `Generators/MissionReport.swift` → `MissionReport.cs` completo —
  `Headline`/`Markdown` (puros) y ahora también `AgentLines(strategy:,
  timeline:)`, que deriva estadísticas por agente del timeline de actividad:
  el orquestador se queda con los pasos sin delegar (`Agent == null`), cada
  subagente hace match laxo por nombre vía `AgentNameMatcher.TitlesMatch`
  (nuevo, `Services/AgentNameMatcher.cs`). Necesitaba que `ActivityStep`
  (`Coral.Core.ViewModels`, creado en Fase 5) tuviera los campos
  `IsDelegation`/`Agent` — no existían en el corte mínimo de Fase 5, así que
  se añadieron ahí también (con valores por defecto, sin romper nada
  existente) y `ChatViewModel` ahora rastrea qué subagente está activo
  (`_activeSubagent`, reseteado al empezar cada turno) para atribuir cada
  paso correctamente, igual que el original Swift. 11 tests nuevos
  (`AgentNameMatcherTests`, `MissionReportTests`, y un caso nuevo en
  `ChatViewModelTests` para la atribución) — 160 en total.
- ⬜ Todo lo demás bajo `Services/` distinto de `ModelCatalog` empieza a pisar
  Fase 3 (spawn de procesos, APIs solo-Windows) — no cuenta como Fase 2

**Fase 3, detalle** (`Services/ClaudeRunner.swift` y afines → `Coral.Core`,
empezando por lo que es puro y testeable sin spawnear nada):
- ✅ `ChatEvent`/`AgentTodo`/`ClaudeStreamParser.events(from:)` →
  `ChatEvent.cs`/`ClaudeStreamParser.cs` (`Coral.Core/Services/`) — parser
  puro y tolerante de las líneas NDJSON de `claude --output-format
  stream-json` (texto, tool_use, tool_result, todos, resultado final,
  denegaciones de permisos, uso de tokens). `ChatEvent` es un `record`
  abstracto con un caso `sealed record` anidado por variante (constructor
  privado en la base → jerarquía cerrada, el equivalente más directo en C#
  al enum de Swift con valores asociados). Nunca lanza excepción ante JSON
  malformado/adversarial — mismo contrato "tolerante" que el original.
- ✅ `ClaudeRunner.resolveBinary()`/`which()` → `BinaryResolver.cs` — **no** es
  un puerto 1:1 (el original invoca un shell de login interactivo `/bin/zsh
  -ilc` para cargar `~/.zshrc`/nvm/Homebrew, sin equivalente en Windows; los
  binarios instalados vía npm en Windows son shims `.cmd`, y las rutas
  conocidas son otras — ver el mapa macOS→Windows en §3). Diseño nuevo con la
  misma forma: ruta absoluta configurada → búsqueda en `PATH` → fallback a
  `%APPDATA%\npm` por nombre de hoja, probando `.cmd`/`.exe`/`.bat` en ese
  orden. `ResolveUncached` es puro (recibe `PATH`/home/probe de ejecutable
  como parámetros vía `IExecutableProbe`), así que el ALGORITMO se prueba sin
  tocar disco real ni depender de qué SO corre `dotnet test`; solo
  `Resolve()` (la capa fina que lee el `PATH`/home reales) queda sin
  verificar hasta correr en Windows de verdad
- ✅ La construcción de argumentos de `stream()` (líneas 267-281 del original)
  → `ClaudeRunArgs.Build()` — puro (dados los inputs, siempre la misma lista
  de argumentos en el mismo orden), así que se prueba sin spawnear nada;
  exactamente lo que el `Process.Start(ArgumentList)` real va a necesitar
- ✅ La parte pura de `Services/ProviderRun.swift`'s `CLIOneShotRunner` (el
  runner "one-shot" multi-proveedor que usa `MetaOrchestrator` — no portado
  todavía) → `CLIOneShotRunner.cs`: `Command()` (argv por proveedor —
  Claude/Codex/Gemini, cada uno con sus propias flags), `StripAnsi()`/
  `ProgressLine()` (limpia el output crudo de Codex/Gemini, que no tienen
  stream estructurado, para mostrar "qué está haciendo ahora"),
  `IsAuthPrompt()`/`IsAntigravityMigration()`/`AuthFailureMessage()`
  (detección de fallos de login por texto), `EstimateTokens()`/
  `EstimatedCostUsd()` (estimación aproximada cuando la CLI no reporta uso
  real). `OneShotResult`/`OneShotError`/`OneShotEvent` y `parseClaudeJSON()`
  **no** se portan todavía — ningún test los ejercita de forma aislada hoy y
  solo tienen sentido junto al runner real (Fase 3's spawn) o a
  `MetaOrchestrator` (fuera de alcance); añadirlos cuando algo los consuma.
- ✅ El spawn real → `ClaudeRunner.cs`/`IProcessLauncher.cs`/
  `RealProcessLauncher.cs`. **Ya corrió contra `claude` real en Windows real**
  (`ManualClaudeRunnerSmokeTest`, ver §10 más abajo) — pasó a la primera:
  `BinaryResolver` encontró `claude` en el PATH, el spawn sin shell funcionó,
  el stream se parseó bien de punta a punta. Diseño:
  - `IProcessLauncher`/`IChildProcess`: la única frontera no pura (spawn de
    verdad, sin shell — `ArgumentList`, nunca una string concatenada).
    `RealProcessLauncher` es un wrapper fino sobre
    `System.Diagnostics.Process`. Gracias a esta interfaz, TODA la orquestación
    de `ClaudeRunner.Stream()` (orden de streaming, watchdog, cancelación,
    manejo de exit code) se prueba con un `FakeProcessLauncher` — sin
    spawnear nada real — igual que `BinaryResolver`.
  - El watchdog de inactividad de Swift (`InactivityWatchdog`, un
    `DispatchSourceTimer` con polling manual) **no** se porta como clase
    propia: `CancellationTokenSource.CancelAfter()` ya reinicia su propio
    plazo en cada llamada, así que rearmarlo en cada línea es más simple y
    exacto que portar el polling — una simplificación deliberada, no un
    corte de esquina.
  - `System.IO.StreamReader.ReadLineAsync` reemplaza el `LineBuffer` a mano
    de Swift (bufferiza líneas de forma nativa); no hace falta portarlo.
  - **Sí verificado de verdad, aunque no contra `claude`:**
    `RealProcessLauncherTests` spawnea el propio `dotnet` (garantizado
    presente donde se compile Coral) como smoke test — prueba que el spawn
    sin shell, la lectura async de stdout línea a línea, el exit code, la
    captura de stderr, `Kill()` y las mutaciones de entorno funcionan de
    verdad en este entorno, no solo que compilan. Lo único que falta
    verificar es el comportamiento específico de `claude`/`codex`/`gemini`
    en Windows real (rutas, shims `.cmd`, el formato exacto de su stream) —
    exactamente lo que el founder va a probar en su máquina.
- ✅ ConPTY (la PRIMITIVA de pseudo-consola, no el flujo de login completo) →
  `IPseudoConsoleLauncher.cs`/`Win32PseudoConsoleLauncher.cs`. **Confirmado
  pasando en Windows real (2026-08-06)** tras encontrar y arreglar un bug
  real (`STATUS_DLL_INIT_FAILED`, ver §10) — a diferencia de
  `RealProcessLauncher` (que sí se pudo smoke-testear de verdad en Linux
  spawneando `dotnet`, porque `System.Diagnostics.Process` es
  multiplataforma), ConPTY es P/Invoke crudo contra `kernel32` sin
  equivalente en Linux, así que esta pieza no se pudo probar en absoluto
  fuera de Windows hasta ahora. Mismo patrón de interfaz que
  `IProcessLauncher` (`IPseudoConsoleLauncher`/`IPseudoConsoleSession`), para
  que la lógica de más alto nivel de un futuro flujo de login (leer una URL
  del output, escribir un código de vuelta) sea testeable con un fake más
  adelante. Secuencia Win32 estándar (la misma que la muestra oficial de
  Microsoft): dos pipes (entrada/salida de la consola) →
  `CreatePseudoConsole` → `STARTUPINFOEX` con el pseudo-console como
  proc-thread attribute → `CreateProcess`. Incluye construcción manual de la
  línea de comandos (reglas de escapado de Win32, ya que `CreateProcess` no
  tiene equivalente a `ArgumentList`) y del bloque de entorno (ordenado, como
  exige `CREATE_UNICODE_ENVIRONMENT`).
  - `ManualPseudoConsoleSmokeTest.cs` (Category=Manual, igual que el de
    `ClaudeRunner`): spawnea `cmd.exe /c echo hello-from-conpty` a través de
    la pseudo-consola de verdad y verifica tanto el texto capturado como el
    exit code. **Confirmado pasando en Windows real** (§10).
- ⬜ El flujo de login completo por proveedor (instalar CLI vía npm, navegar
  el TUI de Gemini, detectar migración a Antigravity, escribir el código de
  auth) — eso es `Services/ProviderInstaller.swift` completo, y es trabajo de
  Fase 6 ("Instalación de CLIs"), no de Fase 3. Fase 3 solo necesitaba la
  PRIMITIVA de pseudo-consola para existir; ya existe.

**Fase 4 (secretos + auth), primera pieza: `ProviderAuth.cs`** —
✅ portada y probada (11 tests nuevos, todos locales/temp-dir, sin ninguna
dependencia de Windows real). Puerto directo de `Services/ProviderAuth.swift`:
comprobación barata de si el login guardado de `claude`/`codex`/`gemini` sigue
vivo, leyendo solo sus ficheros de credenciales en disco (nunca Credential
Manager/Keychain, a propósito — tocarlo arrancaría un prompt del SO al
inicio). Mismo patrón `XUncached(...)` testeable + `X()` real que
`BinaryResolver`: `FreshnessUncached(provider, homeDirectory)` es pura y se
prueba contra un directorio temporal que hace de `%USERPROFILE%`;
`Freshness(provider)`/`VerifyAsync(providers)` son el wiring fino contra el
`%USERPROFILE%` real.

El resto de Fase 4 (`KeychainStore` → Credential Manager, `Account`/
`AuthProviderKind`, el flujo Google OAuth 2.0 + PKCE con loopback HTTP) queda
deliberadamente diferido: en macOS ese flujo solo sirve para activar el sync
de CloudKit, que ya es un no-objetivo explícito de Windows v1 (§8), y el
`googleClientID` en `Constants.swift` es un placeholder sin configurar
todavía incluso en macOS — construirlo ahora sería andamiaje dormido e
imposible de verificar de extremo a extremo en cualquier plataforma. Se
retoma cuando haya un backend/CloudKit-equivalente real que lo necesite, o si
surge otra razón de producto para tener una identidad de usuario en Windows.

- 🔶 **Fase 5 (Chat MVP), primer corte — `ChatViewModel.cs` + `MainPage.xaml`.**
  Puerto MÍNIMO de `ViewModels/ChatViewModel.swift` (1535 líneas en macOS):
  solo el camino `-p` de un único proveedor — sin modo "Ask" (permisos en
  vivo), sin `MetaOrchestrator` multi-proveedor, sin historial de turnos
  persistido; cada uno de esos es su propia función grande, fuera de alcance
  aquí a propósito. `ChatMessage`/`ActivityStep`/`ChatViewModel` viven en
  `Coral.Core` (no en el proyecto WinUI3): son POCOs + `ObservableCollection`/
  `INotifyPropertyChanged` hecho a mano (sin CommunityToolkit.Mvvm — la
  superficie que hacía falta era mínima y así el ViewModel queda testeable en
  cualquier plataforma, mismo patrón que el resto de este port), con
  `IProcessLauncher` inyectado igual que `ClaudeRunner`. Réplica fiel de la
  lógica de dedup delta/texto-completo del original (`gotDelta`/
  `separatorPending`) y del reintento cuando `--resume` apunta a una sesión
  que ya no existe (`sessionMissing`) — comportamiento real ya verificado en
  el Swift, no una reinvención. 10 tests nuevos contra `FakeProcessLauncher`
  (streaming, dedup, activity, formato de coste en `InvariantCulture`,
  reintento de sesión, cancelación).

  `MainPage.xaml`/`.xaml.cs` (proyecto `Coral`, WinUI3) es la primera UI
  real: caja de prompt, transcript, panel de actividad, botones Send/Stop.
  **El primer intento SÍ falló al compilar en `windows-latest` CI** — la
  primera versión ponía los `x:Bind` directamente en `MainWindow.xaml`, y
  WinUI3's `Window` NO es un `FrameworkElement` (a diferencia de `Page` en
  UWP/WinUI), así que el código generado por el compilador de XAML no
  compilaba (`CS1503: cannot convert from 'Coral.MainWindow' to
  'Microsoft.UI.Xaml.FrameworkElement'`). Arreglado moviendo TODO el
  contenido/bindings a una `Page` nueva (`MainPage.xaml`) que `MainWindow`
  aloja como su `Content` — el patrón estándar de WinUI3 para esto.
  `MainWindow` queda como un shell mínimo sin bindings propios. Este bug
  real, encontrado por CI y no por revisión de código, es la prueba de por
  qué esta fase no se puede dar por cerrada solo con una lectura cuidadosa:
  hacía falta una compilación real contra el Windows App SDK para
  encontrarlo. Repasado a mano con cuidado extra en lo demás (orden de
  `InitializeComponent()` vs. asignar `ViewModel`/`RepoPath` antes para que
  los `x:Bind` en modo `OneTime` no capturen `null`; `UpdateSourceTrigger=
  PropertyChanged` en el `TextBox` para que Enviar no lea texto obsoleto; un
  `Border` en vez de `Grid.Padding` para no depender de una propiedad de la
  que no tenía certeza en esta versión del SDK), pero sigue siendo código
  sin verificación visual real — no dar Fase 5 por cerrada hasta que el
  founder la vea correr en Windows.

  Sin selector de repo (usa `%USERPROFILE%` por defecto) ni ajustes de
  modelo/esfuerzo/permission-mode (valores fijos razonables) — eso es
  trabajo de seguimiento, no de este corte mínimo.

- 🔶 **Fase 6 (Instalación de CLIs) — `ProviderInstaller.cs`, primer corte.**
  Puerto de `Services/ProviderInstaller.swift` (361 líneas), con el mismo
  criterio de alcance deliberado que Fase 4:
  - ✅ Portado: `InstallEvent`/`ConnectEvent` (jerarquía cerrada de `record`s,
    mismo patrón que `ChatEvent`), `LoginInput`, `FirstUrl()` (extractor de URL
    puro — quita códigos ANSI, recorta puntuación al final, mismo regex que el
    original), `Install()` (spawna `npm install -g <spec>` vía
    `IProcessLauncher`, streaming línea a línea — **sin** el workaround de
    `--prefix` que hace falta en macOS: el prefix global por defecto de npm en
    Windows, `%APPDATA%\npm`, ya es escribible por el usuario, así lo documenta
    `BinaryResolver.cs`), `SignIn()` (reutiliza `IPseudoConsoleLauncher` — la
    contraparte Windows exacta del `openpty` de Swift — para correr el login de
    cada CLI en una pseudo-consola oculta, detectar la URL de login, disparar
    `NeedsCode` para Claude, detectar la migración a Antigravity de Gemini, y
    el nudge-y-detección-por-mtime del TUI de Gemini que no sale solo al
    autenticar con éxito), y `Connect()` (el flujo unificado: instala si hace
    falta, luego firma). `AIProvider.cs` ganó `BinaryName()`,
    `AlternativeBinaries()`, `NpmPackage()`, `PinnedCliVersions`,
    `NpmInstallSpec()`, `LoginCommand()`, `LoginNeedsTerminal()` (esta última
    siempre `false`, igual que en el Swift actual — el comentario del propio
    original dice que ya no hace falta terminal visible para ningún login).
  - `SignIn()` usa un `Channel<InstallEvent>` en vez de un iterador simple: a
    diferencia de `Install()`/`ClaudeRunner.Stream()` (un solo productor
    secuencial), aquí hay DOS productores concurrentes — el bucle normal de
    lectura-hasta-exit-code, y (solo Gemini) una tarea de fondo que compite
    detectando el cambio de mtime del fichero de credenciales. Es el
    equivalente C# más directo al patrón de Swift (una `AsyncStream` cuya
    `continuation` recibe `yield` desde varios closures a la vez). Un guard con
    `Interlocked.CompareExchange` asegura que solo el PRIMERO de los dos en
    llegar escribe el evento terminal — una pequeña mejora deliberada sobre el
    original, que en esa misma carrera puede llegar a emitir dos eventos
    terminales seguidos (éxito del watcher, luego un fallo espurio del
    `terminationHandler` al matar el proceso ya "ganado").
  - ⬜ Diferido a propósito, cada uno por ser un rediseño de plataforma real y
    no una traducción mecánica: `installNode()` (bootstrap de Node vía
    Homebrew) — Windows no tiene un equivalente único de confianza verificado
    en este port (winget necesita una máquina Windows real para comprobar
    semántica de elevación y disponibilidad de paquete), así que por ahora cae
    al mismo estado terminal que usa el propio Swift cuando Homebrew no está:
    mandar al usuario a `NodeDownloadUrl` (`nodejs.org/en/download`);
    `launchSignIn` (fallback AppleScript + Terminal.app) — no se porta porque
    ya es código muerto inalcanzable en el propio Swift hoy (`loginNeedsTerminal`
    es `false` siempre); abrir el navegador con la URL de login — Swift lo hace
    él mismo (`NSWorkspace.shared.open`), pero `Coral.Core` se mantiene sin
    dependencia de WinUI (mismo motivo que `ChatViewModel` no toca navegación),
    así que este puerto expone la URL como evento (`ConnectEvent.Url`) para que
    una futura capa de ViewModel/View la abra con la API de Windows que
    corresponda — ver el siguiente punto, ya construida.
  - 28 tests nuevos contra fakes (`FakePseudoConsoleLauncher`/
    `FakePseudoConsoleSession`, nuevo, mismo patrón que `FakeProcessLauncher`):
    extracción de URL (incluyendo el caso con códigos ANSI), instalación
    feliz/fallida/sin Node, sign-in con detección de URL y `NeedsCode`,
    fallo por exit code, detección de migración a Antigravity, éxito de Gemini
    por cambio de mtime, y el flujo `Connect` completo (salta instalación si
    la CLI ya existe, instala primero si falta, reenvía el evento de URL) —
    188 en total.
  - ✅ **`ConnectViewModel.cs` (`Coral.Core.ViewModels`), la capa de UI que
    faltaba** — mismo patrón que `ChatViewModel`: consume
    `ProviderInstaller.Connect()`, expone `Phase`/`PhaseLabel`/`LogLines`/
    `NeedsCode`/`CodeInput`/`StatusMessage` como propiedades observables,
    `ConnectAsync()`/`SubmitCodeAsync()`/`CancelConnect()` como comandos, y
    abre el navegador vía un delegado inyectable (`Action<string>? openUrl`,
    por defecto `Process.Start(UseShellExecute: true)`) — el mismo patrón de
    "inyecta el efecto de lado real, pruébalo con un fake" que
    `IProcessLauncher`/`resolveBinary`. 7 tests nuevos, 195 en total.
    `MainPage.xaml` gana un botón "Connect Claude" con un `Flyout` (fase de
    instalación/log/estado/campo para pegar el código), disparado por el
    evento `Opened` del propio `Flyout`. Construido con cuidado extra por la
    lección de Fase 5 (el converter `BoolToVisibilityConverter` nuevo vive en
    `Page.Resources`, no anidado) — pero, igual que toda la UI de Fase 5 antes
    de que el founder la corriera, **esto compila (a falta de confirmar en CI)
    pero no está verificado en Windows real todavía**: ni el `Flyout` con
    contenido `x:Bind` (patrón nuevo en este proyecto, aunque el mismo
    mecanismo ya funciona para los `DataTemplate` de `ChatList`/Activity), ni
    — sobre todo — el flujo de instalación/login real contra `npm`/`claude
    auth login` de verdad.
  - ✅ **`ManualProviderInstallerSmokeTest.cs` (nuevo, Category=Manual)** — la
    herramienta para esa última verificación pendiente, escrita mientras CI
    estaba caído (incidencia real de GitHub Actions, ver §10) para no
    quedarse parado esperando. Dos piezas independientes por el riesgo tan
    distinto que tienen: `InstallsTheClaudeCliForReal` corre `npm install -g
    @anthropic-ai/claude-code` de verdad (seguro de correr sin supervisión —
    `npm install -g` es idempotente) y comprueba que termina en `Finished`;
    `SignsInForReal` corre `claude auth login --claudeai` de verdad contra
    una pseudo-consola oculta — esto dispara un OAuth de navegador real y, si
    se completa, **reemplaza el login de Claude ya guardado en la máquina**,
    así que queda detrás de un opt-in explícito (`CORAL_MANUAL_RUN_SIGNIN=1`;
    sin él, se salta con un aviso incluso si el test se selecciona por
    nombre) y es interactivo (lee el código pegado del navegador por stdin
    cuando `ProviderInstaller` pide `NeedsCode`). Aún sin correr en Windows
    real — ver windows/README.md "Testing the pieces that need a real
    Windows machine" para cómo lanzarlo.

- 🔶 **Fase 7 (Code mode), primera pieza — `CodeGit.cs`.** Puerto de
  `Services/CodeGit.swift` (503 líneas), con el mismo criterio de alcance
  deliberado que Fases 4/6:
  - ✅ Portado: los parsers puros (`Parse` — diff unificado → líneas
    renderizables con numeración old/new; `ParseChangedFiles` — `git diff
    --numstat` + `git status --porcelain -z` → lista de archivos cambiados,
    tolerante a paths con espacios/no-ASCII vía separación NUL; `ParseShortstat`;
    `RepoName` — infiere el nombre de carpeta de una URL de clone) y las
    operaciones de SOLO LECTURA contra git real (`DiffAsync`,
    `CurrentBranchAsync`, `BranchStatAsync`, `ChangedFilesAsync`,
    `HasUncommittedChangesAsync`), todas vía el `IProcessLauncher` que YA
    existe — git es un subproceso "one-shot" normal, sin necesitar ninguna
    primitiva nueva de Windows (a diferencia de ConPTY en Fase 3). Un detalle
    de diseño que sí cambia respecto al original: Swift fusiona stdout+stderr
    en un solo pipe para evitar que un comando con mucha salida de error
    bloquee el proceso por buffer lleno; `IProcessLauncher` los mantiene
    separados (más idiomático en .NET), así que `RunGitAsync` drena ambos EN
    PARALELO (no stdout-y-luego-stderr) para conseguir la misma garantía sin
    fusionar pipes. También se deja caer `GIT_ASKPASS=/usr/bin/true` (ruta
    solo-macOS sin equivalente portable) y se mantiene `GIT_TERMINAL_PROMPT=0`
    + `GCM_INTERACTIVE=never` (este último, más relevante en Windows que en
    macOS: Git for Windows trae Git Credential Manager instalado por
    defecto, y sin este flag su prompt gráfico podría bloquear el proceso).
  - ✅ **Ampliado el mismo día**: también las operaciones de ESCRITURA del
    panel de git sobre un repo YA existente (`StageAsync`/`UnstageAsync`/
    `RevertAsync`/`StagedFilesAsync`/`CommitAsync`/`CommitStagedAsync`/
    `PushAsync`/`CreateBranchAsync`/`BranchesAsync`/`CheckoutAsync`) —
    revisando el razonamiento inicial ("sin UI que las consuma, quedarían
    inverificables"), resultó no sostenerse: un fake prueba la construcción
    de argumentos y el manejo de exit code exactamente igual de bien que para
    las operaciones de lectura, tengan o no consumidor todavía. `CombineOutput`
    aproxima el pipe único fusionado de Swift (stdout+stderr) para los
    mensajes de error que la UI mostrará más adelante, sin pretender
    reproducir el interleaving exacto (imposible con dos streams separados).
  - ⬜ Sigue diferido, y cada uno por una razón real, no por orden de llegada:
    `fullDiff` (el diff completo con archivos untracked incluidos y límite de
    tamaño por archivo — solo tiene sentido junto a un revisor automático de
    diffs, que no está portado todavía, y su forma exacta no está decidida);
    `clone` (ciclo de vida del repo: nombrado/deduplicado de carpeta local,
    creación de directorio — forma de código bastante distinta al resto, ya
    que no hay `-C repo` posible antes de que el repo exista; mejor construirlo
    junto al flujo real de "añadir un repo" que lo vaya a usar, no
    especulativamente); y TODAS las operaciones de WORKTREE (`addWorktree`/
    `mergeNoFF`/`commitAll`/`removeWorktree`/`deleteBranch`) — existen solo
    para aislar loops, la zona explícitamente vetada de Fase 8 (CLAUDE.md:
    los cambios de loop necesitan lectura humana del diff, no solo tests) —
    esto es un límite firme, no una cuestión de agenda.
  - 25 tests nuevos: el caso exacto de `ChatTests.swift`
    (`diffParserTagsAddsRemovesAndNumbers`) como especificación para `Parse`,
    ruido de cabecera de `diff --git` descartado, clasificación de archivos
    modified/added/deleted/untracked/renamed (incluyendo que un rename
    consume su registro de "old path" sin generar un sexto archivo fantasma),
    `ParseShortstat` con y sin coincidencia, `RepoName` con varias formas de
    URL (incluyendo un caso donde el orden real de Swift — comprobar `.git`
    ANTES de recortar la barra final — da un resultado distinto al que
    parecería "más limpio"; documentado como fidelidad al original, no un
    bug), las cinco operaciones de lectura y las diez de escritura, todas
    contra un `FakeProcessLauncher` — 220 en total.
  - Escrito mientras CI estaba caído por la incidencia de GitHub Actions (ver
    §10) — no depende de Windows para nada de esto, así que no hacía falta
    esperar.

- ✅ **`GitPanelViewModel.cs` (`Coral.Core.ViewModels`) — el panel de git
  entero de Code Mode, sin GitHub/PR/terminal.** `CodeModeView.swift` guarda
  su estado como `@State` directamente en la View (norma de SwiftUI), no en
  una clase ViewModel separada, así que no hay un tipo Swift 1:1 que traducir
  — diseño nuevo con la misma forma que `ChatViewModel`/`ConnectViewModel` ya
  establecieron para este port (vive en `Coral.Core`, sin dependencia de
  WinUI, `IProcessLauncher` inyectado). Envuelve exactamente lo que `CodeGit`
  ya expone: lista de archivos cambiados, diff del archivo seleccionado,
  stage/unstage/revert por archivo, commit (todo o solo lo staged), push, y
  crear/cambiar de rama — cada acción con su propio `IsBusy`/`StatusMessage`
  y, cuando aplica, un `RefreshAsync()` automático después. Deliberadamente
  fuera: la integración con GitHub (`GitHubCLI.swift` envuelve el CLI `gh`,
  no portado en absoluto todavía), Auto-PR, y el panel de terminal — cada uno
  es su propia pieza sin servicio C# detrás, así que cablear estado de UI
  para ellos ahora sería especulativo. Tampoco carga el contenido de archivo
  para el modo "file" (no-diff) de `CodeModeView.swift` — este ViewModel es
  el panel de git específicamente, no todo el workspace de Code Mode.
  10 tests nuevos contra fakes (refresh que puebla archivos/rama/selección,
  carga de diff al seleccionar, stage↔unstage del mismo archivo, revert con
  refresh automático, commit con mensaje en blanco como no-op, commit con
  éxito/fallo, push, crear rama con nombre en blanco como no-op, checkout con
  refresh) — 230 en total.

- ✅ **`GitHubCLI.cs` — el flujo de PR de un tap con `gh`.** Puerto de
  `Services/GitHubCLI.swift` (201 líneas), mismo criterio de alcance
  deliberado que el resto de Fase 7:
  - ✅ Portado: `IsInstalled`, `IsAuthenticatedAsync`, `CreatePRAsync` (+
    `LastHttpsLine`, puro — extrae la URL de la PR de la última línea de
    `gh` que empieza por `https://`, igual que el `.last(where:)` de Swift),
    `PrInfoAsync` (+ `ParsePRInfo`, puro y tolerante con `System.Text.Json`
    — nunca lanza excepción ante JSON malformado o incompleto, mismo
    contrato que el `as?` de Swift), y `MergePRAsync`. De paso, se extrajo
    `OneShotProcess.cs` (nuevo, `internal`) con el runner "lanza, drena
    stdout/stderr EN PARALELO, espera el exit code" que `CodeGit.RunGitAsync`
    ya tenía — duplicado casi textual entre `git` y `gh`, así que se
    refactorizó `CodeGit` para reutilizarlo (reverificado: los 35 tests de
    `CodeGit`/`GitPanelViewModel` siguen en verde exactamente igual tras el
    refactor).
  - ⬜ Diferido a propósito: `listRepos`/`RepoRef` y `createRepo` (alimentan
    un selector/lanzador de repos de GitHub que no existe todavía en este
    port — el selector de repo sigue siendo trabajo pendiente de Fase 5 —
    portarlos ahora sería especulativo); `searchCommunitySkills`/
    `RemoteSkill` (área de producto totalmente distinta — catálogo de
    skills, no Code Mode — con lógica bastante más compleja: varias
    llamadas a la API encadenadas con límite, ranking por estrellas —
    merece su propio pase con alcance propio, no un puerto de paso junto al
    flujo de PR).
  - 17 tests nuevos: `LastHttpsLine` (URL al final, varias URLs — se queda
    con la ÚLTIMA en cualquier posición, no solo si la última línea
    coincide; sin URL), `ParsePRInfo` (payload completo, campos opcionales
    ausentes con sus valores por defecto, JSON malformado/incompleto → null
    en los tres casos), y las cuatro operaciones reales contra un
    `FakeProcessLauncher` — 247 en total.
  - ✅ **Ampliado el mismo día, otra vez revisando el propio razonamiento
    inicial**: `CodeGit.CloneAsync` (antes diferido con "forma de código
    distinta, sin repo existente donde correr" — pero resulta que
    `createDirectory`/`pathExists` inyectables lo hacen igual de testeable
    con un fake que todo lo demás, así que no había razón real para
    dejarlo fuera) y, en `GitHubCLI.cs`, `ListReposAsync`/`RepoRef` +
    `CreateRepoAsync` (antes diferidos con "sin selector de repo que los
    consuma" — mismo razonamiento que ya se abandonó para las operaciones
    de escritura de `CodeGit`). Ambos usan el mismo patrón de deduplicación
    de carpeta (`base`, `base-2`, `base-3`, …) que ya tenía `RepoName`.
    11 tests nuevos (clone con/sin carpeta ocupada, git no encontrado;
    `ParseRepoList` con entradas sin `nameWithOwner` descartadas y valores
    por defecto rellenados, JSON malformado → lista vacía; listRepos/
    createRepo reales contra fakes) — 258 en total. Sigue diferido, y
    ahora por una razón más nítida: `searchCommunitySkills` (área de
    producto distinta, lógica bastante más compleja) y todo lo de
    worktrees (límite firme de Fase 8).

- 🔶 **Primera UI real de Fase 7 — `CodeModePage.xaml`/`.xaml.cs` (proyecto
  `Coral`), en `CodeModeWindow` propia.** Con toda la capa de servicio de
  `CodeGit`/`GitPanelViewModel` ya lista, tocaba construir algo que la
  ejercite de verdad — igual que Fase 6 necesitó `ConnectViewModel` +
  botón en `MainPage` para que `ProviderInstaller` dejara de ser código sin
  consumidor. Panel de git a la izquierda (rama actual + selector +
  "New branch" en un `Flyout`, lista de archivos cambiados con
  Stage/Revert por archivo, caja de mensaje de commit + botones Commit/Push)
  y visor de diff a la derecha (glyph +/-/@@  por línea vía
  `DiffLineKindToGlyphConverter`, nuevo, en `Page.Resources` — no anidado,
  la lección de Fase 5). Deliberadamente en una **ventana separada**
  (`CodeModeWindow`, mismo patrón shell-delgado que `MainWindow`/`MainPage`)
  en vez de embebida en `MainPage`, para que este código nuevo — sin
  verificar en Windows real ni una vez — no pueda arriesgar la UI de Fase
  5/6 que el founder ya confirmó funcionando de punta a punta. `MainPage`
  gana solo un botón "Code Mode" que abre la ventana.
  Deliberadamente NO en esta UI (ver el doc comment de `GitPanelViewModel`):
  Auto-PR, panel de terminal, ni cargar el contenido crudo (no-diff) de un
  archivo.
  **Sin verificar en Windows real todavía** — escrito con la misma
  disciplina de las fases anteriores (patrones ya confirmados: converters en
  `Page.Resources`, `ViewModel` asignado antes de `InitializeComponent()`,
  `UpdateSourceTrigger=PropertyChanged` en el `TextBox` de commit) pero,
  igual que toda la UI de Fase 5 antes de que el founder la corriera, eso no
  sustituye una compilación real contra el Windows App SDK ni una
  verificación visual.
- ✅ **`PullRequestViewModel.cs` — el flujo de PR de un tap, en la UI.**
  Ampliando `CodeModePage` el mismo día: ViewModel nuevo y deliberadamente
  SEPARADO de `GitPanelViewModel` (misma separación que `ConnectViewModel`/
  `ChatViewModel` en `MainPage` — un ViewModel, una responsabilidad), que
  envuelve `GitHubCLI.PrInfoAsync`/`CreatePRAsync`/`MergePRAsync`. El estado
  de la rama no vive aquí — lo pasa `GitPanelViewModel` en cada llamada, ya
  que ese sigue siendo el dueño de qué rama está activa. En `CodeModePage`,
  un botón "Pull Request" con `Flyout` (mismo patrón que "Connect Claude" en
  `MainPage`): título/estado del PR si existe, campos de título/descripción,
  botones Create PR/Merge, mensaje de estado — `x:Bind` con rutas anidadas
  que pueden ser null (`PullRequestViewModel.Info.Title`) confía en que
  x:Bind genera sus propios null-checks automáticamente en cada segmento de
  la ruta, a diferencia de `{Binding}` clásico — comportamiento documentado
  de WinUI3, no una suposición nueva de este port.
  7 tests nuevos contra fakes (refresh con/sin PR existente, create como
  no-op con título en blanco, create con éxito que limpia título/descripción
  y refresca, create con fallo que conserva el título, merge con
  éxito/fallo) — 265 en total.

- ✅ **`RepoPickerViewModel.cs` + botón "Open Repo" en `MainPage`** — el
  "selector de repo" que quedaba pendiente desde Fase 5 (`PORT-PLAN.md`
  llevaba varias entradas diciendo "sin selector de repo todavía"). Une
  `GitHubCLI.ListReposAsync` (navegar los repos del usuario), `CodeGit.
  CloneAsync` (clonar por URL) y `GitHubCLI.CreateRepoAsync` (crear uno
  nuevo) en un solo ViewModel, con `createDirectory`/`pathExists`
  inyectables igual que los servicios que envuelve, así que el camino
  feliz de clone/create es tan testeable como el resto. Deliberadamente
  NO intenta cambiar el repo activo de una sesión de chat/Code Mode ya
  abierta — al clonar o crear con éxito, abre una `MainWindow` NUEVA
  apuntando al path resultante, en vez de mutar el `RepoPath`/`ViewModel`
  de la página actual (que son `OneTime`-bound a propósito, ver Fase 5) —
  más simple y de menor riesgo que hacerlos reasignables en caliente.
  8 tests nuevos — 273 en total.

**Hito: primer run de CI de verdad, con la incidencia de GitHub ya resuelta
(2026-08-07, ~05:26 UTC).** El run para el commit `465085f` pasó completo,
incluyendo por primera vez el paso "Build the WinUI 3 app (Coral)" — hasta
ahora esta rama había acumulado 11 commits sin que CI llegara nunca a
compilar el proyecto WinUI3 de verdad, por la incidencia de horas de GitHub
Actions (ver más abajo en esta sección). El único fallo que sí llegó a
producirse en un run real (`23b2e0a`, antes de este) no era del código de
hoy: `ClaudeRunnerTests.ActivityResetsTheWatchdogSoALongQuietRunSurvives`
resultó ser un test con un margen de tiempo demasiado ajustado (40ms de
delay contra un watchdog de 100ms, 2.5x) que ya había fallado una vez en
este mismo sandbox bajo carga — confirmado no relacionado con nada portado
en esta sesión (la lógica del watchdog de `ClaudeRunner` no se tocó).
Arreglado ensanchando el margen a 20x (20ms/400ms), verificado con 5
corridas locales seguidas antes de repushear. Con esto, TODO el trabajo
acumulado de Fase 6/7 de hoy —`ProviderInstaller`, `ConnectViewModel`, el
flyout "Connect Claude", `CodeGit`, `GitHubCLI`, `GitPanelViewModel`,
`PullRequestViewModel`, `CodeModePage`/`CodeModeWindow`— está confirmado
compilando de verdad contra el Windows App SDK, no solo en teoría.

273 xUnit tests automatizados en `Coral.Tests` a día de hoy (todos pasando,
verificados con `dotnet test` real en este entorno además de en
`windows-latest`) más 4 tests manuales (Category=Manual, excluidos del CI):
los dos de Fase 3/5 **confirmados pasando en Windows real** (uno contra
`claude` real, el otro — ConPTY — contra un `cmd.exe` real adjunto a una
pseudo-consola real), y los dos nuevos de Fase 6
(`ManualProviderInstallerSmokeTest`) aún sin correr en Windows real.

Cada fase debería ser su propio PR (o pocos), contra `windows/**`, disparando
solo `windows-tests.yml` — nunca el gate de macOS.

## 7. Testing

- Framework: **xUnit** (equivalente directo al `Testing` de Swift que ya usa
  el proyecto — assertions y estructura similares).
- Los 165 tests de macOS no se "portan" literalmente (prueban Swift/SwiftUI),
  pero **la cobertura que importa sí**: los tests de `GeneratorTests`,
  `LoopGeneratorTests`, `ModelJSONTests`, `DiffTests`, `RepoMentionTests` etc.
  prueban lógica pura de texto/parsing que tiene un equivalente 1:1 en C# —
  usar esos casos como especificación al escribir los tests C#.
- `LoopRunnerTests`/`LoopStoreTests` (la zona vetada) necesitan el mismo nivel
  de escrutinio que en macOS: no fusionar sin lectura humana del diff.

## 8. No-objetivos para v1 (recortar, no fingir paridad)

- **CloudKit sync** — no hay equivalente directo en Windows sin diseñar un
  backend propio (decisión de producto, no de ingeniería; no improvisar algo
  a medias).
- **Sign in with Apple** — no existe en Windows; si se necesita alguna forma
  de login, evaluar solo Google/Microsoft, nunca simular Apple.
- **Efectos visuales Metal** (`WowShaders`, `ParticleField`, `AuroraBackground`)
  — cosméticos, recórtalos del v1 y hazlo explícito en vez de dejarlos rotos.
- **Microsoft Store** como único canal — evaluar más adelante; v1 puede
  distribuirse como instalador firmado directo, igual que macOS ships fuera
  de la Mac App Store hoy.

## 9. Siguiente paso concreto

Fases 0-1 hechas; **Fase 2 cerrada del todo** (detalle en §6): todo `Models/` y
`Generators/` está portado y probado, incluyendo el camino completo "Strategy →
archivos en disco" (`StrategyWriter`, contra un directorio temporal real), la
estimación de coste, y `MissionReport.AgentLines()` (el último cabo suelto,
resuelto junto con Fase 5 una vez `ChatViewModel`/`ActivityStep` ya existían).

**Fase 3 (runner de procesos) cerrada del todo.** Escrita, probada con
fakes, con smoke tests de proceso real, y **confirmada en Windows real
(§10)** tanto para el spawn normal (`ClaudeRunner.Stream()` contra `claude`
real) como para la primitiva ConPTY (`Win32PseudoConsoleLauncher` contra un
`cmd.exe` real adjunto a una pseudo-consola real) — todo el camino: parser
NDJSON (`ClaudeStreamParser`), resolución de binario/PATH (`BinaryResolver`),
construcción de argumentos (`ClaudeRunArgs`), spawn real
(`ClaudeRunner.Stream()`), y la primitiva de pseudo-consola (detalle en §6).
El flujo de login completo por proveedor que USA esta primitiva (instalar
CLI vía npm, navegar el TUI de Gemini, escribir el código de auth) es trabajo
de Fase 6, no de Fase 3 — Fase 3 solo necesitaba que la primitiva existiera y
funcionara de verdad, y ya está.

**Fase 4 (secretos + auth) en marcha, con alcance recortado a propósito**:
`ProviderAuth.cs` (login-freshness de claude/codex/gemini vía sus ficheros de
credenciales) portada y probada — 11 tests nuevos, 139 en total (detalle en
§6). El resto de la fase tal como estaba descrita originalmente (Credential
Manager, `Account`/`AuthProviderKind`, Google OAuth+PKCE con loopback) queda
diferido: solo existe en macOS para activar CloudKit sync, que ya es
no-objetivo de v1 (§8), y el client ID de Google ni siquiera está configurado
de verdad todavía en el propio macOS — construirlo ahora sería trabajo
dormido e inverificable en cualquier plataforma. Se retoma si/cuando surja
una razón de producto real para tener identidad de usuario en Windows.

**Fase 5 (Chat MVP) cerrada — confirmada en Windows real de punta a punta.**
`ChatViewModel.cs` (`Coral.Core`) probado de verdad (10 tests contra fakes);
`MainPage.xaml`/`.xaml.cs` (`Coral`, WinUI3) es la primera UI real del port,
y el founder la corrió de verdad: prompt real, streaming en vivo, UTF-8
correcto, panel de Activity con pasos reales, coste bien formateado, scroll
automático, texto seleccionable, y Stop cancelando un turno en curso. Cinco
bugs reales en el camino (compilación rota por `x:Bind` en `Window`, ventana
en blanco por un recurso mal ubicado, mojibake por no fijar UTF-8, texto no
seleccionable, scroll que no seguía el streaming) — detalle completo en §10.

**Fase 6 (Instalación de CLIs) en marcha — servicio + ViewModel + UI ya
construidos, sin verificar en Windows real todavía.** `ProviderInstaller.cs`
(instalación vía npm, sign-in headless reutilizando la primitiva ConPTY de
Fase 3, flujo `Connect` unificado) y `ConnectViewModel.cs` (la capa de UI que
lo consume — mismo patrón que `ChatViewModel`) escritos y probados con fakes
(35 tests nuevos entre ambos, 195 en total) — detalle en §6. `MainPage.xaml`
gana un botón "Connect Claude" con un panel de instalación/log/estado/código.
A diferencia de Fase 3/5, ningún test hasta ahora llamó a un `npm` real ni a
un `claude auth login` real esperando autenticación por una pseudo-consola
real, y la UI nueva (un `Flyout` con contenido `x:Bind`, patrón no usado
antes en este proyecto) tampoco se ha visto correr — eso es lo único que
falta para cerrar esta fase del todo. `ManualProviderInstallerSmokeTest.cs`
(nuevo, detalle en §6) ya existe para esa verificación en cuanto haya
oportunidad de correrla en Windows real.

## 10. Bitácora de verificación en Windows real

Esta sección registra qué se probó de verdad en una máquina Windows (no solo en
`windows-latest` o en este sandbox Linux), y qué salió de esas corridas — para
no perder la señal de "esto ya se verificó de verdad" a medida que el plan
crece.

**2026-08-06 — primera corrida de `ClaudeRunner.Stream()` contra `claude` real**
(`ManualClaudeRunnerSmokeTest`, ver `windows/README.md` "Testing against the
real `claude` CLI"): **pasó a la primera.** `BinaryResolver` resolvió `claude`
en el `PATH`, el spawn sin shell funcionó, y el stream se parseó de punta a
punta hasta `Finished`. Confirma que el diseño de Fase 3 (no solo el código)
es correcto para el caso feliz.

Esa misma corrida completa (suite completa + build de la app WinUI3 en Windows
real, no solo el smoke test) encontró un bug real que ni este sandbox ni
`windows-latest` podían atrapar: formato de `$`/tokens sensible a la cultura
del sistema operativo. `$"{costUsd:F2}"` usa `CultureInfo.CurrentCulture`, así
que en una máquina con locale español (coma decimal) `$0.83` se renderizaba
como `$0,83` — afectaba `MissionReport.cs`, `CostEstimationHooks.cs`, y el
propio output de diagnóstico del smoke test (7 sitios en total). Arreglado
forzando `CultureInfo.InvariantCulture` en los 7; `LocaleRegressionTests.cs`
(nuevo) fija `CurrentCulture` a `es-ES` dentro de cada test para que una
regresión futura se atrape aquí mismo, sin necesitar un runner de CI en un
locale no inglés.

**Lección para lo que sigue:** cualquier lógica portada que formatee números,
fechas o moneda para mostrar (no solo para JSON/máquina) necesita o bien
`InvariantCulture` explícito, o un test bajo una cultura no inglesa — no
asumir que "pasa en CI" cubre esto, porque ni este sandbox ni `windows-latest`
corren con una locale distinta a la inglesa por defecto.

**2026-08-06 — `ManualPseudoConsoleSmokeTest` (ConPTY): hang, luego
`STATUS_DLL_INIT_FAILED`, causa raíz encontrada por revisión de código.**
La primera corrida real en Windows colgó más de 3 minutos: el pipe de salida
de ConPTY nunca llega a EOF por sí solo cuando el proceso hijo termina —
conhost mantiene su extremo de escritura abierto hasta que se llama
`ClosePseudoConsole` explícitamente, con independencia de que el proceso
adjunto siga vivo. Arreglado con una tarea de fondo que espera la salida del
proceso, da 200ms de margen para drenar el búfer, y fuerza el cierre de la
pseudo-consola (de forma idempotente) para desbloquear la lectura pendiente.

Confirmado que el hang se arregló (232ms en vez de >3min), pero apareció un
fallo nuevo: `STATUS_DLL_INIT_FAILED` (0xC0000142) — `CreateProcess` tiene
éxito (PID válido), pero el proceso hijo (`cmd.exe`) muere durante su propio
arranque, sin producir ninguna línea de salida. Se descartaron por pruebas
directas del founder: sandboxing de herramientas (falla igual en una consola
PowerShell sin sandbox) y versión de Windows (build 10.0.26200.0, muy por
encima del mínimo de ConPTY). Un test de bisección temporal
(`ManualConPtyDiagnosticTest.cs`, borrar una vez cerrado esto) probó que
`STARTUPINFOEX` + lista de atributos + `CreateProcess` + bloque de entorno a
medida funcionan bien de forma aislada — acotando el bug a la parte
específica de ConPTY (`CreatePseudoConsole`/pipes/el atributo
`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`).

Con eso acotado, una relectura del código encontró la causa: en
`CreateAndInitializeAttributeListForPseudoConsole`,
`UpdateProcThreadAttribute` recibía como `lpValue` un puntero a un bloque de
heap que a su vez contenía el handle `hPc` — un nivel de indirección de más.
La muestra oficial de ConPTY de Microsoft (y cualquier otro binding correcto,
p.ej. el paquete Go de `hcsshim`) pasa el propio valor del handle `HPCON`
directamente como `lpValue`, no un puntero a una variable que lo contenga —
como `HPCON` ya ocupa el tamaño de un puntero, ese es el contrato exacto de
este atributo en particular. Con el nivel de indirección de más,
`CreateProcess` seguía teniendo éxito (la lista de atributos es
estructuralmente válida) pero el hijo fallaba al intentar adjuntar su E/S de
consola a través de una referencia de pseudo-consola corrupta — encaja
exactamente con `STATUS_DLL_INIT_FAILED`. Arreglado pasando `hPc` en vez de
un puntero nuevo; de paso desaparece el pequeño leak intencional de
`hPcPtr` que existía antes. Se añadió también un `Diagnostics` opcional
(`Action<string>?`) en `Win32PseudoConsoleLauncher`, cableado en el smoke
test, para tener trazas paso a paso si esta corrección no fuera suficiente.

**Confirmado en Windows real: `CreateProcess OK`, sin crash.** Pero apareció
un tercer síntoma, distinto del `DLL_INIT_FAILED`: el texto real de
`echo hello-from-conpty` nunca llegaba por el pipe de salida — solo la
negociación inicial de ConPTY (`ESC[?9001h ESC[?1004h`, win32-input-mode +
focus-tracking, 16 bytes) cruzaba el pipe y luego silencio total, mientras el
texto aparecía impreso directamente en la terminal real del founder, sin
abrir ninguna ventana nueva. Reproducido de forma idéntica con `cmd.exe` Y
`powershell.exe` (mismo patrón exacto de 16 bytes), descartando que fuera un
comportamiento específico de `cmd.exe`. Un volcado de bytes crudos
(bypaseando el line-splitting de `StreamReader`) confirmó que esos 16 bytes
son literalmente lo único que el pipe entrega, incluso esperando con calma
sin forzar el cierre de la pseudo-consola.

**Causa: no era un bug de este código.** Windows Terminal (que alojaba la
sesión de PowerShell del founder) usa ConPTY para alojar su propia shell; al
crear NOSOTROS una segunda ConPTY anidada dentro de un proceso que ya corre
bajo una ConPTY externa, conhost puede decidir "pasar por delante"
(passthrough/reparenting) el renderizado de la sesión interna directamente a
la terminal externa, en vez de relayarlo por nuestro pipe — dejando en el
pipe solo el hand-shake inicial. Confirmado repitiendo la misma prueba desde
la consola clásica (Win+R → `cmd`, sin Windows Terminal de por medio): ahí
"hello-from-conpty" sí llegó por el pipe con normalidad. Como la app real
nunca se lanza desde una terminal (se abre desde el Explorador, sin ningún
ConPTY por encima en el árbol de procesos), esto no la afecta — es un
artefacto exclusivo de probar manualmente `dotnet test` dentro de Windows
Terminal. Documentado como advertencia en el XML doc de
`ManualPseudoConsoleSmokeTest.cs` para que nadie pierda tiempo con esto otra
vez.

Todo el scaffolding temporal de esta investigación se retiró tras confirmar
la corrección: `ManualConPtyDiagnosticTest.cs` (bisección) borrado,
`ReadRawOutputForDiagnosticsAsync` (volcado de bytes crudos) retirado de
`Win32PseudoConsoleLauncher.cs`, `Win32PseudoConsoleSession` vuelto a
`internal`. El `Diagnostics` opcional (`Action<string>?`) se queda —
utilidad genuina y de coste cero cuando no se usa.

**Fase 3 cerrada del todo: `ManualPseudoConsoleSmokeTest` confirmado pasando
en Windows real (2026-08-06)**, con las dos aserciones completas (texto
capturado, exit code 0) — no solo "no crashea".

**2026-08-06 — primera corrida real de la UI de Fase 5, dos bugs reales
encontrados con el depurador de Visual Studio (no por revisión de código).**
El founder instaló Visual Studio por primera vez para esto. Primer bug:
`e.Message` = "Cannot find a resource with the given key: Negate." —
`COMException` lanzada desde `Bindings.Initialize()` generado por `x:Bind`,
que tumbaba la ventana entera antes de renderizar nada (de ahí la ventana en
blanco). Causa: `BoolNegationConverter` vivía en `Border.Resources`
(anidado dentro de la página), pero la búsqueda de conversores que genera
`x:Bind` (`LookupConverter`) no recorre el árbol visual como sí hace un
`{Binding}` normal — espera el recurso en `Page.Resources`. Arreglado
moviéndolo ahí.

Segundo bug, encontrado en la primera corrida real contra `claude` con un
prompt con tildes: la respuesta se veía como `Â¿QuÃ©... calorÃas...` — un
mojibake clásico de leer UTF-8 con la página de códigos equivocada.
`RealProcessLauncher` no fijaba `StandardOutputEncoding`/
`StandardErrorEncoding` en el `ProcessStartInfo`, así que .NET usaba la
página de códigos activa de la consola (no UTF-8) para decodificar la
salida de `claude` (que sí es UTF-8) — exactamente la misma familia de bug
que el de formato culture-sensitive de Fase 3, pero en codificación de
texto en vez de en formato numérico. Arreglado fijando
`Encoding.UTF8` explícitamente en ambos. Sin test automatizado de
regresión para esto (requeriría invocar un binario externo con salida UTF-8
no-ASCII conocida sin usar shell, desproporcionado para lo que es —
verificado en su lugar con una corrida real contra `claude` con un prompt
con tildes/ñ).

Con esto, la primera corrida real de principio a fin funcionó: prompt
enviado, streaming en vivo, coste mostrado (`$0.0657`, con punto decimal
correcto), sin mojibake. El founder confirmó a continuación: el panel de
Activity se puebla de verdad con pasos reales (probado con un prompt que
dispara herramientas — salieron `Glob`/`Glob`/`Read` con sus detalles) y el
botón Stop cancela un turno en curso (estado pasa a "Cancelled.", tal como
está programado).

**Cuarto y quinto bug, en el mismo hilo de pruebas:** el texto de las
respuestas no se podía seleccionar/copiar (`TextBlock.IsTextSelectionEnabled`
es `false` por defecto en WinUI3 — arreglado fijándolo a `true`), y el
transcript no bajaba solo mientras llegaba el streaming. El primer intento de
arreglar el scroll (`ChatList.ScrollIntoView(lastItem)` en cada
`PropertyChanged` del mensaje) solo bajaba hasta el borde superior del último
mensaje una vez, sin seguir el borde inferior mientras ese mismo mensaje
seguía creciendo — `ScrollIntoView` garantiza que un ítem sea visible, no que
la vista siga su borde inferior si crece. Arreglado buscando el
`ScrollViewer` real dentro del `ListView` (vía `VisualTreeHelper`, una vez,
en `Loaded`) y llamando `ChangeView(null, ScrollableHeight, null)` en cada
delta de texto — la forma fiable de fijar la vista al final de verdad.
Confirmado por el founder que ahora sí baja solo.

**Fase 5 (Chat MVP) cerrada.** Con las cinco correcciones de esta bitácora,
todas las interacciones del alcance mínimo quedaron confirmadas en Windows
real por el founder: enviar un prompt, streaming en vivo con texto correcto
(incluyendo tildes/ñ), panel de Activity con pasos reales, coste mostrado
bien formateado, scroll automático, texto seleccionable, y cancelación con
Stop. Sigue pendiente, deliberadamente fuera de este alcance mínimo: selector
de repo, ajustes de modelo/esfuerzo/permission-mode, modo "Ask" de permisos
en vivo, `MetaOrchestrator` multi-proveedor, historial de turnos persistido.

**2026-08-06 — incidencia real de GitHub Actions bloqueó la verificación de
CI de Fase 6 durante horas, no un problema del código.** Tras empujar
`983c022` (ConnectViewModel + UI), el run de `windows-tests.yml` se quedó en
`queued` sin que ningún runner lo recogiera (`runner_id: 0`) durante ~15
minutos, y GitHub lo canceló solo, marcando el run como `failure` aunque
ningún paso llegó a ejecutarse. Un rerun tuvo el mismo problema, esta vez sin
ni siquiera crear una entrada de job (`list_workflow_jobs` devolvía
`total_count: 0` durante más de una hora). Confirmado como incidencia real
(no conjetura): githubstatus.com reportó un incidente desde las 15:22 UTC
("workflow runs delayed or failing to start/complete, Actions REST API
errors, unexpected rate limiting"), todavía sin resolver a las 17:40 UTC
("rolling out a further fix across all affected systems"), lo bastante
grande como para que The Register publicara sobre ello. Mientras tanto se
siguió trabajando en local (`dotnet test`/`dotnet build` de `Coral.Core`/
`Coral.Tests` no dependen de GitHub en absoluto) — se escribió
`ManualProviderInstallerSmokeTest.cs` (detalle en §6) en ese hueco, en vez
de quedarse esperando sin avanzar. Lección: una incidencia de infraestructura
de GitHub no bloquea el desarrollo local, solo la verificación remota — hay
trabajo real que seguir haciendo mientras se resuelve.
