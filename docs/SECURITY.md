# Security

RoomOps Studio executes privileged Microsoft 365 administration on behalf of the signed-in administrator. This document describes the trust boundaries, the controls at each boundary, and what is deliberately out of scope.

## Trust boundaries

| Boundary | Trusted side | Less-trusted side | Controls |
|---|---|---|---|
| WebView2 ↔ .NET host | .NET backend | Frontend bundle (could be affected by a rendering bug or injected content) | Closed method registry, typed strict deserialization, origin check, CSP, navigation lockdown, no host objects |
| .NET host ↔ PowerShell worker | .NET backend | Worker process and the Microsoft modules it loads | Private stdio pipes, per-process nonce, worker-side allowlist, splatting, output framing, timeouts |
| Worker ↔ Microsoft 365 | Microsoft 365 | — | Microsoft's modules and authentication (MFA, Conditional Access, PIM) |
| Tenant A ↔ tenant B / demo | Active session | Any other session | Single session, epoch, cache discard, plan binding, tenant verification per service |

## Frontend cannot run commands

- The bridge exposes a **closed set of typed methods** (`BridgeContract.Build`). There is no generic "run PowerShell" endpoint; unknown methods are rejected (`bridge.unknownMethod`).
- Requests are deserialized with `UnmappedMemberHandling.Disallow` and `RespectNullableAnnotations`; extra properties or wrong types are rejected before any handler runs.
- Mutations can only be requested as **intents** (desired state). The backend loads current state, validates, computes the diff and builds the commands itself.
- Executing a plan requires the plan id, the **tenant id the administrator confirmed**, and an explicit acknowledgement for removals. Plans are single-use, expire after 15 minutes and are discarded when the session changes.

## Command construction and execution

- `CommandCatalog` lists every cmdlet RoomOps may run and **every parameter** each may receive. `PsCommand.Create` refuses anything else. The catalog contains no cmdlet that deletes mailboxes, places or groups, and no tenant-wide setting writes (`Set-PlacesSettings` is excluded).
- Only parameters **documented on Microsoft Learn** are used for Places cmdlets, even where the module exposes more (verified by test).
- Microsoft Graph is **read-only**: `Invoke-MgGraphRequest` is allowed only with `GET` and only for `/v1.0/users/{id}?$select=…` and `/v1.0/users/{id}/licenseDetails` (checked in .NET and again in the worker).
- The worker enforces the **same allowlist independently** (a test fails the build if the two drift), resolves names to cmdlets/functions only, converts values to primitives, string arrays or switches (structured values are refused), and invokes with **splatting**. Request content is never parsed as script. Batch requests (`invoke-many`) are limited to `Get-` commands, and each parameter set in a batch is checked the same way. A contract test passes hostile strings (`'; Remove-Item …; $(…)`) through a real PowerShell process and verifies they arrive literally.
- Preview scripts are rendered from the same `PsCommand` objects with single-quote escaping (including typographic quotes PowerShell treats as quotes).

## Worker process isolation

- One `pwsh` process per service (Exchange, Places, Graph), started with `-NoLogo -NoProfile -NonInteractive`. Profiles are not loaded; interactive prompts fail instead of reading from the protocol stream.
- Communication uses the process's **own anonymous stdio pipes** — there is no TCP port or named pipe for another process to reach.
- A random 256-bit **nonce** is passed in the environment and removed by the script on start-up. Every request must carry it; every response frame must carry it. Unframed output is treated as diagnostic text and redacted.
- The worker script is embedded in the application, written to the user's profile with a content hash in its name, and re-verified before launch.
- Timeouts terminate the worker. Disconnect and application exit call `Disconnect-*` and terminate the process, clearing in-memory tokens held by the Microsoft modules.
- `-ExecutionPolicy Bypass` applies to the process scope only. Machine or user policies set by Group Policy take precedence; organizations that enforce `AllSigned` must sign the worker script (see limitations).

## Authentication and authorization

