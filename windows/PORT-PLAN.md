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
2. Selección/edición de estrategias (los 15 templates — el "13" original de
   esta lista estaba desactualizado; tanto macOS como el port ya tienen 15),
   Advisor básico. Selección de plantilla (Fase 1) y el motor del Advisor
   en su mitad determinista, enganchado a la UI (Fase 3) — ambos ✅, ver
   §10 (2026-08-11). Edición de estrategia (Fase 2) — 🔶 primer corte
   escrito (nombre/modelo/instancias/herramientas por rol + validación +
   Fix All), pendiente de CI y de prueba en Windows real (2026-08-21).
   Queda: el pulido de UI del Advisor con niveles Economy/Recommended/Max
   (Fase 4).
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
Fase 6 — Instalación de CLIs         ✅ ProviderInstaller.cs + ConnectViewModel.cs
                                      + botón "Connect Claude" en MainPage —
                                      CONFIRMADO de punta a punta en Windows
                                      real: npm install real, `claude auth
                                      login` real, navegador real, login
                                      resuelto por loopback hasta "Connected."
                                      (ver más abajo en esta sección). El
                                      camino de pegar código a mano sigue sin
                                      ejercitarse contra un CLI real en vivo
                                      (dos intentos, los dos resueltos por
                                      loopback antes de necesitarlo) — cubierto
                                      por tests con fakes, no bloqueante
Fase 7 — Code mode                   ✅ cerrada — capa de servicio completa +
                                      UI real (git panel, diff viewer, flujo
                                      de PR, selector de repo, panel de
                                      terminal, "Commit + PR"), CONFIRMADA
                                      compilando en windows-latest CI Y
                                      verificada a mano en Windows real (ver
                                      §10): git panel, barra de ramas,
                                      selector/dedup de repos, panel de
                                      terminal, el ciclo completo de PR real
                                      vía el agente (`gh pr create`/`merge`
                                      por Bash) Y, por separado, los botones
                                      propios "Create PR"/"Merge" pulsados
                                      directamente — PR creado y mergeado
                                      desde la UI, confirmado en GitHub.
                                      Nueve bugs reales encontrados y
                                      arreglados por el camino (detalle en
                                      §10). El toggle opt-in de Auto-PR ya
                                      está construido (`AppSettings.cs` +
                                      `PullRequestViewModel.AutoPr` +
                                      checkbox en `CodeModePage.xaml` +
                                      disparo en `OnChatViewModelPropertyChanged`,
                                      ver §10) Y VERIFICADO funcionando de
                                      punta a punta en Windows real (abre PR
                                      al terminar el turno, lo actualiza en
                                      cambios siguientes, no dispara nada con
                                      el toggle apagado). Un bug de estilo
                                      encontrado y arreglado en el mismo pase
                                      (checkbox desalineado de su texto, ver
                                      §10)
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
  Auto-PR, ni cargar el contenido crudo (no-diff) de un archivo. (El panel de
  terminal, mencionado aquí como pendiente en versiones anteriores de este
  párrafo, se portó después — ver la entrada más abajo.)
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

- ✅ **Panel de terminal de Code Mode — `ChatViewModel.CommandLog` +
  `CodeModePage`.** Investigado primero: no es un terminal PTY en vivo (no
  hace falta ningún primitivo Windows nuevo), es un log de solo lectura —
  `CodeModeView.swift`'s `terminalPane`, colapsable, alimentado por
  `vm.commandLog: [CommandRun]`. `ClaudeStreamParser` (Fase 3) ya emitía
  `ChatEvent.CommandStarted`/`CommandOutput`; el `ChatViewModel.cs` mínimo
  solo manejaba el primero. Añadido: record `CommandRun(Command, Output, At)`,
  `Trimmed(string, limit: 12_000)` — puerto directo de `trimmed(_:limit:)` de
  Swift (head+tail ~12KB, elide el medio; usa slicing UTF-16 de C# en vez de
  slicing por Character de Swift, una diferencia de plataforma aceptada sin
  más ceremonia para una heurística de truncado) — y `_pendingCommands`
  (diccionario id→command, resetado cada turno igual que `_activeSubagent`)
  para emparejar el `CommandStarted` de un Bash con su `CommandOutput`
  posterior. Divergencia real y deliberada de Swift: el original limpia
  `commandLog`/`pendingCommands`/`activity` en CADA turno; este port deja
  `CommandLog` acumulando toda la sesión, igual que `Activity` ya hace en
  este port — consistencia interna con lo ya construido, no un intento de
  replicar Swift línea por línea. En la UI, `CodeModePage` gana un panel
  colapsable (toggle por code-behind plano, sin estado en el ViewModel) que
  lista cada `CommandRun`; para eso, `CodeModeWindow`/`CodeModePage` ahora
  reciben el MISMO `ChatViewModel` que usa el chat de `MainPage` (no uno
  nuevo) — igual que `CodeModeView.swift`, que recibe el `ChatViewModel` del
  chat en vez de construir uno propio — así el panel refleja la sesión viva,
  no una copia local. `MainPage.OnCodeModeClick` pasa `ViewModel` al abrir la
  ventana. 5 tests nuevos (pairing command↔output, tool_results de ids no
  rastreados se ignoran, que `CommandLog` sobrevive entre turnos mientras
  `_pendingCommands` no arrastra entradas obsoletas de un turno cancelado, y
  el truncado puro de `Trimmed`) — 278 en total.

- ✅ **`ShipFlow.cs` + botón "Commit + PR" — el motor de `commitAndPR(auto:)`
  de `CodeModeView.swift`, sin su disparo automático.** Servicio nuevo,
  hermano de `CodeGit`/`GitHubCLI`, no otro ViewModel: commit (todo, o solo
  lo staged si algo está staged y no es el flujo auto) → push → abrir un PR,
  o saltarse abrirlo y reportar "actualizado" si la rama ya tenía uno abierto
  (un segundo push ya deja al PR existente de `gh` al día; llamar `gh pr
  create` otra vez daría error). Tolera un fallo "nothing to commit" — puede
  que ya haya commits locales listos para el push/PR. Recibe un
  `IProcessLauncher` directamente en vez de depender de ninguno de los dos
  ViewModels, así que sigue siendo testeable con fakes por sí solo sin
  acoplar `GitPanelViewModel` a `PullRequestViewModel` — el acoplamiento que
  Fase 7 ha evitado deliberadamente todo este tiempo.
  `PullRequestViewModel.ShipAsync` es el envoltorio fino que lo llama y
  actualiza `Info`/`Title`/`Body`/`StatusMessage`. `ChatViewModel` gana
  `DraftCommitMessage()`/`DraftPrBody()` (puertos de `draftMessage()`/
  `prBody()`) para cuando la caja de commit / título / descripción del PR
  están en blanco. `CodeModePage` gana un botón "Commit + PR" junto a
  Commit/Push. Deliberadamente SIN portar todavía: el toggle opt-in de
  Auto-PR que dispara esto solo al terminar un run — Swift lo persiste con
  `@AppStorage`, y este port no tiene ningún mecanismo de settings-storage
  aún; además, cablear el disparo automático significaría meter la mano en
  el ciclo de vida de turnos de `ChatViewModel` desde Code Mode, un límite
  que Fase 5 dejó confirmado funcionando y que este pase prefiere no
  arriesgar sin que un humano lo corra. 15 tests nuevos: 7 de `ShipFlow`
  (commit+push+PR de punta a punta, salta crear un segundo PR si ya había
  uno, tolera "nothing to commit", un fallo real de commit/push detiene el
  flujo antes de tiempo, `stagedOnly` respeta auto vs. staged), 3 de
  `PullRequestViewModel.ShipAsync`, y 5 de `ChatViewModel.DraftCommitMessage`/
  `DraftPrBody` (primera línea, límite de 64 chars, fallback sin respuesta
  del agente todavía) — 293 en total.

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

