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
Fase 2 — Núcleo portable             ✅ esencialmente cerrada — ver detalle abajo
                                      (solo queda MissionReport.agentLines(),
                                      diferido a Fase 5 a propósito)
Fase 3 — Runner de procesos          🔶 en progreso — parseo NDJSON portado y
                                      probado (ver detalle abajo); falta el
                                      spawn de verdad (Process + PATH + ConPTY)
                                      — solo verificable en windows-latest
Fase 4 — Secretos + auth             Credential Manager/DPAPI, Google OAuth (loopback)
Fase 5 — Chat MVP                    ViewModel + XAML mínimo: enviar prompt, ver
                                      streaming, ver activity panel — esto es "P0"
Fase 6 — Instalación de CLIs         ProviderInstaller equivalente, probado contra
                                      claude/codex/gemini reales
Fase 7 — Code mode                   git/diff/PR
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
- 🔶 `Generators/MissionReport.swift` → `MissionReport.cs` — solo `Headline`/
  `Markdown` (puros); `agentLines(strategy:timeline:)` queda sin portar porque
  depende de `ActivityStep` (un tipo de ViewModel) y `AgentNameMatcher`
  (`Services/`), ninguno portado — se hará junto al runtime de chat/actividad
  (Fase 5)
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
- ⬜ `ClaudeRunner.resolveBinary()`/`which()`/PATH resolution — lógica
  mayormente pura (dado un `PATH` y un `FileManager`/`IO` fake se puede
  testear sin spawnear nada de verdad)
- ⬜ El spawn real (`Process.Start` sin shell, streaming de stdout línea a
  línea, `PermissionResponder`/`LaunchGate`/`InactivityWatchdog`) — esto SÍ
  necesita ejecutarse en Windows de verdad para probarse con confianza, así
  que solo se verifica en `windows-latest` (con un binario fake/echo, no
  `claude` real) — ver §5 (nunca usar shell, `ArgumentList` no una string)
- ⬜ ConPTY para captura de login OAuth — API solo-Windows, sin equivalente
  probable en Linux; documentar y portar cuando llegue Fase 6

88 xUnit tests en `Coral.Tests` a día de hoy (todos pasando, verificados con
`dotnet test` real en este entorno además de en `windows-latest`).

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

Fases 0-1 hechas; Fase 2 esencialmente cerrada (detalle en §6): todo `Models/` y
`Generators/` que es lógica pura (sin runtime de chat/actividad) está portado y
probado, incluyendo el camino completo "Strategy → archivos en disco"
(`StrategyWriter`, contra un directorio temporal real) y la estimación de coste.
Solo queda pendiente, y deliberadamente diferido:

1. `MissionReport.agentLines()` — depende de `ActivityStep`/`AgentNameMatcher`
   (runtime de chat), se porta junto a Fase 5.

Fase 3 (runner de procesos) ya empezó: el parser NDJSON puro
(`ClaudeStreamParser`) está portado y probado (detalle en §6). Lo que queda de
Fase 3, en orden:

1. `ClaudeRunner.resolveBinary()`/PATH resolution — todavía mayormente lógica
   pura (dado un PATH y una capa de IO fake), con tests.
2. El spawn real (`System.Diagnostics.Process`, sin shell, `ArgumentList`) +
   streaming de stdout línea a línea hacia `ClaudeStreamParser.Events()` — la
   primera vez que el puerto necesita ejecutarse en Windows de verdad para
   probarse con confianza (`windows-latest`, con un binario fake — no
   `claude` real todavía).
3. ConPTY para login OAuth — API solo-Windows, entra más adelante (Fase 6).