- Sign-in uses Microsoft's modules: `Connect-ExchangeOnline` (browser, WAM or device code), `Connect-MicrosoftPlaces`, `Connect-MgGraph -Scopes User.Read.All`. MFA, Conditional Access and PIM-elevated roles are handled by Microsoft's sign-in experience.
- **RoomOps never sees, stores or logs passwords or tokens.** Tokens stay inside the worker processes' Microsoft libraries.
- Each service connects separately and shows its own state; signing in to one service never implies a session for another.
- Least privilege: Exchange Administrator (or narrower custom role groups), Places Administrator / TenantPlacesManagement, and delegated `User.Read.All` for Graph. Global Administrator is never required.
- Permission errors are classified and explained; RoomOps does not try alternative paths around them.

## Tenant isolation

- One session at a time. Switching tenant or between demo and live **disposes every worker, discards every cache and pending plan, and increments the session epoch**. Frontend query keys include the epoch and the query cache is cleared on change.
- Exchange anchors the tenant id. Graph must report the same tenant id or it is disconnected. Places is confirmed by correlating its room objects with the tenant's room mailboxes; if that is impossible, **Places writes are blocked**.
- Every target re-checks the session epoch and tenant immediately before writing.
- History, exports, revert and resume are scoped to the active tenant.

## Hybrid environments

Directory-synchronized objects are detected (`IsDirSynced`). Name and capacity changes to synchronized room mailboxes are **blocked with an explanation** that they must be made in on-premises Exchange. Places address properties that Microsoft documents as not updatable for synchronized rooms are never sent for rooms (they are building-level fields in RoomOps).

## Desktop shell (WebView2)

- The UI is served from the application folder through `SetVirtualHostNameToFolderMapping("app.roomops.example", …, Deny)` — no local web server and no remote content.
- Navigation outside that origin, new windows, downloads and permission requests (camera, location, …) are blocked.
- Bridge messages are accepted only from the application origin.
- Release builds disable DevTools, default context menus, browser accelerator keys, script dialogs, autofill and password saving; host objects are not allowed.
- The production bundle carries a Content Security Policy (`default-src 'self'`, no remote scripts, `object-src 'none'`, `frame-ancestors 'none'`).
- External links are limited to `https` Microsoft documentation and admin portals and open in the system browser.
- The application requests no elevation (`asInvoker`). The MSI installs per machine into Program Files, so only administrators can change the installed files; it needs administrator rights during installation only. The Inno Setup installer installs per user without elevation.

## Development host

`RoomOps.DevHost` exists for development and automated UI tests. It binds to `127.0.0.1` only, requires a random per-run token on the WebSocket (constant-time comparison), checks the `Origin` header, sends hardening headers, and **refuses live Microsoft 365 connections unless started with `--allow-live`**. It is not shipped in the Windows package.

## Data at rest

| Data | Location | Protection |
|---|---|---|
| Change history, templates | `%LOCALAPPDATA%\RoomOps Studio\roomops.db` | User profile ACLs. Not encrypted, not tamper-proof (documented as an application-local log). |
| Preferences, known tenants (id, domain, last account) | `settings.json` | User profile ACLs. No secrets. |
| Logs | `logs\` (14 days) | Token-like strings are redacted by a Serilog enricher; worker output is redacted before logging. |
| WebView2 profile | `WebView2\` | Contains no credentials (autofill and password saving disabled). |

Exports (CSV, JSON, HTML) never contain tokens. CSV exports neutralize spreadsheet formula prefixes in tenant-provided text.

## Settings hardening

The PowerShell path setting accepts only `pwsh.exe` / `pwsh`, so the settings endpoint can't be used to launch another executable.

## Demo mode

Demo sessions create no worker processes and cannot reach Microsoft 365. Demo audit records are kept in memory only and never written to the persistent history. Demo mode is labeled in a persistent band, in the tenant switcher, in every preview and in every report.

## Out of scope / not implemented

- No password changes, license assignments, Conditional Access changes or device settings (Teams Rooms readiness is read-only).
- No automatic tenant-wide remediation and no tenant-wide Places settings changes.
- No code signing of the executable or worker script in this repository (organizations should sign before distribution).
- The local audit log is not a substitute for the Microsoft Purview unified audit log.

## Reporting

Treat any way to make the backend run a command or parameter outside `CommandCatalog`, write to a tenant other than the one confirmed in the preview, or expose a token as a security defect.