**Hito: panel de terminal (`ChatViewModel.CommandLog` + `CodeModePage`)
confirmado en CI.** Commit `fc21820`, run
[31156454867](https://github.com/pedalbacklog/StrategyForge/actions/runs/31156454867)
(`windows-latest`, disparado por `workflow_dispatch`), verde a nivel de
run Y a nivel de step: `Test Coral.Core (via Coral.Tests)` → success
(278/278) y `Build the WinUI 3 app (Coral)` → success (no skipped) —
confirma que el `CodeModeWindow`/`CodeModePage` con el `chatViewModel`
compartido, el nuevo `xmlns:vm`, y el `ListView`/`DataTemplate` del panel
de terminal en `CodeModePage.xaml` compilan de verdad contra el Windows
App SDK, no solo localmente en Linux (donde el proyecto `Coral` en sí no
puede compilarse). Todavía sin verificación visual real en Windows.

**Hito: "Commit + PR" (`ShipFlow`) confirmado en CI.** Commit `e7a8093`, run
[31162585239](https://github.com/pedalbacklog/StrategyForge/actions/runs/31162585239)
(`windows-latest`, disparado por `workflow_dispatch`), verde a nivel de run
Y a nivel de step: `Test Coral.Core (via Coral.Tests)` → success (293/293)
y `Build the WinUI 3 app (Coral)` → success (no skipped) — confirma que el
nuevo recurso `BoolNegationConverter x:Key="Negate"` y el botón "Commit + PR"
en `CodeModePage.xaml` compilan de verdad contra el Windows App SDK.
Todavía sin verificación visual real en Windows.

**Primer bug real de Fase 6 encontrado en Windows real (2026-08-07): el
`Flyout` de "Connect Claude" se auto-cancelaba al abrirse el navegador.**
El founder confirmó `dotnet test` (293/293) en su propia máquina por primera
vez (no solo en este sandbox ni en `windows-latest`), y al probar "Connect
Claude" de verdad: el CLI ya estaba instalado, el navegador se abrió solo
para el login, pero al volver a la app el flyout aparecía vacío/cerrado, sin
la caja para pegar el código — pese a que la página de `platform.claude.com`
sí mostraba el código a pegar. Causa raíz: `MainPage.xaml`'s `Flyout` tenía
`Closed="OnConnectFlyoutClosed"` → `ConnectViewModel.CancelConnect()` — pero
un `Flyout` de WinUI3 hace *light-dismiss* automático ante cualquier clic o
cambio de foco de ventana fuera de él, y abrir el navegador (con
`Process.Start`) es exactamente eso: la ventana pierde el foco, el flyout se
cierra solo, y ese cierre cancelaba TODO el flujo de conexión justo en el
paso en el que se le pide al usuario ir al navegador. Confirmado comparando
con el original de macOS (`ProviderConnectSheet.swift`): allí es una
`.sheet` modal que solo se cierra con un botón "Done" explícito — las
sheets de macOS no se auto-descartan por perder el foco de la ventana, así
que este comportamiento nunca existió en el original; es un efecto
secundario de portar a un primitivo de UI (`Flyout`) con semántica de
descarte distinta, no un fallo de lógica del ViewModel. **Arreglado**
quitando el `Closed="OnConnectFlyoutClosed"` del `Flyout` (y el método,
ahora sin uso) — el estado (`LogLines`/`Phase`/`NeedsCode`/`StatusMessage`)
vive en `ConnectViewModel` independientemente de si el popup está visible,
así que reabrir el flyout ahora debería mostrar el progreso real donde se
quedó, en vez de cancelarlo. `ConnectViewModel.CancelConnect()` sigue
existiendo como API pública (cubierta por tests) para un futuro botón
"Cancel" explícito — simplemente ya no está cableada al cierre implícito
del flyout.

**Confirmado en CI (pero el fix estaba incompleto).** Commit `7b94969`, run
[31193766949](https://github.com/pedalbacklog/StrategyForge/actions/runs/31193766949)
(`windows-latest`, `workflow_dispatch`) verde a nivel de step:
`Test Coral.Core (via Coral.Tests)` → success (293/293) y `Build the WinUI 3
app (Coral)` → success. El founder reprodujo el flujo tras ese fix y el
primer sign-in terminó con éxito sin pedir código (el CLI resolvió el login
vía loopback esa vez — un camino legítimo de `claude auth login`, no algo
que este port controle). Pero al repetirlo cerrando sesión primero para
forzar el camino CON código, apareció un **segundo bug, más profundo, del
mismo síntoma**: quitar `Closed → CancelConnect()` evitó que cerrar el
flyout cancelara la operación de FONDO, pero no evitó que el propio flyout
se **cerrara visualmente** en cuanto el navegador robaba el foco — solo dejó
de abortar el proceso, no lo mantuvo a la vista. El founder vio la caja de
pegar código "un instante" al reabrir el flyout y luego desaparecer de
nuevo, y — la prueba definitiva de que esto es un problema de UX real, no
solo de estado interno — acabó pegando el código sin querer en la caja de
chat principal ("Ask Coral…") en vez de en la caja de login (porque esta
última no estaba visible en ese momento), lo que la app respondió
correctamente con "Not logged in · Please run /login" desde el CLI. **Fix
definitivo**: nuevo handler `Closing` (distinto de `Closed` — este sí es
cancelable) en el `Flyout`, que llama `e.Cancel = true` mientras
`ConnectViewModel.IsConnecting` sea true — bloquea el *light-dismiss* del
todo mientras el login está en curso, en vez de solo dejar de reaccionar a
él. Esto es lo más parecido a cómo `ProviderConnectSheet.swift` nunca tuvo
este problema: una `.sheet` modal de macOS simplemente no se auto-cierra por
perder el foco, así que nunca necesitó una defensa explícita — WinUI3 sí, y
ahora la tiene. Acotado por el timeout de 150s ya existente en
`RunSignInAsync`: si el login se cuelga, `IsConnecting` vuelve a `false` al
fallar y el flyout puede volver a cerrarse con normalidad. **Confirmado en
CI** (commit `ab90d34`, run
[31194701495](https://github.com/pedalbacklog/StrategyForge/actions/runs/31194701495),
`windows-latest`, verde a nivel de step incluyendo "Build the WinUI 3 app
(Coral)" — el nuevo `FlyoutBase.Closing`/`FlyoutBaseClosingEventArgs`
compila bien).

**Reintento del founder tras este fix: resultado ambiguo, sin cerrar del
todo la fase.** Esta vez el flyout SÍ estaba abierto antes de ir al
navegador (a diferencia del intento anterior), pero al volver ya estaba
cerrado otra vez — sin ver ni la caja de pegar código ni el Submit. Dos
explicaciones posibles, sin evidencia suficiente para decidir entre ellas:
(a) el login se completó de verdad muy rápido sin llegar a pedir código
(`IsConnecting` volvió a `false` legítimamente y el cierre fue correcto —
plausible si la sesión ya estaba reconocida de algún modo, algo que el
propio founder sospechó: "o no la he cerrado bien, o el navegador me la
está reconociendo de alguna otra forma"), o (b) `Closing`/`e.Cancel` no
basta para bloquear el cierre cuando la ventana entera pierde el foco frente
a OTRA app (el navegador es un proceso/ventana totalmente distinta, no un
clic fuera dentro de la misma ventana — WinUI3 puede forzar el cierre de
popups por desactivación de ventana a un nivel que `Closing` no cubre).
Reescribir el flyout entero a una `Window` separada (como `CodeModeWindow`)
sería la solución más robusta si (b) es la causa real, pero es un cambio de
más superficie sin poder verificarlo yo mismo en este sandbox — no se hizo
a ciegas. En su lugar, ajuste de bajo riesgo y alto valor mientras tanto:
un aviso "⚠ Paste the code — click Connect Claude" en la cabecera de
`MainPage`, FUERA del `Flyout`, visible mientras `ConnectViewModel.NeedsCode`
sea true — así, aunque el flyout se cierre por lo que sea, el usuario ve
una señal persistente de que hay un código pendiente, en vez de depender de
pillar la ventana emergente en el momento exacto. No resuelve la causa raíz
(sigue sin saberse cuál de las dos es), pero hace el síntoma observable y
manejable mientras se decide. **Confirmado en CI**: commit `47fc459`, run
[31195743149](https://github.com/pedalbacklog/StrategyForge/actions/runs/31195743149)
(`windows-latest`, `workflow_dispatch`), verde a nivel de step incluyendo
"Build the WinUI 3 app (Coral)" — el aviso nuevo en la cabecera compila bien.
Reintentar con más cuidado en el próximo pase — ver checklist de reanudación
al final de esta sección. La sesión se pausó aquí; nadie ha vuelto a probar
"Connect Claude" todavía tras este commit.

296 xUnit tests automatizados en `Coral.Tests` a día de hoy (todos pasando,
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

**Fase 6 (Instalación de CLIs) cerrada — confirmada de punta a punta en
Windows real (2026-08-07).** `ProviderInstaller.cs`
(instalación vía npm, sign-in headless reutilizando la primitiva ConPTY de
Fase 3, flujo `Connect` unificado) y `ConnectViewModel.cs` (la capa de UI que
lo consume — mismo patrón que `ChatViewModel`) escritos y probados con fakes
(35 tests nuevos entre ambos, 195 en total en su momento) — detalle en §6.
`MainPage.xaml` gana un botón "Connect Claude" con un panel de
instalación/log/estado/código, más un aviso en la cabecera para cuando el
flyout no está a la vista (ver más abajo en esta sección — dos bugs reales
de UI encontrados y arreglados por el camino). El founder pulsó "Connect
Claude" de verdad, con `claude` ya instalado por npm: el navegador se abrió
solo, la autorización de OAuth se completó, y el log de la app mostró
"Connected." — login real, de punta a punta, confirmado. El camino de pegar
el código a mano (la caja "Paste the code from the browser" + Submit)
llegó a mostrarse correctamente en pantalla (confirmando que el aviso de
cabecera y el bloqueo de auto-cierre del flyout funcionan), pero el propio
`claude auth login` resolvió el login por su listener local (loopback) antes
de que hiciera falta pulsar Submit — dos intentos reales, los dos por ese
camino. `SubmitCodeAsync()`/`LoginInput.SubmitAsync()` siguen sin
ejercitarse contra un CLI real esperando ese envío concreto (solo contra
fakes) — un hueco de cobertura real pero no bloqueante, ya que depende de
una condición de red que este port no controla (que el loopback local falle
o esté bloqueado). `ManualProviderInstallerSmokeTest.cs` (detalle en §6)
sigue disponible como verificación automatizada equivalente. Lo que SÍ se
puede probar de forma determinista, sin depender de esa condición de red,
es el mecanismo mismo que `SubmitAsync` usa para entregar el código
(`IPseudoConsoleSession.WriteLineAsync` escribiendo en un ConPTY real) —
ver el nuevo test en §10 (2026-08-11).

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

> Nota editorial: el trabajo de Fase 7 completo y varios bugs reales de
> Fase 6 (documentados más arriba, en el bloque de §9) se registraron por
> error fuera de esta sección numerada durante varias sesiones seguidas —
> se mantienen donde están para no reescribir el historial, pero las
> entradas nuevas deberían ir aquí a partir de ahora.

**2026-08-07 — Fase 6 (Connect Claude) cerrada: login real de punta a punta
en Windows real, tras dos bugs de UI reales encontrados y arreglados en el
camino.** El founder ejecutó "Connect Claude" contra un `claude` ya
instalado por npm. Primer intento: el navegador se abrió, la página de
`platform.claude.com` mostró un código para pegar de vuelta en la app — pero
la app no mostraba ninguna caja para pegarlo, y el founder acabó pegando el
código, por confusión, en la caja de chat principal ("Ask Coral…") en vez de
en el login, que el CLI (correctamente) rechazó con "Not logged in · Please
run /login". Causa raíz: `MainPage.xaml`'s `Flyout` de "Connect Claude"
tenía `Closed="OnConnectFlyoutClosed"` → `ConnectViewModel.CancelConnect()`,
pero un `Flyout` de WinUI3 hace *light-dismiss* automático ante cualquier
clic o cambio de foco de ventana fuera de él — y abrir el navegador con
`Process.Start` es exactamente eso: la ventana pierde el foco, el flyout se
cierra solo, y ese cierre cancelaba TODO el flujo justo en el paso en el que
se pide ir al navegador. `ProviderConnectSheet.swift` (el original de
macOS) nunca tuvo este problema: una `.sheet` modal no se auto-descarta por
perder el foco de ventana — es un efecto secundario de portar a un
primitivo de UI con semántica de descarte distinta, no un fallo de lógica
del ViewModel. Primer arreglo: quitar la cancelación al cerrar. Insuficiente
por sí solo — un segundo intento mostró el mismo síntoma (el flyout seguía
cerrándose visualmente, solo dejamos de cancelar la operación de fondo).
Segundo arreglo: un handler `Closing` (cancelable, a diferencia de `Closed`)
que bloquea el *light-dismiss* del todo mientras `ConnectViewModel.
IsConnecting` sea true, acotado por el timeout de 150s ya existente en
`RunSignInAsync`. Como red de seguridad adicional mientras no había forma
de confirmar si ese segundo arreglo bastaba por sí solo (una reproducción
intermedia fue ambigua: el flyout apareció cerrado al volver del navegador,
sin evidencia de si el login había terminado rápido de verdad o si el
cierre por desactivación de ventana se saltó `Closing`), se añadió un aviso
persistente en la cabecera de `MainPage` ("⚠ Paste the code — click Connect
Claude"), fuera del flyout, visible mientras `NeedsCode` sea true. Con las
tres piezas en su sitio, la siguiente corrida real fue limpia: el aviso de
cabecera apareció, el flyout se mantuvo abierto con la caja de pegar código
visible, y el login se resolvió por el listener local (loopback) de
`claude auth login` antes de que hiciera falta pulsar Submit — terminando
en "Connected." tanto en el log de la app como en la página de éxito del
navegador ("Sign in successful… Ya puedes cerrar esta ventana"). Confirma
la Fase 6 de punta a punta: npm install real, sign-in real, browser real,
login real. Hueco de cobertura que queda, no bloqueante: el propio envío
manual de un código (`SubmitCodeAsync`/`LoginInput.SubmitAsync`) nunca se
ha ejercitado contra un CLI real esperando ese envío concreto — los dos
intentos reales se resolvieron por loopback antes de necesitarlo; sigue
cubierto solo por tests con fakes.

**2026-08-10 — Fase 7 (Code Mode), primera verificación visual real en
Windows: tres bugs de UI encontrados y arreglados, panel de terminal
confirmado funcionando.** El founder probó el flujo completo (Open Repo,
git panel, ramas, commit/push, Commit + PR, terminal) contra un repo real.
Push y el panel de terminal funcionaron tal cual (confirmado con captura:
`$ ls -la` con salida real y el contador correcto), pero aparecieron tres
bugs reales:

1. **El flyout "New" (crear rama) no daba ningún aviso de éxito ni se
   cerraba solo.** `GitPanelViewModel.CreateBranchAsync` no ponía ningún
   `StatusMessage` en el camino feliz — solo en el de error — así que un
   mensaje de error de un intento anterior (p. ej. "a branch named
   'animated-sticker' already exists") se quedaba pegado en pantalla
   indefinidamente, incluso después de una creación posterior con éxito, y
   nada le decía al flyout que se cerrara tras crear la rama. Arreglado:
   `CreateBranchAsync` ahora devuelve `bool` y pone un `StatusMessage` de
   éxito ("Created branch 'x'."); `CodeModePage.xaml.cs` cierra el flyout
   (`NewBranchFlyout.Hide()`) y limpia la caja de texto solo cuando la
   creación tuvo éxito, dejándolo abierto en caso de error para poder
   reintentar viendo el mensaje. Mismo arreglo aplicado a `CheckoutAsync`
   (mismo patrón de "éxito silencioso"), aunque no fue lo reportado
   explícitamente.
2. **El `ComboBox` de ramas resaltaba en azul la primera rama de la lista,
   no la rama realmente activa** (capturado en vivo: la rama activa real
   era `main`, pero `animated-sticker` — la primera del desplegable —
   aparecía resaltada). Causa raíz: `GitPanelViewModel.RefreshAsync()`
   asignaba `Branch` ANTES de repoblar `Branches`; como el `ComboBox`'s
   `SelectedItem` es un `x:Bind` `OneWay` cuya fuente es `Branch`, el push
   del nuevo valor llegaba cuando `Branches` todavía no contenía ese string
   (estaba a punto de vaciarse/rellenarse), así que WinUI3 no encontraba
   ítem que resaltar — y al añadirse los ítems justo después, el `ComboBox`
   termina auto-seleccionando el índice 0 por defecto, sin volver a
   evaluar el binding (`Branch` no cambió de nuevo, así que no hay nuevo
   push). Arreglado reordenando `RefreshAsync()`: `Branches` se repuebla
   ANTES de asignar `Branch`. Sin test de regresión posible en
   `Coral.Core`/`Coral.Tests` (no tienen dependencia de WinUI, así que no
   pueden observar el binding de un `ComboBox` real) — exactamente el tipo
   de bug que solo aparece corriendo la app de verdad en Windows.
3. **Pulsar Enter en la caja de nombre de rama no disparaba "Create".**
   `NewBranchBox` no tenía `KeyDown` cableado, a diferencia de `PromptBox`
   en `MainPage` (que sí soporta Enter para enviar). Arreglado con un
   `OnNewBranchBoxKeyDown` que replica el patrón ya usado en `MainPage`.

Nota aparte, no arreglada: el desplegable del `ComboBox` de ramas puede
tapar visualmente el `TextBlock` de la rama actual (justo encima) cuando la
ventana es muy pequeña — comportamiento de posicionamiento de `Popup` de
WinUI3 (voltea hacia arriba si no hay sitio abajo), no algo con un arreglo
de una línea evidente; a revisar solo si sigue siendo un problema con la
ventana a tamaño normal. 3 tests nuevos en `GitPanelViewModelTests`
(`CreateBranchAsync` éxito/fallo, `CheckoutAsync` mensaje de éxito) — 296 en
total. **Confirmado en CI**: commit `391cbfa`, run
[31386827224](https://github.com/pedalbacklog/StrategyForge/actions/runs/31386827224)
(`windows-latest`, `workflow_dispatch`), verde a nivel de step incluyendo
"Build the WinUI 3 app (Coral)" — el nuevo retorno `bool` de
`CreateBranchAsync`, el `NewBranchFlyout.Hide()`, y el `KeyDown` en
`NewBranchBox` compilan bien. **Reconfirmado visualmente por el founder**:
el desplegable ya resalta "main" correctamente, y Enter ya crea la rama —
los tres arreglos funcionan de verdad, no solo compilan.

**2026-08-10, mismo pase — cuarto bug real: reabrir el mismo repo desde
"Open Repo" lo re-clonaba en una carpeta nueva cada vez** (`pedalbacklog`
→ `pedalbacklog-2` → `pedalbacklog-3`…), y eso a su vez hacía que las
ramas creadas en sesiones anteriores "desaparecieran" — no habían
desaparecido, estaban en la carpeta anterior, abandonada, a la que la app
ya no apuntaba. Causa raíz: `MainPage.OnRepoSelectionChanged` llamaba
siempre a `RepoPickerViewModel.CloneAsync`, que a su vez usa la lógica de
dedup de carpetas de `CodeGit.CloneAsync` (`nombre`, `nombre-2`,
`nombre-3`…) — una lógica pensada para el cuadro "clonar por URL" (donde
clonar una segunda copia es una intención real, aunque rara), pero
aplicada también al simple hecho de volver a seleccionar un repo YA
clonado en la lista de "tus repos", donde la intención es siempre "ábrelo",
nunca "clona otra copia". Arreglado con `RepoPickerViewModel.
OpenOrCloneAsync(RepoRef, parentDir)`: comprueba primero si
`parentDir/nombre-del-repo` ya existe — si existe, pone `ResultRepoPath`
directamente a esa ruta (abre lo que ya hay, sin tocar git en absoluto);
si no existe, cae al `CloneAsync` normal (que aterriza limpio en la ruta
sin sufijo, al no existir todavía). `MainPage.OnRepoSelectionChanged` ahora
llama a este método en vez de forzar siempre un clone. El cuadro "clonar
por URL" no se toca — sigue usando `CloneAsync` directamente, dedup
incluido, porque ahí sí puede ser una intención real. Las carpetas
"-2"/"-3" que ya existan en disco de las pruebas anteriores NO se limpian
solas — quedan huérfanas pero intactas (con el trabajo/ramas de esas
sesiones dentro, recuperable a mano si hace falta); solo se evita crear
MÁS a partir de ahora. 2 tests nuevos en `RepoPickerViewModelTests`
(abre sin clonar cuando la carpeta ya existe, clona cuando no) — 298 en
total. **Confirmado en CI**: commit `62cbf2d`, run
[31388764516](https://github.com/pedalbacklog/StrategyForge/actions/runs/31388764516)
(`windows-latest`, `workflow_dispatch`), verde a nivel de step incluyendo
"Build the WinUI 3 app (Coral)" — `OpenOrCloneAsync` compila bien.
**Reconfirmado visualmente por el founder**: seleccionar el mismo repo dos
veces seguidas desde "Open Repo" abre la carpeta existente, sin crear una
nueva "-4" — arreglo funcionando de verdad, no solo compilando. Con esto,
los cuatro bugs reales de este pase de Fase 7 (flyout de rama sin feedback,
ComboBox con la rama incorrecta resaltada, Enter sin efecto, re-clonado
duplicado) están todos arreglados y confirmados en Windows real.

**2026-08-10, mismo pase — quinto y sexto bug reales, más un hallazgo de
Fase 5 (no de Fase 7): probar "Commit + PR" y el flujo de PR de verdad
destapó tres cosas distintas.**

1. **Fase 5, bug real de portado (no de Fase 7): `ChatViewModel.cs` corría
   con `permission-mode "default"` en vez de `"acceptEdits"`.** El founder
   le pidió al chat "haz una nueva rama que me permita incrustar un vídeo
   en el readme" — el agente intentó `git checkout -b feature/embed-video`
   vía Bash, pero el comando se quedó reportando "This command requires
   approval" indefinidamente (visto repetido varias veces en el panel de
   Activity y en el panel de terminal), porque el modo `"default"` exige
   aprobación interactiva para la mayoría de llamadas a herramientas, y un
   run headless por `-p` no tiene ningún canal para darla — exactamente el
   límite de alcance ya documentado ("no 'Ask' live-permission mode"), pero
   agravado porque ni siquiera estábamos usando el modo por defecto
   correcto. Comparado con el original: `ChatViewModel.swift` usa
   `permissionMode: String = "acceptEdits"` como su propio valor por
   defecto — nuestro puerto tenía `"default"` puesto a fuego en
   `RunTurnAsync`, una divergencia real, no una decisión de diseño.
   Arreglado cambiando el literal a `"acceptEdits"`. 1 test nuevo
   (`SendAsyncRunsWithAcceptEditsSoToolCallsDontHangOnApproval`) que
   comprueba el argumento `--permission-mode` real que recibe el proceso —
   299 en total.
2. **El flyout "Pull Request" no tenía ninguna protección contra el
   auto-cierre** — el mismo problema que ya se había encontrado y arreglado
   para "Connect Claude" (`PORT-PLAN.md`, entradas de Fase 6 más arriba),
   sin haberse portado también aquí. El founder lo confirmó: cambiar de
   ventana con el flyout abierto (con texto ya escrito en título/
   descripción) lo cerraba solo; el texto sobrevivía al reabrirlo (el
   estado vive en el ViewModel, no en el popup), pero cerrarse sin que el
   usuario lo pidiera seguía siendo el problema real. A diferencia de
   "Connect Claude", este flyout no tiene un momento de "operación en
   curso" (`IsConnecting`) al que enganchar el bloqueo — es solo el usuario
   escribiendo, sin más. Arreglado bloqueando el *light-dismiss* de forma
   incondicional (`Closing` con `e.Cancel = true` siempre) y añadiendo un
   botón "✕" explícito (`OnClosePrFlyoutClick`) que activa una bandera
   (`_prFlyoutClosingAllowed`) justo antes de llamar a `Hide()`, para que
   el `Closing` handler sepa distinguir ese cierre explícito del
   *light-dismiss* implícito. Deliberadamente NO se cierra solo al crear/
   mergear el PR con éxito (a diferencia del flyout de "New" rama): el
   propio flyout muestra `PullRequestViewModel.Info.Title`/`.State`
   actualizados tras el éxito, así que cerrarlo de golpe le quitaría al
   usuario esa confirmación visual — mejor dejar que lo cierre él cuando
   ya la haya visto.
3. **El botón "Commit + PR" no daba ninguna señal visible de si había
   hecho algo.** El founder lo probó y no tenía forma de saber si había
   funcionado. Causa: `PullRequestViewModel.ShipAsync` sí pone
   `StatusMessage` (éxito o error), pero el único `TextBlock` que lo
   mostraba vivía DENTRO del flyout separado "Pull Request" — que
   "Commit + PR" nunca abre. Arreglado añadiendo una copia de ese mismo
   `TextBlock` junto al propio botón "Commit + PR", en el panel principal,
   siempre visible sin necesidad de abrir nada más.

Nota aparte, no un bug: los dos errores que vio el founder al intentar crear
el PR a mano ("you must first push the current branch to a remote" y luego,
tras el push, "GraphQL: No commits between main and embed-video") son
comportamiento CORRECTO de `gh`/GitHub — la rama `embed-video` se creó pero
nunca llegó a tener ningún commit propio (el intento de editar el README vía
chat se quedó bloqueado por el bug #1 de esta entrada, así que el archivo
nunca cambió), así que no hay diff real que ofrecer como PR. Con el bug #1
arreglado, un intento futuro donde el agente sí complete la edición debería
producir un PR real sin este error.

**Confirmado en CI**: commit `b55abcc`, run
[31467871783](https://github.com/pedalbacklog/StrategyForge/actions/runs/31467871783)
(`windows-latest`, `workflow_dispatch`), verde a nivel de step incluyendo
"Build the WinUI 3 app (Coral)" — el `permission-mode "acceptEdits"`, el
`Closing`/botón "✕" del flyout de PR, y el `TextBlock` de estado nuevo junto
a "Commit + PR" compilan bien.

**Reconfirmación real: `"acceptEdits"` no bastaba — séptimo bug, mismo
pase.** El founder pidió al chat crear una rama y comitear un cambio en el
README; el `git add`/`git commit` se quedaron pidiendo aprobación otra vez,
igual que antes de este fix. El propio modelo, al recibir la denegación,
le pidió al usuario en el CHAT que "aceptara un diálogo" que nunca existió
en la UI — confusión perfectamente esperable, porque el modelo asume que
hay un canal de aprobación en vivo que este puerto no tiene. Causa raíz
completa (encontrada revisando `ChatViewModel.swift` a fondo, no solo su
valor por defecto): Swift usa `"acceptEdits"` como base, PERO ese modo
también puede denegar llamadas a herramientas — cuando eso pasa, la app
real muestra una UI de permisos en vivo ("Ask" mode: `pendingPermission`/
`respondPermission`/`retryAllowingAll`) donde el usuario aprueba y el turno
se reanuda con `"bypassPermissions"`. Esa UI es, desde el principio de este
port, alcance deliberadamente diferido ("no 'Ask' live-permission mode",
ver el doc comment de `ChatViewModel.cs` y el Status de `README.md`) — pero
sin ella, un run headless de un solo turno (`-p`) que recibe una denegación
no tiene ningún canal a mitad de turno para pedir/recibir aprobación: es un
callejón sin salida, no un prompt recuperable. **Arreglo definitivo**:
`ChatViewModel.RunTurnAsync` ahora usa `"bypassPermissions"` directamente
desde el principio del turno — lo mismo que hace `retryAllowingAll` en
Swift, aplicado sin condición porque aquí no existe la alternativa
interactiva. 1 test actualizado (comprobaba `"acceptEdits"`, ahora
comprueba `"bypassPermissions"`) — sigue en 299 en total (no se añadió,
se corrigió). Si en el futuro se porta el modo "Ask" de verdad, este valor
por defecto debería revisarse.

**Confirmado en CI**: commit `0ead749`, run
[31469136602](https://github.com/pedalbacklog/StrategyForge/actions/runs/31469136602)
(`windows-latest`, `workflow_dispatch`), verde a nivel de step incluyendo
"Build the WinUI 3 app (Coral)" — el nuevo literal `"bypassPermissions"` y
el test actualizado compilan bien.

**Reconfirmado de verdad por el founder.** Le pidió al chat conectarse al
repo, cambiar a la rama `embed-video`, y preparar un GIF local para el
README — el agente encadenó `find`, `git status`, `git diff`,
`git branch -a`, `git fetch`, copiar el archivo, y `ls`, todo sin
bloquearse ni una vez pidiendo aprobación (visible en el panel de
Activity, más de diez pasos de Bash seguidos). Un segundo mensaje ("haz
commit y push") completó el ciclo con éxito: `README actualizado con el
<img> y assets/test.gif añadido al repo`, commit `34a5caa`. Con esto, el
séptimo bug de este pase (y el más disruptivo, porque bloqueaba el caso
de uso central de Code Mode: que el agente haga trabajo de git de verdad)
queda cerrado y confirmado en Windows real, no solo compilando.

**Ciclo completo de PR real confirmado — vía el agente, no los botones
propios.** El founder siguió con "haz PR y mergea" en el mismo chat: el
agente creó el PR (`gh pr create --base main --head embed-video`),
confirmó la URL (`.../pull/1`), y lo mergeó (`gh pr merge 1 --merge
--delete-branch=false`), verificando el resultado con
`gh pr view 1 --json state,mergedAt,url`. Todo por Bash, con el fix de
`bypassPermissions` sosteniendo cada paso sin bloquearse. Esto prueba que
`gh`/`git` funcionan de punta a punta en este entorno (autenticación,
remoto, permisos de repo) — pero NO prueba específicamente los botones
"Create PR"/"Merge" del flyout de `CodeModePage` ni "Commit + PR"
(`ShipFlow`/`PullRequestViewModel.ShipAsync`), que usan un camino de
código distinto (`GitHubCLI.CreatePRAsync`/`MergePRAsync` llamados desde
C#, no desde una llamada a Bash del agente) y que en esta prueba nunca se
llegaron a pulsar. Dado que el `gh` subyacente ya está confirmado
funcionando en esta máquina, el riesgo restante es bajo, pero sigue siendo
una verificación explícita pendiente antes de dar la Fase 7 por cerrada
del todo.

**2026-08-11 — botones "Create PR"/"Merge" probados de verdad, con éxito, y
dos bugs reales más encontrados y arreglados por el camino (uno de ellos
serio: la app se quedaba bloqueada).** El founder aisló cada botón en su
propia rama siguiendo un plan de prueba dedicado. `Push`, `Create PR` y
`Merge` funcionaron correctamente de punta a punta — PR #2 creado y
mergeado desde los botones propios de la app, confirmado también en
GitHub. Por el camino:

1. **Falso positivo inicial: "not a git repository" en Code Mode.** No era
   un bug — el founder tenía abierta una ventana de Code Mode apuntando a
   `C:\Users\mase_` (su carpeta de usuario, que no es un repo) en vez de a
   `C:\Users\mase_\pedalbacklog`, por tener más de una ventana de Coral
   abierta a la vez. Cada ventana recuerda el repo con el que se abrió;
   "Code Mode" siempre opera sobre el de SU ventana. Se explicó y no
   requirió cambio de código — aunque sí destapó que `GitPanelViewModel.
   RefreshAsync()` no pone ningún `StatusMessage` cuando el repo no es
   válido (a diferencia de `CommitAsync`/`PushAsync`/`CreateBranchAsync`,
   que sí informan de sus fallos) — el founder pulsó "Refresh" antes de
   nada y "no vio cambios ni avisos", exactamente ese hueco. Anotado como
   arreglo menor pendiente, no bloqueante.
2. **Bug real, serio: pulsar "Connect Claude" estando ya conectado
   relanzaba el login completo — dos ventanas de navegador, app bloqueada,
   tuvo que forzar el cierre.** Dos causas combinadas:
   - `ConnectViewModel` nunca comprobaba si ya había una sesión válida
     antes de lanzar instalación+login — `ProviderAuth.cs` (portado en
     Fase 4 precisamente para esta comprobación barata, sin subproceso) no
     estaba conectado a este botón. Arreglado: `ConnectAsync()` ahora
     llama a `ProviderAuth.Freshness(Provider)` primero; si ya está `Ok`,
     pone `StatusMessage = "Already connected."` y no toca ni el proceso
     ni la pseudo-consola.
   - El propio `claude auth login` imprime la URL de login en más de una
     línea (una vez al abrir el navegador, otra como "si no se abrió,
     visita..."), y `ProviderInstaller.Connect()` abría el navegador por
     cada línea que la contuviera, sin bandera de "ya abierta" a ese nivel
     — a diferencia de `ProviderInstaller.swift`, que abre la URL
     directamente dentro del bucle de lectura con su propio `openedURL`,
     sin dividir "detectar la URL" y "abrirla" en capas distintas como
     hace este port. Arreglado añadiendo esa misma bandera de una sola vez
     en `Connect()`. Con el primer arreglo puesto, este caso concreto ya
     ni debería dispararse (al estar ya conectado, nunca se llega a esta
     rama) — pero el bug de fondo (URL duplicada) seguía siendo real para
     cualquier primer login futuro, así que se arregló igualmente. El
     "bloqueo" que vivió el founder era, en parte, el propio arreglo de
     `Closing` de la semana pasada funcionando tal como se diseñó
     (bloquea el cierre mientras `IsConnecting`) — solo que aplicado a una
     operación que nunca debió arrancar. 2 tests nuevos
     (`ConnectAsyncSkipsSignInEntirelyWhenAlreadyFresh`,
     `ConnectAsyncOnlyOpensTheLoginUrlOnceEvenIfTheCliPrintsItTwice`) —
     301 en total.

Además, hallazgo de UX (no bug funcional): ningún botón de Code Mode daba
ninguna señal MIENTRAS trabajaba (solo al terminar) — "en ninguno de los
dos puntos anteriores parece que esté pasando nada hasta que acaba". Los
ViewModels ya llevan su propio `IsBusy`, simplemente no estaba conectado a
nada visual. Arreglo mínimo: `IsEnabled` de Commit/Push/Refresh/Create
(rama)/Create PR/Merge ahora atado a `!IsBusy` del ViewModel
correspondiente — un botón deshabilitado durante la operación es señal
suficiente sin necesitar un spinner. `"Commit + PR"` ya lo tenía desde que
se creó. CONFIRMADO compilando en windows-latest CI (commit `7e2cd30`, run
[31471341980](https://github.com/pedalbacklog/StrategyForge/actions/runs/31471341980) —
"Build the WinUI 3 app (Coral)" en verde).

**Cerrado el cabo suelto anotado en la entrada anterior: `Refresh` sin
aviso en un repo inválido.** `GitPanelViewModel.RefreshAsync()` ahora pone
un `StatusMessage` ("Couldn't read this repo — is it a real git
repository?") cuando `CurrentBranchAsync` devuelve `null` — deliberadamente
sin limpiar el mensaje en el camino de éxito, porque varios llamantes
(`CommitAsync`/`PushAsync`/`CreateBranchAsync`/`CheckoutAsync`) ponen su
propio mensaje de éxito ANTES de llamar a `RefreshAsync()` y necesitan que
sobreviva. El test nuevo (`RefreshAsyncReportsAStatusMessageWhenTheFolderIsntARepo`)
destapó de paso un fake perezoso en `CommitAsyncClearsTheMessageAndRefreshesOnSuccess`
que nunca simulaba `rev-parse` con una rama real (devolvía éxito con salida
vacía) — inofensivo antes de este cambio porque nada comprobaba `Branch`,
pero ahora sí, así que se corrigió el fake para que devuelva `"main"`. 1
test nuevo — 302 en total. CONFIRMADO compilando en windows-latest CI
(commit `bfe8b88`, run
[31471817071](https://github.com/pedalbacklog/StrategyForge/actions/runs/31471817071) —
"Build the WinUI 3 app (Coral)" en verde).

**2026-08-11 — Toggle opt-in de Auto-PR construido de punta a punta.**
Tras cerrar Fase 7, el founder pidió seguir con el producto y, entre las
opciones planteadas, eligió construir el toggle de Auto-PR — lo único que
había quedado deliberadamente diferido porque necesitaba una capa de
persistencia de settings que aún no existía.

1. **`Coral.Core/Services/AppSettings.cs` (nuevo).** Persistencia mínima
   respaldada por un fichero JSON en `%LOCALAPPDATA%\Coral\settings.json`
   — deliberadamente NO usa `Windows.Storage.ApplicationData` porque
   requiere identidad de paquete y Coral sigue sin empaquetar (Fase 9
   pendiente). Sigue el mismo patrón núcleo-puro-más-envoltorio-real ya
   establecido por `ProviderAuth.FreshnessUncached(homeDirectory)`/
   `Freshness()`: `ReadAutoPrUncached(path)`/`WriteAutoPrUncached(path,
   value)` son funciones puras testeables contra un directorio temporal
   (nunca tocan el disco real en los tests), y la propiedad estática
   `AutoPr` es el envoltorio real que usa `DefaultPath()`. Tolera JSON
   corrupto devolviendo `false` en vez de lanzar. 6 tests nuevos en
   `AppSettingsTests.cs`.
2. **`PullRequestViewModel.AutoPr` (propiedad nueva).** `bool` respaldado
   por `_saveAutoPr`/el valor inicial de `loadAutoPr`, ambos inyectables
   por constructor (por defecto, el getter/setter reales de
   `AppSettings.AutoPr`) — así el toggle es testeable sin tocar el fichero
   real, mismo patrón que `checkFreshness` en `ConnectViewModel`. Se
   persiste inmediatamente en cada cambio real (`SetProperty` ya evita
   escribir si el valor no cambia). 3 tests nuevos.
3. **`PullRequestViewModel.ShouldAutoShip(autoPr, hasRepo, ghInstalled,
   hasChanges)` (método estático nuevo).** Decisión pura que replica la
   guarda de `CodeModeView.swift`'s `.onChange(of: vm.isRunning)`
   (`autoPR, isRepo, GitHubCLI.isInstalled, !changeStats.isEmpty`) — sirve
   de spec testeada (5 casos `[Theory]`) aunque, por una colisión de
   nombres en C# (ver más abajo), el disparo real no lo llama
   directamente.
4. **Checkbox en `CodeModePage.xaml`.** `"Auto-PR when a run finishes"`
   atado a `PullRequestViewModel.AutoPr` con `Mode=TwoWay` — sin lógica
   propia, el propio binding ya persiste el cambio a través de la cadena
   de arriba.
5. **Disparo real — `CodeModePage.xaml.cs`, `OnChatViewModelPropertyChanged`.**
   Puerto de `.onChange(of: vm.isRunning)` de `CodeModeView.swift`: el
   constructor de `CodeModePage` se suscribe a
   `ChatViewModel.PropertyChanged` (sin desuscribirse al cerrar — mismo
   precedente ya sentado por `MainPage` con `RepoPickerViewModel`,
   inofensivo porque el propio guard de `IsBusy` de `ShipAsync` convierte
   cualquier disparo repetido en el mismo tick en un no-op). Cuando
   `IsSending` pasa a `false`, si `AutoPr` está activo, refresca el panel
   de git y — si `gh` está instalado y hay cambios — dispara `ShipAsync`
   con un commit message/PR body redactados a partir de la última
   respuesta del chat (mismo fallback que usa `OnShipClick`). Nota de C#:
   `PullRequestViewModel.ShouldAutoShip(...)` NO se puede llamar así desde
   dentro de `CodeModePage` — `CodeModePage` tiene su propia propiedad de
   instancia llamada `PullRequestViewModel`, y el identificador resuelve a
   esa propiedad antes que al tipo, así que C# no deja alcanzar un miembro
   estático a través de lo que resuelve a una referencia de instancia
   (CS0176). Se evitó compilando el mismo chequeo de cuatro partes en
   línea, dejando el método estático como spec documentada y testeada.

Cubierto por tests unitarios, build/test local (316/316) y CONFIRMADO
compilando en windows-latest CI (commit `88d0781`, run
[31472737875](https://github.com/pedalbacklog/StrategyForge/actions/runs/31472737875) —
"Build the WinUI 3 app (Coral)" en verde, la XAML del nuevo checkbox
incluida). 14 tests nuevos — 316 en total.

**Verificación a mano en Windows real, el mismo día.** El founder probó
el toggle punto por punto sobre `ui-test-create-pr-2`:

1. Checkbox y su texto ("Auto-PR when a run finishes") se veían
   desalineados verticalmente — real bug de estilo, no funcional. La
   plantilla por defecto de `CheckBox` en WinUI3 no centra el contenido
   verticalmente frente al glifo cuando se reduce `FontSize` (aquí a 12);
   arreglado añadiendo `VerticalContentAlignment="Center"` al `CheckBox` en
   `CodeModePage.xaml`. Solo XAML, sin tests unitarios posibles. CONFIRMADO
   compilando en windows-latest CI (commit `d415f90`, run
   [31474875284](https://github.com/pedalbacklog/StrategyForge/actions/runs/31474875284) —
   "Build the WinUI 3 app (Coral)" en verde) — pendiente de reconfirmación
   visual en Windows real.
2. Con el toggle activo, pedir un cambio por chat abrió un PR solo al
   terminar el turno — confirmado tanto en la captura de Coral
   ("Pull request opened.") como en la rama `ui-test-create-pr-2` en
   GitHub. Funciona como se diseñó.
3. Un segundo cambio en la misma rama actualizó el PR existente en vez de
   abrir uno nuevo — confirmado en GitHub. El mensaje "Pull request
   updated." si aparece (visible en la captura, en el mismo `TextBlock` que
   ya mostraba "Pull request opened." antes) — el founder no lo localizó
   en el momento, no porque falte, sino porque es texto gris pequeño justo
   encima del checkbox y fácil de pasar por alto durante la prueba en vivo.
   No es el mismo bug que el de "Push no da feedback" (aquella vez el
   `TextBlock` correcto no existía en ese punto de la UI; aquí sí existe y
   se actualiza) — pero el founder pidió pulirlo igualmente, ver más abajo.
4. Con el toggle desactivado, un tercer cambio por chat SÍ se aplicó
   localmente (el chat lo confirma) pero NO se disparó ningún commit/push
   automático — el cambio se quedó sin confirmar en el working tree,
   invisible en GitHub. Comportamiento esperado: confirma que el toggle
   apagado no dispara nada.

**Pulido pedido explícitamente tras la verificación: hacer más visible
"Pull request updated"/"Pull request opened".** El `TextBlock` de
`PullRequestViewModel.StatusMessage` junto a "Commit + PR" en
`CodeModePage.xaml` estaba a `Opacity="0.7"` y peso normal, igual que el
resto de mensajes de estado de la página — a diferencia de esos otros,
este es justo el que el toggle de Auto-PR necesita que se note, así que
pasa a opacidad completa y `FontWeight="SemiBold"` (13px en vez de 12). El
resto de `TextBlock`s de estado de la página (el de `GitPanelViewModel` y
la copia dentro del flyout "Pull Request") se dejan igual a propósito —
no fueron el hallazgo, y ese otro contexto (el flyout) ya es lo bastante
prominente al ser un panel modal propio.

CONFIRMADO compilando en windows-latest CI (commit `7560aeb`, run
[31475443776](https://github.com/pedalbacklog/StrategyForge/actions/runs/31475443776) —
"Build the WinUI 3 app (Coral)" en verde).

Pendiente: reconfirmar visualmente en Windows real tanto el arreglo de
alineación del checkbox como este cambio de contraste del mensaje de
estado.

**Cerrando (parcialmente) el hueco de cobertura de Fase 6: `WriteLineAsync`
contra un ConPTY real.** Con Fase 9 aparcada a la espera de decidir la
estrategia de firma, se priorizó este cabo suelto anotado desde el cierre
de Fase 6: `SubmitCodeAsync()`/`LoginInput.SubmitAsync()` — el camino que
entrega a mano el código de login pegado del navegador — nunca se había
ejercitado contra un CLI real esperando ese envío (los dos intentos reales
de login se resolvieron por loopback antes de llegar a ese punto, algo que
este port no controla porque depende de la red). Lo que SÍ se puede probar
de forma determinista, sin depender en absoluto de esa condición de red o
de tener `claude` instalado, es el mecanismo de bajo nivel que
`SubmitAsync` usa para entregar el código:
`IPseudoConsoleSession.WriteLineAsync` escribiendo en la pseudo-consola.
`ManualPseudoConsoleSmokeTest.cs` (Fase 3) solo había probado hasta ahora
la mitad de LECTURA de esa primitiva (`cmd.exe /c echo ...`, sin stdin de
por medio) — nunca la mitad de ESCRITURA.

Nuevo test manual, `WriteLineAsyncDeliversInputToARealChildProcessStdin`:
arranca `cmd.exe /c "findstr /r ."` (sin fichero, `findstr` lee de stdin
línea a línea y reimprime las que casan con el patrón — un "cat" de toda
la vida en cmd, sin las trampas de temporización de `set /p`/expansión
retrasada), escribe una línea de prueba vía `WriteLineAsync`, y confirma
que esa misma línea aparece en la salida real del proceso hijo. A
diferencia del resto de tests manuales de este fichero, `findstr` no
termina solo (el pipe de entrada del ConPTY se queda abierto), así que el
test lee solo hasta ver lo que busca (con un timeout de 10s) y luego mata
la sesión explícitamente en vez de esperar una salida natural.

Categoría `Manual` como el resto — compila limpio en Linux (`dotnet build`/
`dotnet test --filter "Category!=Manual"`, 316/316 sin cambios, ya que el
test nuevo queda excluido del filtro) Y CONFIRMADO compilando en
windows-latest CI (commit `bb84703`, run
[31476621086](https://github.com/pedalbacklog/StrategyForge/actions/runs/31476621086) —
"Test Coral.Core (via Coral.Tests)" y "Build the WinUI 3 app (Coral)" en
verde), pero como el resto de esta familia de tests SOLO puede ejecutarse
de verdad en Windows — pendiente de que el founder lo corra:
`dotnet test windows/Coral.Tests/Coral.Tests.csproj -c Release --filter "FullyQualifiedName~WriteLineAsyncDeliversInputToARealChildProcessStdin"`.
Sigue sin cubrirse (y no puede forzarse de forma determinista) el camino
end-to-end completo contra un `claude auth login` real llegando a
`NeedsCode` — eso sigue dependiendo de que el loopback local falle o esté
bloqueado, fuera del control de este port.

**Intento de verificación en Windows real, sin éxito — problema de entorno
sin resolver, documentado en vez de perseguido más.** El founder lo probó
en su máquina física real (no RDP, no VM — descartado explícitamente) y
los dos tests de `ManualPseudoConsoleSmokeTest` fallaron con la MISMA huella
que el bug de Fase 3 (`STATUS_DLL_INIT_FAILED`, ver más arriba en esta
sección): solo los 16 bytes del handshake inicial de ConPTY
(`ESC[?9001h ESC[?1004h...`) cruzan nuestro pipe y luego nada — incluido
el test de `echo` que en su momento (2026-08-06) SÍ se confirmó pasando.
Se repitió el mismo diagnóstico de entonces (cambiar a la consola clásica,
sin Windows Terminal) — el founder confirmó explícitamente, antes de
volver a correr el test, que la ventana era la consola clásica sin
pestañas (no Windows Terminal alojando una pestaña de `cmd`/PowerShell) —
y el fallo se reprodujo IGUAL. Esto descarta la causa documentada en Fase 3
como explicación completa: algo más está interceptando la creación de la
ConPTY anidada en esta máquina hoy, que no interceptaba (o no de la misma
forma) cuando se diagnosticó aquello. Candidatos razonables sin confirmar
(ninguno diagnosticable a distancia sin acceso directo a la máquina): una
actualización de Windows que haya cambiado el comportamiento interno de
`conhost.exe` desde entonces, algún software de terminal/consola de
terceros interceptando la creación de consolas a nivel de sistema, o una
directiva de grupo/registro local.

**Decisión: aparcado, no bloqueante.** La app real nunca se lanza desde una
terminal (se abre desde el Explorador, sin ninguna ConPTY por encima en el
árbol de procesos), así que este escenario de "ConPTY anidada" no puede
darse en producción — solo afecta a la verificación manual de estos dos
tests concretos vía `dotnet test` a mano. `WriteLineAsyncDeliversInputToARealChildProcessStdin`
queda escrito, compilando en CI, y documentado como pendiente de
verificación futura si alguna vez cambia el entorno (o se investiga con
acceso directo a la máquina) — no se ha invertido más tiempo en
diagnosticarlo a ciegas por chat.

**2026-08-11 — Selección de estrategia (P0 punto 2), Fase 1: el chat deja de
ser un `claude -p` a pelo.** Hallazgo real durante una revisión de qué
seguir construyendo: el propio §4 marca "selección/edición de estrategias,
Advisor básico" como P0 ("sin esto no es Coral, es un shell vacío"), pero
nunca se conectó a la UI de Windows — `ChatViewModel.cs`/`MainPage.xaml` no
referenciaban `Strategy` en ningún sitio. El chat corría siempre en bruto,
sin generar los ficheros de subagentes/`CLAUDE.md` que `StrategyWriter`
(portado y probado desde Fase 2) sabe escribir desde hace meses. Antes de
tocar código se pidió un plan detallado a un subagente de planificación,
que investigó tanto el Swift original (`ChatView.swift`, `StrategyPickerColumn.swift`,
`AdvisorEngine.swift` — ~4400 líneas en total) como el estado actual del
port, y propuso cortar el trabajo en 4 fases. Fase 1 (esta entrada):
selector de plantilla de solo lectura → generar sus ficheros → el chat
corre contra el equipo generado. Sin edición de estrategia y sin Advisor
todavía (fases separadas, ver el hallazgo original más abajo).

Por qué Fase 1 es tan pequeña de lo que parece: el CLI `claude` lee
`.claude/agents/*.md`/`CLAUDE.md`/`.mcp.json` de su propio directorio de
trabajo — no hace falta decírselo por argumento. En cuanto `StrategyWriter.Write`
deja esos ficheros en el repo, el SIGUIENTE turno del chat ya corre contra
el equipo completo sin que `ChatViewModel` necesite saber nada de
`Strategy`. El único cambio real necesario: `ChatViewModel._model` (privado,
fijado una sola vez en el constructor) pasa a ser `ChatViewModel.Model`
(propiedad pública mutable), para que elegir una plantilla pueda cambiar el
modelo del orquestador del turno siguiente sin reconstruir el ViewModel ni
perder la transcripción/`sessionId` en curso.

Piezas nuevas:
- `Coral.Core/ViewModels/StrategyPickerViewModel.cs` (nuevo). Expone
  `Templates` (las 15 de `StrategyLibrary.All`), `SelectedStrategy`,
  `IsBusy`, `StatusMessage`. `SelectAsync(strategy)` envuelve
  `StrategyWriter.Write` (I/O síncrona) en `Task.Run`, igual que el resto
  de ViewModels de este port envuelven trabajo síncrono. Sin dependencia de
  `IProcessLauncher` — `StrategyWriter` nunca lanza un proceso, es
  `System.IO` puro.
- Botón "Strategy" nuevo en la cabecera de `MainPage.xaml`, mismo patrón de
  `Flyout` ya usado tres veces (Connect Claude, Open Repo, el PR flyout de
  Code Mode) — lista de las 15 plantillas (nombre + descripción, sin las
  tarjetas visuales con diagrama de topología que tiene macOS, deliberadamente
  fuera de alcance, ver más abajo), un `TextBlock` en la cabecera muestra el
  nombre de la estrategia activa. Deliberadamente NO se cierra solo al
  elegir una — el propio `StatusMessage` actualizándose in situ es la
  confirmación, mismo razonamiento que el flyout de Pull Request no
  cerrándose solo al crear/mergear.
- `MainPage.xaml.cs`: `OnStrategySelectionChanged` llama a `SelectAsync` y,
  si tiene éxito, pone `ViewModel.Model = strategy.Orchestrator.Model.ToRawValue()`.

**Decisión deliberada, distinta de lo que proponía el plan del subagente:**
NO se escribe ninguna estrategia por defecto al abrir la ventana. El plan
sugería escribir `Solo()` automáticamente al arrancar como "cambio neutro
de comportamiento" — pero se descartó tras comprobar dos cosas que el plan
no había verificado: (1) `RepoPath` cae por defecto en la carpeta de
usuario COMPLETA cuando no se abre un repo concreto, así que escribir
ficheros ahí sin que el usuario haga nada sería una sorpresa real, no
neutra; (2) `StrategyLibrary.Solo()` usa `ClaudeModel.Opus5` como modelo
sugerido, mientras que el chat de hoy arranca en `claude-sonnet-5` por
defecto — aplicar ese cambio automáticamente habría subido el coste de
CADA chat sin que nadie lo pidiera. En vez de eso: el chat se comporta
EXACTAMENTE igual que antes de que esta pieza existiera hasta que el
usuario abre "Strategy" y elige algo explícitamente — cero acción
requerida, cero efecto secundario sorpresa.

Otros hallazgos del plan, documentados como cortes deliberados (mismo
patrón que los diferidos de Fase 6/7):
- La cuadrícula visual de tarjetas de macOS (diagrama de topología animado,
  pastillas de coste, filtrado por tema, "mostrar solo estrategias para
  principiantes") — puramente presentación, no bloquea "elegir una
  plantilla y correr contra ella". Diferido a un pase de pulido futuro.
- Los asistentes guiados (`ChooseStrategyWizard`, `TaskToStrategySheet`) —
  redundantes una vez exista el Advisor (Fase 3), no vale la pena
  construirlos dos veces.
- El "Advisor" en sí NO es una llamada a un LLM — es un motor heurístico
  local y determinista (palabras clave, longitud, idioma) que devuelve una
  `Strategy` recomendada; la única parte que sí usa IA
  (`adviseWithAI`, una mejora vía Apple Intelligence on-device) no tiene
  equivalente en Windows y queda descartada de raíz, no como "pendiente".

4 tests nuevos (`StrategyPickerViewModelTests`: todas las plantillas
expuestas, escritura real a un directorio temporal, error controlado en
vez de excepción sin capturar sobre una ruta no escribible;
`ChatViewModelTests`: regresión confirmando que `Model` mutado después de
construir el ViewModel se refleja en el `--model` del siguiente turno) —
320 en total. Corrección de paso: el propio §4 decía "13 templates";
tanto macOS como el port ya tienen 15 — número desactualizado, no una
funcionalidad real que faltara.

Cubierto por tests unitarios, build/test local (320/320) y CONFIRMADO
compilando en windows-latest CI (commit `7945288`, run
[31489629778](https://github.com/pedalbacklog/StrategyForge/actions/runs/31489629778) —
"Build the WinUI 3 app (Coral)" en verde) — pendiente de prueba a mano en
Windows real (elegir una plantilla, confirmar que aparecen
`.claude/agents/*.md` reales en el repo, y que el siguiente turno del chat
delega de verdad en los subagentes generados).

**2026-08-11 (mismo día) — Fase 3: el motor del Advisor, portado y
enganchado a la UI (mitad heurística).** El founder no iba a poder probar
en Windows real hasta pasadas unas horas, así que se pidió seguir
avanzando todo lo posible mientras tanto — Fase 3 es exactamente el tipo
de trabajo que se puede completar y verificar del todo SIN Windows: es
lógica pura, cubierta al 100% por `dotnet test` en Linux.

Hallazgo real durante la investigación: `AdvisorEngine.advise()` no es
autocontenido — depende de `StrategyGenerator.swift` (351 líneas, el
clasificador de tarea → forma de equipo), un fichero que el plan del
subagente no había identificado como dependencia. Sin portarlo primero,
Fase 3 no podía avanzar. Se portó:

- `Coral.Core/Generators/StrategyGenerator.cs` (nuevo): `Classify(task)` (el
  lector multi-eje por palabras clave — intención, alcance, si es adversarial,
  si necesita "scouting", etc., bilingüe EN/ES con plegado de diacríticos vía
  normalización Unicode NFD, equivalente .NET de `.folding(options:
  .diacriticInsensitive)` de Swift), `ShapeFor(profile, connected)` (el mapa
  determinista perfil → forma de equipo + tamaño), `BuildStrategy(shape,
  teamSize)` (construye un `Strategy` real desde `StrategyLibrary` y lo pasa
  por `AutoFixed()`), y `HeuristicShape` (los dos anteriores compuestos).
  Deliberadamente NO portado, mismo criterio que Fase 6/7: el camino de IA
  on-device de Apple (`generate(from:)`, `isAIAvailable`, `TaskRead`) y la
  capa de "asistencia semántica" por embeddings (`SemanticClassifier`, otra
  vez modelo on-device de Apple) que `heuristicShape` añade encima de una
  lectura de baja confianza — ninguno de los dos tiene equivalente gratis/
  privado en Windows. Sin ellos, una tarea parafraseada de forma rara puede
  clasificar un poco menos fino que en macOS; nunca de forma incorrecta de
  un modo que el camino por palabras clave no arriesgue también.
- `Coral.Core/Services/AdvisorEngine.cs` (nuevo): `Advise(task, connected)`
  — el árbol de decisión determinista completo (profundidad → modelo →
  forma de equipo → ajustes de forma barata/no-delegable → tipo de loop →
  esfuerzo), con `DecisionStep`/`Advice` portados como tipos con igualdad
  semántica (Swift define `==`/`hash` a mano para que dos recomendaciones
  idénticas comparen iguales aunque `Strategy.Id` sea un GUID fresco en
  cada llamada — el port hace lo mismo con `IEquatable<Advice>` manual en
  vez de confiar en la igualdad por defecto de un record, que compararía
  por referencia en las colecciones). Alcance de ESTA pasada: solo
  `Advise()` (el camino determinista). Deliberadamente no portados todavía:
  `adviseWithAI` (mejora vía IA on-device, mismo motivo que arriba),
  `adviseTiers` (la UI de tres niveles Economy/Recommended/Max — pertenece
  al pase de pulido de UI, Fase 4, no al motor), y `assignProviders`
  (`AdvisorEngine+Providers.swift`, reasignación de roles entre proveedores
  — un seguimiento más pequeño, aparcado).

**Decisión delicada documentada aparte: `Models/LoopKind.cs` (nuevo).**
`Advice.loopKind` es parte del contrato de `advise()` — pero `LoopKind` se
define en `Models/LoopPlan.swift`, uno de los cuatro ficheros que
`CLAUDE.md` marca como vetados para cambios autónomos ("los cambios de
loop necesitan lectura humana del diff, no solo tests... esto es un límite
firme, no una cuestión de agenda"). NO se ha leído `LoopPlan.swift` para
esto: los cuatro valores del enum (`turnBased`/`goalBased`/`timeBased`/
`proactive`) ya eran visibles en `AdvisorEngine.swift` mismo (un fichero sí
permitido), así que `LoopKind.cs` se escribió como un enum mínimo y
autocontenido a partir de esa única fuente — sin tocar, leer ni depender de
ningún fichero de la zona vetada, y sin habilitar ni acercar nada del
propio Loop scheduling/running/generation (Fase 8 sigue sin empezar en
Windows). Documentado aquí explícitamente para que quede claro que fue una
decisión consciente, no un descuido del límite.

**UI mínima enganchada, no solo el motor.** Dentro del mismo flyout
"Strategy" de Fase 1: una sección "Or describe the task and let Advisor
suggest a team" — un `TextBox` + botón "Suggest" (`AdvisorViewModel.Suggest()`,
sujeta a `AdvisorEngine.Advise()`, sin red ni proceso, instantáneo) que
muestra un resumen de una línea (equipo · modelo · esfuerzo) y un botón
"Use this" que aplica la recomendación exactamente por el mismo camino que
elegir una plantilla a mano (`StrategyPickerViewModel.SelectAsync` +
actualizar `ChatViewModel.Model`). `AdvisorViewModel` es un ViewModel
nuevo y separado de `StrategyPickerViewModel` — mismo criterio de una
responsabilidad por ViewModel que el resto del port; nunca escribe
ficheros él mismo, solo produce una recomendación.

20 tests nuevos (`AdvisorEngineTests.cs`, traducción casi mecánica de
`AdvisorEngineTests.swift` — los 18 casos migraron y pasaron a la primera,
validando que el port es fiel al original; `AdvisorViewModelTests.cs`, 2
casos) — 340 en total.

Cubierto por tests unitarios, build/test local (340/340) y CONFIRMADO
compilando en windows-latest CI (commit `cd871b0`, run
[31490734337](https://github.com/pedalbacklog/StrategyForge/actions/runs/31490734337) —
"Test Coral.Core (via Coral.Tests)" y "Build the WinUI 3 app (Coral)" en
verde).

**Verificación en Windows real, mismo día: punto 1 (elegir plantilla)
funciona perfecto — confirmado con captura, cabecera mostrando "Executor +
Advisor" y el panel de Activity con "→ advisor" real. Bug real encontrado
en "Suggest": la flyout se autocerraba sola a los pocos segundos, sin
mostrar nunca la recomendación.** El founder lo describió con precisión:
"hizo como un salto a otra ventana igual y a los pocos segundos se cerró
sola". Causa: el mismo patrón de bug ya visto dos veces en este port
(Connect Claude, Pull Request) — la fila "Use this" aparece cuando
`AdvisorViewModel.HasAdvice` pasa a `true`, la flyout crece de alto,
WinUI3 la reposiciona para seguir anclada al botón "Strategy", y ese
reposicionamiento se interpreta como una interacción externa que dispara
el light-dismiss por defecto — cerrándola antes de que la recomendación
llegara a verse. Arreglado con el mismo patrón ya probado en la flyout de
Pull Request: bloqueo incondicional de `Closing` (`FlyoutBase.Closing` +
`e.Cancel = true`) emparejado con un botón "✕" explícito
(`OnCloseStrategyFlyoutClick`) como única forma real de cerrarla. Solo
XAML/code-behind — 340/340 sin cambios en el conteo de tests (no hay
nada que testear con fakes aquí, es puro comportamiento de WinUI3).
CONFIRMADO compilando en windows-latest CI (commit `8b498ea`, run
[32400427691](https://github.com/pedalbacklog/StrategyForge/actions/runs/32400427691) —
"Build the WinUI 3 app (Coral)" en verde). El founder ya lo reconfirmó en
Windows real ese mismo día: la flyout dejó de cerrarse sola — ver los dos
hallazgos siguientes (Intro y "Use this"), encontrados justo gracias a que
ya se podía ver la recomendación con calma.

**Segunda ronda de verificación, mismo día: con la flyout ya sin
autocerrarse, dos hallazgos más — uno cosmético, uno real sin causa
confirmada.**

1. **Intro no dispara "Suggest".** La caja de texto de la tarea no tenía
   `KeyDown`, a diferencia del resto de cajas de este port (el prompt
   principal, la de nombre de rama nueva). Arreglado añadiendo
   `OnAdvisorTaskBoxKeyDown` — mismo patrón exacto que los demás.
2. **La recomendación de la captura adjunta ("Debate / Consensus
   (mediated) · Sonnet 5 · Medium effort" para "define la arquitectura
   para una app en android") NO era un bug — se confirmó con el founder
   que la captura era solo para mostrar el estado, no una queja.** De
   hecho es el resultado correcto: "arquitectura" cae en el grupo de
   intención `Decide` de `StrategyGenerator.Classify`, que sin señales de
   `multiDomain`/`breadth` mapea a `DebateConsensus`; sin palabras de
   profundidad (`depthGroups`) el modelo se queda en Sonnet 5. Coincide
   exactamente con lo que predice el port — una confirmación más de
   fidelidad, no un hallazgo.
3. **"Use this" no producía ningún cambio visible — ni cabecera, ni
   mensaje de estado, ni la flyout.** Causa RAÍZ sin confirmar todavía
   (no reproducible a distancia), pero revisando `StrategyPickerViewModel.SelectAsync`
   se encontró un no-op silencioso real: si `IsBusy` ya es `true` cuando
   se llama (p. ej. una escritura anterior todavía en marcha), el método
   devuelve `false` sin tocar `StatusMessage` ni `SelectedStrategy` —
   exactamente indistinguible de "el botón no está conectado a nada",
   que es justo lo que describió el founder. Arreglado para que ese
   camino deje rastro visible (`StatusMessage = "Busy — try again in a
   moment."`) en vez de ser silencioso, y añadido `IsEnabled` a "Use
   this" atado a `!IsBusy` (mismo patrón que la lista de plantillas ya
   tenía) para dar señal visual mientras está ocupado. Esto convierte un
   bug silencioso e indiagnosticable en uno con rastro — si el founder
   vuelve a probar y ve "Busy…", confirma esta hipótesis; si sigue sin
   ver NADA en absoluto, la causa es otra (el clic no llega al manejador)
   y habrá que investigar por ahí la próxima vez. 1 test nuevo
   (`SelectAsyncReportsBusyRatherThanSilentlyNoOpingWhileAlreadyRunning`,
   dos llamadas solapadas a `SelectAsync` confirmando que la segunda deja
   rastro) — 341 en total.

Build/test local confirmado (341/341) y CONFIRMADO compilando en
windows-latest CI (commit `5d53e03`, run
[32402981485](https://github.com/pedalbacklog/StrategyForge/actions/runs/32402981485) —
"Test Coral.Core (via Coral.Tests)" y "Build the WinUI 3 app (Coral)" en
verde).

**RECONFIRMADO en Windows real: "Suggest"/"Use this" funcionan de punta a
punta.** El founder escribió una tarea, pulsó "Suggest", vio la
recomendación ("Debate / Consensus (mediated) · Sonnet 5 · Medium
effort"), pulsó "Use this", y la cabecera cambió a "Debate / Consensus
(mediated)" — captura adjunta confirmándolo. Con esto, la Fase 3
(Advisor, mitad heurística) queda cerrada del todo: motor portado,
enganchado a la UI, los tres bugs reales encontrados en el camino
(flyout autocerrándose, Intro sin disparar Suggest, "Use this" silencioso
por `IsBusy` atascado) arreglados y verificados en Windows real.

**2026-08-21 — Fase 2: edición de estrategia, primer corte deliberadamente
recortado.** El founder pidió seguir con "la primera de la lista"
(edición de estrategia). Antes de tocar código se leyó
`StrategyEditorView.swift` (589 líneas) y `RoleRowView.swift` (469
líneas) — mucho más grande de lo necesario para "tocar los roles que ya
tengo y guardar": selector de repo propio (este port ya tiene el repo
abierto), diagrama de topología animado, popover de coste, editor de
servidores MCP, mezcla entre proveedores (Windows aún no puede conectar
Codex/Gemini), edición de prompt/descripción/memoria, y variantes de
generar (Terminal, commit, descargar brief, copiar prompt). Se acordó con
el founder un recorte explícito antes de escribir nada: solo nombre,
modelo, número de instancias y herramientas por rol, con
`Strategy.Validate()` en vivo y "Fix All" (`Strategy.AutoFixed()`) — todo
lo demás queda fuera, documentado como corte deliberado, no ausencia.

Piezas nuevas:
- `Coral.Core/ViewModels/StrategyEditorViewModel.cs` (nuevo). Envuelve un
  `Strategy` en el sitio: `Roles` (expuesto aparte de `Strategy` para que
  el `ItemsControl` de filas solo se refresque cuando la LISTA de roles
  cambia, no en cada notificación no relacionada), `Issues`/`IssueLines`
  (mensajes pre-formateados como texto plano — `"❌ ..."`/`"⚠ ..."` — para
  que la plantilla de la lista de incidencias haga `x:Bind` contra
  `string` en vez de necesitar una referencia XAML al tipo anidado
  `Strategy.ValidationIssue`), `IsValid`, `HasAutoFixableIssues`,
  `Revalidate()`, `AutoFix()` (sustituye `Strategy` entero por la copia
  arreglada que devuelve `AutoFixed()` — no es una mutación en el sitio).
- `Coral/StrategyEditorWindow.xaml`/`.xaml.cs` + `StrategyEditorPage.xaml`/`.xaml.cs`
  (nuevos). Ventana separada, mismo patrón que `CodeModeWindow` — un
  editor de roles de verdad necesita más sitio del que da cómodamente una
  Flyout de 380px, y mantenerlo separado evita que esta UI todavía no
  verificada en Windows real pueda desestabilizar el flujo de chat/Strategy
  ya confirmado. Un `ItemsControl` (no `ListView`, para que la selección
  no estorbe al hacer clic dentro de una `TextBox`) con una tarjeta por
  rol. La mayoría de los controles de cada fila (modelo, número de
  instancias, herramientas) se conectan desde el code-behind en vez de con
  `x:Bind`, porque `AgentRole` no es un tipo observable y ninguno de esos
  tres campos tiene un camino de `x:Bind` limpio sin un converter que no
  compensaba para una superficie tan pequeña: el modelo usa 4
  `ComboBoxItem` fijos con `Loaded`/`SelectionChanged` mapeando a mano
  (evita un converter `ClaudeModel`↔índice); el número de instancias es una
  `TextBox` numérica con `Loaded`/`TextChanged` (evita un converter
  int↔double para `NumberBox`); las herramientas son una `TextBox` de texto
  separado por comas con el mismo patrón `Loaded`/`TextChanged`. El nombre
  del rol sí usa `x:Bind Mode=TwoWay` normal (es un `string` simple, sin
  fricción de tipos) — deshabilitado para el orquestador, igual que en
  Swift, porque no genera fichero de subagente propio.
- **Bug real evitado antes de llegar a CI**: `Text="{x:Bind Role}"` (el
  `RoleKind` de cada fila) no habría compilado — `x:Bind` no convierte un
  enum a `string` implícitamente, el mismo motivo por el que ya existe
  `DiffLineKindToGlyphConverter` para `DiffLineKind`. Se detectó en
  revisión propia antes de pushear (no en Windows real esta vez) y se
  arregló con un converter nuevo del mismo patrón,
  `RoleKindToDisplayNameConverter.cs`.
- Botón nuevo "Edit current team…" en la flyout "Strategy" de Fase 1,
  habilitado solo cuando `StrategyPickerViewModel.HasSelectedStrategy` es
  verdadero (propiedad nueva, mismo patrón que `AdvisorViewModel.HasAdvice` —
  este port ya solo tiene conversores bool→algo, no uno para referencia
  nula). "Save" en el editor escribe por el mismo camino
  (`StrategyPickerViewModel.SelectAsync`) que elegir una plantilla o
  aplicar la sugerencia del Advisor, así que la cabecera y el modelo del
  orquestador quedan sincronizados igual.

Persistencia deliberadamente mínima, igual que el plan original ya
anotaba: la edición vive en el `Strategy` en memoria mientras la ventana
está abierta; no hay todavía una biblioteca de "mis plantillas guardadas"
con nombre — eso, si hace falta, es un seguimiento futuro aparte.

5 tests nuevos (`StrategyEditorViewModelTests.cs`: validación inicial,
`Revalidate()` recogiendo un error real hecho a mano sobre un rol,
`AutoFix()` sustituyendo la estrategia y volviendo a validar,
`IssueLines` distinguiendo error/aviso por el prefijo) — 345 en total.

Cubierto por tests unitarios y build/test local (345/345) — pendiente de
verificación de compilación en windows-latest CI (toda la superficie de
`StrategyEditorPage.xaml` es nueva: `ItemsControl` anidado, un
`DataTemplate` con `x:DataType="x:String"`, varios `Loaded`/eventos de
cambio) y, sobre todo, de prueba a mano en Windows real — es la pieza de
UI más grande de todo este bloque de trabajo, así que hay que contar con
al menos una ronda de "el founder lo prueba y aparece un bug de WinUI",
como en cada fase anterior.
