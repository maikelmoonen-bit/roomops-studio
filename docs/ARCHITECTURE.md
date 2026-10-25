# Architecture

RoomOps Studio is a Windows desktop application whose user interface is a React application hosted in WebView2, and whose backend is a .NET 10 process that talks to Microsoft 365 through isolated PowerShell 7 worker processes.

```
┌──────────────────────────────── RoomOpsStudio.exe (WPF, .NET 10) ───────────────────────────────┐
│                                                                                                  │
│  WebView2 ── https://app.roomops.example/  (virtual host → ./wwwroot, no web server)             │
│  │  React UI: TanStack Query, plan-review flow, typed bridge client (contracts.gen.ts)          │
│  │                                                                                               │
│  │ postMessage (string JSON)        ▲ PostWebMessageAsString (responses + events)               │
│  ▼                                  │                                                            │
│  BridgeDispatcher ── closed registry of typed methods ── origin check, strict deserialization   │
│  │                                                                                               │
│  Application services ── ChangePlanner ── PlanService ── ExecutionEngine ── ValidationService   │
│  │                     gateways (Exchange / Places / Graph) build PsCommand objects              │
│  │                                                                                               │
│  SessionManager ── one tenant session, per-session cache, epoch                                  │
│  │        │                                                                                      │
│  │        ├── Live:  ICommandRunner = PowerShellWorker ── stdin/stdout ──▶ pwsh (Exchange)      │
│  │        │                                              stdin/stdout ──▶ pwsh (Places)         │
│  │        │                                              stdin/stdout ──▶ pwsh (Graph, read)    │
│  │        └── Demo:  ICommandRunner = DemoCommandRunner (in-memory synthetic tenant)            │
│  │                                                                                               │
│  SQLite (audit, templates) · settings.json · Serilog (redacted) — all in %LOCALAPPDATA%          │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## Layers

| Project | Responsibility | Depends on |
|---|---|---|
| `RoomOps.Domain` | Booking-policy model and **descriptor table** (one entry per `Set-CalendarProcessing` parameter: label, range, help, AutoAccept-only flag, formatting, JSON patching, parameter binding), validator, plain-language explainer, scenario simulator, working-hours model and Windows time-zone catalog, Places hierarchy rules, room-list rules, templates, validation engine. | nothing |
| `RoomOps.Application` | `CommandCatalog` (allowlist) and `PsCommand`; session abstractions; gateways that map cmdlet output to domain objects; **ChangePlanner** (intent → per-target plan); **PlanService** and **ExecutionEngine**; read services (rooms, delegates, places, validation, dashboard, history, templates, bulk import, Teams Rooms readiness); HTML report renderer. | Domain |
| `RoomOps.Infrastructure` | `PowerShellWorker` (process host, protocol, nonce), embedded `RoomOps.Worker.ps1`, `SessionManager`, demo tenant and command interpreter, SQLite stores, JSON settings. | Application |
| `RoomOps.Bridge` | `BridgeRegistry` (every method and event, typed), `BridgeDispatcher`, `BridgeEventSink`, `LiveConnectionGate`, host abstraction `IHostServices`. | Application |
| `RoomOps.Hosting` | Composition root shared by both hosts; Serilog with a redacting enricher; PowerShell environment probe. | Infrastructure, Bridge |
| `RoomOps.Desktop` | WPF shell: splash, WebView2 hardening, virtual host mapping, origin-checked message bridge, native file dialogs, title-bar theming, single-instance guard. | Hosting |
| `RoomOps.DevHost` | Loopback ASP.NET Core host for browser development and Playwright: serves the bundle and the bridge over a token-protected WebSocket. | Hosting |

The frontend lives in `web/` and is described below.

## The change pipeline

Every mutation, from a single toggle to a 1,000-row import, uses one pipeline. The frontend can only submit **intents** (desired state); it can never submit commands.

| Stage (requirement) | Where |
|---|---|
| 1. Load current configuration | Each planner reads fresh state (`ChangePlanner.Plan*Async`) |
| 2. Validate target identity and tenant | `PlanService.CreateAsync` requires an active session; planners resolve targets in the session's inventory |
| 3. Validate permissions | Service errors classified as `PermissionDenied` and explained; hybrid (directory-synced) objects blocked where Microsoft documents on-premises authority |
| 4. Generate desired state | Intent → desired object (full policy, patch, template with exclusions, wizard) |
| 5. Calculate differences | Descriptor-based diff; recipient lists compared by resolved SMTP, order-insensitive. Unchanged values never produce a write ("No change required") |
| 6. Check dependencies and conflicts | `BookingPolicyValidator`, `WorkingHoursValidator`, hierarchy rules, uniqueness checks, room-list conversion assessment |
| 7. Present execution preview | `PlanPreview`: target tenant, per-object changes, issues, notes and the exact script (`PsCommand.ToScript`) |
| 8. Obtain confirmation | `plans.execute` requires the plan id, the **tenant id the administrator saw**, and an explicit acknowledgement when removals are included. Plans expire after 15 minutes and execute once |
| 9. Execute | `ExecutionEngine`: sequential, paced, bounded retries for throttling/transient errors, provisioning waits for newly created objects, deferral of steps that need a not-yet-provisioned object |
| 10. Reread | Each target's `Verify` callback reads the changed values back |
| 11. Compare | `VerificationResult`: Verified, PendingPropagation (Places documented delays), NotVerified, Mismatch |
| 12. Record | `AuditRecord` per target with before/after, executed scripts, outcome, verification, and a serialized compensating intent when one exists |

**Stale previews:** each target stores a fingerprint of the state it was planned against. Immediately before writing, the engine re-reads and compares; a difference marks the target *Stale* and nothing is written.

**Tenant binding:** a plan carries the tenant id and the session epoch. Switching tenant (or demo ↔ live) increments the epoch, clears every cache and every pending plan; executing an old plan fails with *TenantMismatch* before anything is sent.

**Recovery:** compensating intents restore the recorded before-values and are re-planned against the current state. Created objects are never deleted automatically. Steps that waited too long for provisioning are stored (as allowlisted command data) and can be resumed from Change History after revalidation.

## Sessions and services

`SessionManager` holds exactly one session: **None**, **Demo** or **Live**.

- **Exchange Online** connects first and anchors the tenant identity (`Get-ConnectionInformation` → `TenantID`).
- **Microsoft Graph** connects with `-TenantId <anchored tenant>` and must report the same tenant (`Get-MgContext`), otherwise the connection is closed.
- **Microsoft Places** has no documented tenant-context cmdlet. The session is confirmed by **correlation**: Places room objects must carry addresses of room mailboxes in the anchored tenant. If correlation is impossible the service is marked *Unverified* and every Places write is blocked.

Each service runs in its **own worker process**, which isolates assembly conflicts between Microsoft modules, keeps sessions independent (a failure or expiry affects one service), and makes recovery explicit: reconnecting starts a fresh process. Disconnecting Exchange ends the whole session; disposing a worker sends `Disconnect-*` and terminates the process, clearing privileged state.

## PowerShell worker protocol

`pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File RoomOps.Worker.<hash>.ps1`

- **Transport:** the process's own redirected stdin/stdout (anonymous pipes). There is no listening socket or named pipe another process could connect to.
- **Authentication:** a random 256-bit nonce is passed in the environment and removed by the script at start-up. Every request must carry it; every response frame is prefixed `@@RO1:<nonce>:`. Output without the prefix (module banners, device-code instructions) is treated as diagnostic text, redacted and never parsed as a response.
- **Dispatch:** `{op: "invoke", command, parameters}`. The worker checks the command and **each parameter name** against its own allowlist (identical to `CommandCatalog`, enforced by a test), converts JSON values to primitives/string arrays/switches (structured values are rejected), resolves the name to a module cmdlet or function (never aliases or scripts), and invokes it with **splatting**. No request content is ever parsed as PowerShell.
- **Batches:** `{op: "invoke-many", command, items, throttle}` runs one `Get-` command for many parameter sets (the configuration scan reads 20 rooms per request). Every parameter set passes the same allowlist checks. Up to `throttle` items (at most 8) run at the same time with `ForEach-Object -Parallel`. Each runspace imports the module that `Connect-ExchangeOnline` generated, so all runspaces share the one signed-in connection. A failing item is reported in its own slot and the other items still run. Without that module, the items run one at a time.
- **Output:** objects are normalized to JSON (primitives, ISO dates, arrays of strings, nested objects stringified); warnings are returned separately.
- **Errors:** message, FullyQualifiedErrorId and category are returned and classified (`NotFound`, `Throttled`, `PermissionDenied`, `Authentication`, `Conflict`, `Transient`, `Validation`).
- **Failure handling:** a request timeout terminates the worker; an exited worker fails pending requests with *Authentication* and marks the service *Failed* so the UI offers reconnection.

The worker contract tests (`tests/RoomOps.Infrastructure.Tests/WorkerContractTests.cs`) run this exact script in a real PowerShell 7 process with a stand-in module that echoes bound parameters, proving literal binding of hostile input, switch handling, allowlist and nonce enforcement, error classification and crash handling.

## Demo mode

Demo mode replaces the command runners, not the application. `DemoCommandRunner` interprets the same catalog commands against `DemoTenant` and returns objects with the real cmdlets' property names, so gateways, planners, verification and auditing run unchanged. It simulates mailbox provisioning delays, Places synchronization delays, localized calendar folders, ambiguous and unresolved recipients, hierarchy-rule errors and occasional throttling. Demo audit records live in a volatile in-memory store. A demo session has no worker processes, so it cannot reach Microsoft 365.

## Bridge and contracts

`BridgeContract.Build()` registers every method with its request and response type and its kind (query/command), plus every event. `tools/RoomOps.ContractGen` reflects over that registry and writes `web/src/bridge/contracts.gen.ts` (interfaces, string-literal enums, polymorphic intent unions, a typed method map). The build fails when the generated file is out of date. The dispatcher deserializes parameters strictly (unknown properties and nullability violations are rejected) and maps `RoomOpsException` kinds to structured errors the UI explains.

## Frontend

- **Stack:** React 19, TypeScript (strict), Vite 7, Tailwind CSS 4 tokens, Radix primitives (shadcn-style components written for this product), TanStack Query/Table/Virtual, Motion, Recharts, cmdk, sonner, Lucide.
- **State:** `SessionProvider` (session snapshot, settings, theme/density/motion, event wiring). All query keys start with the session epoch, and a new epoch clears the query cache, so data can't cross tenants.
- **Plan flow:** `PlanFlowProvider` is the only path to execution. Any screen calls `review(intent)`; the provider shows the preview, collects confirmation, executes, streams progress and shows verification.
- **Design system:** graphite surfaces, signal-cyan accent, status colors reserved for meaning, Barlow Semi Condensed for titles/metrics/room plates, Inter for UI, JetBrains Mono for identifiers. Compact/comfortable density, reduced motion, dark and light themes.
- **Localization:** English is the source language. Components call `t("English text", { vars })` (`tn` for plurals, `tr` for sentences with embedded elements); the Dutch catalog in `web/src/lib/locales/nl/` is keyed by the English text, and a unit test fails when a string used in the interface has no Dutch entry or a translation changes its placeholders. The backend writes its own sentences (explanations, findings, previews, errors) with `Text.L(english, dutch)` in the language stored in the settings; the EN | NL switch updates that setting and refetches every query.

## Cross-platform

Everything except `RoomOps.Desktop` targets `net10.0` and runs on Windows, Linux and macOS. The worker runs on any platform with PowerShell 7. The development host demonstrates the UI and backend on Linux. A future macOS/Linux shell would replace only the host (for example with a WebView-based shell) and reuse the bridge unchanged.

## Data locations

`%LOCALAPPDATA%\RoomOps Studio\` (or `$ROOMOPS_DATA_DIR`):
- `roomops.db` — SQLite: audit records, templates, template assignments.
- `settings.json` — preferences only.
- `logs\roomops-YYYYMMDD.log` — 14 days retained, token-like text redacted.
- `worker\RoomOps.Worker.<hash>.ps1` — materialized worker script, verified by hash before launch.
- `WebView2\` — WebView2 user data folder.
