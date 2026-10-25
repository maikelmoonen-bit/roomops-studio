# Verification

This document records what has been verified, how, and with what result. It ends with the checklist for what still needs a Windows machine and an authorized Microsoft 365 tenant.

## Status

| Area | Status | Evidence |
|---|---|---|
| Domain rules (booking, working hours, hierarchy, validation) | Verified, automated | 74 unit tests |
| Change pipeline: preview, execute, reread, verify, audit | Verified against the demo tenant | 20 pipeline tests, 17 UI flows |
| PowerShell worker protocol and command safety | Verified with a real PowerShell 7.4 process and a stand-in module | 14 worker contract tests, 24 command tests |
| Bridge, contracts and persistence | Verified, automated | 8 bridge and persistence tests, generated-contract check |
| User interface in demo mode | Verified in Chromium through the development host | 17 Playwright flows, 72 screenshots |
| Windows package | 1.0.0 **hung at the splash screen on Windows** (see [start-up fix](#start-up-fix-in-101)); 1.0.1 built, **not yet confirmed on Windows** | `artifacts/RoomOpsStudio-1.0.1-win-x64.zip` |
| MSI installer | Built; install, upgrade, downgrade block and uninstall tested **under Wine 9**, not yet on Windows | `artifacts/RoomOpsStudio-1.0.1-win-x64.msi`, [results below](#msi-installer) |
| Inno Setup installer | **Not built** (needs Windows and Inno Setup 6) | — |
| Live Exchange Online, Microsoft Places, Microsoft Graph | **Pending** | Checklist below |

## Final automated run

Run on 25 October 2026 with `./build.sh --e2e`, starting from an empty UI-test data folder. Exit code 0.

Environment: Linux 6.8 (x64), .NET SDK 10.0.402, Node.js 26.8.2, PowerShell 7.4.13, Playwright 1.64.1 with Chromium.

| Stage | Result |
|---|---|
| Bridge contract check | `contracts.gen.ts` is up to date |
| TypeScript type check (strict) | Passed |
| Frontend unit tests (Vitest) | 9 of 9 passed |
| Production bundle (Vite) | Built |
| .NET build, warnings treated as errors | 0 warnings, 0 errors |
| `RoomOps.Domain.Tests` | 74 of 74 passed |
| `RoomOps.Application.Tests` | 24 of 24 passed |
| `RoomOps.Infrastructure.Tests` | 42 of 42 passed |
| Playwright UI tests (demo mode) | 17 of 17 passed |
| Package | `RoomOpsStudio-1.0.0-win-x64.zip`, self-contained win-x64 |

Screenshots were then captured with `npx playwright test e2e/screenshots.spec.ts`: 18 screens in dark and light themes at 1920×1080 and 1366×768, 72 images in `web/e2e/screenshots/`. Every screen was reviewed during development and the issues found were fixed (truncated text, misaligned cards, layout at 1366 px, localized time-zone names, browser dialogs replaced by in-app dialogs). The final capture was spot-checked.

## MSI installer

Built on 25 October 2026 by `./build.sh` with wixl 0.103 (msitools); all tests in that run passed again. The package is 65 MB and contains 335 files.

Package contents were checked with `msiinfo`:
- x64 package (`Template x64;1033`), every component marked 64-bit;
- per-machine (`ALLUSERS=1`);
- upgrade rows that replace older versions and block downgrades;
- component GUIDs that stay the same between builds.

It was then installed silently (`msiexec /qn`) in an empty Wine 9.0 prefix:

| Step | Result |
|---|---|
| Install 1.0.0 | Exit 0. The installed files match the publish folder exactly. One uninstall entry, the HKLM key-path value and the Start-menu shortcut are present. |
| Install 1.0.1 over it | Exit 0. 1.0.0 was removed automatically; one entry for 1.0.1 remains. |
| Install 1.0.0 over 1.0.1 | Refused (exit 1603, "A newer version of RoomOps Studio is already installed."). 1.0.1 stays installed. |
| Uninstall | Exit 0. Files, shortcut, registry value and uninstall entry are removed. |

The 1.0.1 package was tested the same way. It upgrades 1.0.0, and a rebuilt 1.0.1 replaces an installed 1.0.1 instead of installing a second copy; both times one entry remained. Installing 1.0.0 over it was refused, and uninstall removed everything.

Two results differ from what Windows should do, and need confirming on Windows:
- Wine put the shortcut in the current user's Start menu. With `ALLUSERS=1`, Windows uses the all-users Start menu.
- Three empty folders (`RoomOps Studio\runtimes\win-x64`) remained after uninstall. Windows Installer removes empty folders it created.

Building the package exposed a wixl 0.103 defect: it writes the `HKMU` registry root as an invalid value. The package uses `HKLM`, which is correct for a per-machine install.

## What the automated tests cover

**Change pipeline** (`ChangePipelineTests`, demo command runner):
- a booking change is previewed, executed, read back, verified and audited;
- unchanged values produce no write;
- a preview made stale by another change is detected before writing;
- plans are bound to their tenant session and execute at most once;
- invalid desired state is blocked and never executed, and removals need acknowledgement;
- room-list conversion requires room-only membership;
- Exchange-first and Places-first room creation wait for provisioning, defer what can't run yet and resume later;
- templates apply only their own settings and honor exceptions;
- revert restores recorded values, and retry re-plans only unfinished items;
- unresolved and ambiguous recipients are detected and remediated;
- folder permissions target the localized calendar folder;
- Places reparenting follows hierarchy rules and reports propagation;
- synchronized mailbox properties are not changed in the cloud;
- bulk imports validate rows and plan per object;
- working hours are validated and verified;
- Places and Graph sessions require an Exchange anchor.

**PowerShell worker** (`WorkerContractTests`, runs the shipped worker script in `pwsh` with the stand-in module `FakeServices.psm1`):
- hostile strings (`'; Remove-Item …; $(…)`) arrive as literal values;
- switches bind correctly, including an explicit `false`;
- output is normalized to primitives, arrays and ISO dates;
- service errors are classified;
- the worker enforces its own allowlist, which must match `CommandCatalog`;
- requests without the session nonce are rejected;
- a crashed worker fails pending requests and reports that it stopped;
- device-code instructions are surfaced;
- token-like text in worker output is redacted;
- only a PowerShell 7 executable is accepted.

**Command catalog** (`CommandTests`): no command or parameter outside the catalog can be created; no undocumented Places parameter is in the catalog; no catalog command deletes objects; Graph access is limited to the allowlisted reads; script rendering escapes quotes and never interpolates; service error messages are classified (throttling, not found, permission, conflict, authentication); stored commands are re-checked against the allowlist and tampered ones are rejected.

**Bridge and persistence** (`BridgeAndPersistenceTests`): unknown methods, malformed messages and wrongly typed parameters are rejected; polymorphic intents round-trip; live connections are refused when the host disallows them; external links are limited to Microsoft documentation and portals; audit records are scoped by tenant; settings persist without secrets.

**User interface** (`web/e2e/app.spec.ts`, demo mode):
- welcome screen and demo labeling;
- every navigation entry opens a populated page;
- command palette and theme switching;
- room search, quick filters and card view;
- room detail separating Exchange and Places objects;
- the explanation for synchronized mailboxes;
- a booking window change from edit to verification;
- delegate approval without delegates is blocked;
- the scenario simulator;
- room creation with provisioning stages;
- drag-and-drop between room lists;
- the removal acknowledgement;
- remediation from the Validation Center;
- change history with executed commands;
- bulk CSV validation;
- no horizontal overflow at 1366×768.

### What the tests can't show

The demo tenant and the stand-in module reproduce the documented behavior of Microsoft's cmdlets. They can't show:
- whether a real tenant accepts every parameter combination RoomOps sends;
- the exact output property names the current module versions return;
- real provisioning and propagation times;
- the exact wording of real permission and throttling errors;
- how sign-in behaves inside a background PowerShell process.

The WPF and WebView2 shell compiles and packages. It can't be started in this environment.

## Start-up fix in 1.0.1

On Windows, version 1.0.0 stayed on the splash screen ("Starting the interface…") indefinitely. The cause was a deadlock:
- The main window was kept hidden until the interface reported that it had rendered.
- The WPF WebView2 control can't finish initializing in a hidden window. It waits for its own child window, and WPF creates that only once the window is shown: `Window` sets its content on `Show()`, not on `EnsureHandle()`. This was confirmed by decompiling WebView2 SDK 1.0.4314.35 and the WPF runtime that ships with the app.
- The 20-second fallback was scheduled after initialization, so it never ran.

Version 1.0.1 changes the start-up:
- The WebView2 Runtime is checked before any window opens.
- The main window is shown before WebView2 starts. The splash stays above it until the interface is ready.
- The fallback timer starts first, so it always runs.
- WebView2 start-up is limited to 60 seconds, after which an error message names the log folder.
- Each start-up stage, and any failure to load the interface, is written to the log.

The fix builds and is packaged. It has not yet been confirmed on Windows.

## Live verification checklist

Use a test tenant, or test rooms in a tenant where changes are acceptable. Sign in with accounts that hold the roles in the [capability matrix](CAPABILITY-MATRIX.md), not Global Administrator. Record the date, the module versions shown on the Connections page and the result of each row. When a row passes, the matching capability in the capability matrix can be marked as live-verified.

### A. Windows shell (no tenant needed)

- [ ] Unzip the package and start `RoomOpsStudio.exe`. The splash screen appears, then the main window. The title bar follows the dark or light theme.
- [ ] In demo mode, change a booking window, apply it and find it in Change History.
- [ ] Start the application a second time. A message says that RoomOps Studio is already running.
- [ ] Export the room list as CSV and a change report as HTML. The Windows Save dialog appears and the files open correctly.
- [ ] Import a CSV on the Bulk operations page through the Windows Open dialog.
- [ ] Open a Microsoft documentation link. It opens in the default browser, not in the application.
- [ ] Check `%LOCALAPPDATA%\RoomOps Studio\logs`. A log exists and contains no tokens.
- [ ] Double-click the MSI. Windows asks for administrator approval, then shows the installation progress. **RoomOps Studio** appears in the Start menu of every user (`C:\ProgramData\Microsoft\Windows\Start Menu\Programs`) and in *Installed apps* with its icon.
- [ ] From an elevated prompt, run `msiexec /i RoomOpsStudio-<version>-win-x64.msi /qn /l*v install.log`. The exit code is 0 and the log has no errors.
- [ ] Build a higher version (raise `<Version>` in `Directory.Build.props`) and install it over the first. One entry remains in *Installed apps*, with the new version.
- [ ] Uninstall. `C:\Program Files\RoomOps Studio` is removed completely, and `%LOCALAPPDATA%\RoomOps Studio` is kept.
- [ ] Optionally, deploy the MSI through Intune or Configuration Manager to a test device.
- [ ] On Windows with Inno Setup 6, run `.\build.ps1`. The setup installs for the current user and reports a missing WebView2 runtime if there is one.

### B. Connections

- [ ] The Connections page shows the installed PowerShell 7 and module versions.
- [ ] Connect Exchange Online with the system browser, including MFA. The tenant name and tenant id are correct.
- [ ] Connect Exchange Online with WAM. Note whether the sign-in window appears (known risk).
- [ ] Connect Exchange Online with device code. The code is shown in the application.
- [ ] Connect Microsoft Places. Tenant confirmation reports *Correlated*.
- [ ] Connect Microsoft Graph with *Windows sign-in*. The WAM dialog opens over RoomOps and no console window appears (1.1.0 fix for "A window handle must be configured").
- [ ] Connect Microsoft Graph with *Device code*. The code is shown in the application.
- [ ] A failed Graph sign-in leaves Exchange Online connected.
- [ ] Connect Microsoft Graph. Signing in with an account from another tenant is refused.
- [ ] Disconnect Exchange. No `pwsh` processes started by RoomOps remain in Task Manager.
- [ ] Start the application. Nothing is written: the Microsoft Purview audit log shows no changes from the start-up.

### C. Reads

- [ ] The room count matches `Get-EXOMailbox -RecipientTypeDetails RoomMailbox -ResultSize Unlimited` in your own session.
- [ ] Room lists and their members match `Get-DistributionGroup -RecipientTypeDetails RoomList` and `Get-DistributionGroupMember`.
- [ ] **Run scan** reads every room; booking modes and the dashboard metrics fill in.
- [ ] Booking settings of three rooms match `Get-CalendarProcessing`.
- [ ] Working hours of three rooms match `Get-MailboxCalendarConfiguration`.
- [ ] Delegates & access shows the calendar folder permissions, including for a mailbox with a localized calendar folder name (for example "Agenda").
- [ ] The Buildings & floors explorer matches the Places hierarchy in `Get-PlaceV3`. Label, tags, devices, capacity and accessibility map to the right fields.
- [ ] The Microsoft Places page shows the `Get-PlacesSettings` values.
- [ ] Teams Rooms readiness detects the license of a Teams Rooms resource account.

### D. Writes on a test room

For each row:
1. Preview the change and compare the Technical Execution Preview with what you expect.
2. Apply it and check that the outcome is **Verified**, or **Accepted, propagation pending** for Places.
3. Check the result in the admin center or PowerShell.
4. Use **Restore previous values** where it is offered.

- [ ] Booking window and maximum duration (`Set-CalendarProcessing`).
- [ ] The *Delegate approval required* model with one delegate. A request from a test user reaches the delegate.
- [ ] The *Selected users only* model with five users. One of them books automatically, another user is declined, and after rereading the room shows the same model.
- [ ] Opening a room with a non-matching configuration shows *Custom configuration* and proposes no changes.
- [ ] Automatic-booking and request lists with a user and a group.
- [ ] Working hours with a non-default time zone.
- [ ] Display name and capacity of a cloud-only room. A synchronized room is blocked with an explanation.
- [ ] Add, change and remove a calendar folder permission. Removal requires the acknowledgement.
- [ ] Change the role of an existing folder entry from the *Access rights* column, including Default, and demote an Editor with the Delegate flag to LimitedDetails.
- [ ] *Choose a person* finds users by first name, last name and address.
- [ ] Create a room list, rename it, add and remove a member. Convert a test distribution group that contains only rooms.
- [ ] Create a building, floor and section in Places and edit them. Move a test room to another floor. After propagation, **Verify again** reports Verified.
- [ ] Create a room Exchange-first, with a room list and a Places location. The provisioning stages complete or resume from Change History.
- [ ] Create a room Places-first.
- [ ] Apply a template to two rooms, with one setting excluded for one room.
- [ ] Import a three-row CSV in which one row is invalid. Only the valid rows are planned.
- [ ] Remediate one finding from the Validation Center.

### E. Safety under real conditions

- [ ] With an account without a Places role, a Places change fails with a permission explanation. Nothing else is attempted.
- [ ] Preview a booking change, change the same setting in the Exchange admin center, then apply. The result is **Stale** and nothing is written.
- [ ] Preview a change, switch tenant, then try the old preview. It is refused.
- [ ] Leave the application idle until the session expires. Reconnecting is offered and works.
- [ ] Copy a Technical Execution Preview script and run it in your own session. It runs without edits.
- [ ] If throttling occurs during a scan or a bulk run, the back-off and retry appear in the progress and in the history.

## Reproducing the automated run

```bash
./build.sh --e2e                                   # Linux or macOS; builds the MSI when wixl is installed
cd web && npx playwright test e2e/screenshots.spec.ts
```

```powershell
.\build.ps1 -E2E                                   # Windows
```

The UI tests start the development host in demo mode on `127.0.0.1:5399` and store their data in `web/e2e/.data`. Delete that folder for a clean run.
