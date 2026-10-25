# Known limitations

These limitations are listed honestly so they can be planned for. None of them is hidden in the user interface.

## Verification

- **No live tenant verification yet.** Every Exchange Online, Microsoft Places and Microsoft Graph operation is implemented against Microsoft Learn documentation and module metadata, and exercised through the demo tenant and a stand-in PowerShell module. None has run against a real tenant in this environment. Treat all live operations as *live-verification-pending* until you complete the checklist in [VERIFICATION](VERIFICATION.md).
- **The MSI has been tested under Wine only.** Install, upgrade, the downgrade block and uninstall passed under Wine 9. Under Wine, the Start-menu shortcut went to the current user's Start menu and three empty folders remained after uninstall; on Windows the shortcut should go to the all-users Start menu and the folders should be removed. Confirm both on Windows (see [VERIFICATION](VERIFICATION.md)).
- **The Windows shell can't be run here.** `RoomOpsStudio.exe` is compiled and packaged on Linux. On Windows, version 1.0.0 stayed on the splash screen; 1.0.1 fixes the cause (see [VERIFICATION](VERIFICATION.md#start-up-fix-in-101)) but hasn't been confirmed on Windows yet. Splash, window theming, native dialogs and the WebView2 message bridge still need a smoke test on Windows.

## Authentication

- **WAM sign-in is unverified.** WAM in a hidden worker process may not have a parent window for its sign-in prompt. The default sign-in method for Exchange Online is therefore the system browser (`-DisableWAM`, available in ExchangeOnlineManagement 3.7.2 and later). Device code is available as a fallback.
- **Microsoft Graph always signs in through WAM.** Since Microsoft.Graph.Authentication 2.34, `Connect-MgGraph` uses WAM on Windows for its default client and takes the parent window from the PowerShell console window. A worker started without a window failed with *"A window handle must be configured"*. From 1.1.0 the Graph worker gets a hidden console window that is owned by the RoomOps window, so the WAM dialog opens over RoomOps. This has not yet been confirmed on Windows. If the dialog still doesn't appear, choose **Device code** as the Graph sign-in method on the Connections page.
- **Places uses its own sign-in.** `Connect-MicrosoftPlaces` is called without parameters, which is the documented usage, and the module's own sign-in experience applies. Account and scopes are not reported by the module.
- **Places tenant confirmation depends on existing rooms.** There is no documented tenant-context cmdlet for Places. RoomOps confirms the tenant by correlating Places rooms with Exchange room mailboxes. A tenant with no rooms in Places can't be confirmed, so Places writes stay blocked until at least one room exists in both services.

## Microsoft Places

- **Output property names are not fully documented.** They were mapped from the `MicrosoftPlaces` 2.1.12 output type (`PlaceDirectoryPresentationObject`). The output shape of `Get-PlacesSettings` is not documented; RoomOps shows the raw values it receives.
- **Desks and workspaces are out of scope.** The hierarchy explorer covers buildings, floors, sections and rooms.
- **Tenant-wide settings are never changed.** `Set-PlacesSettings` (Places Finder, building experiences) is shown as documentation only.
- **Places-first creation hands naming to the service.** With Places-first room creation, Places derives the mailbox name and alias from the address.

## Exchange

- **Synchronized mailboxes can't be renamed or resized here.** Name and capacity changes are blocked for synchronized room mailboxes and must be made on-premises.
- **Recipient lists are entered by address.** Group membership is never expanded; the nested-group warning is informational.
- **Folder permission matching is by name.** Permissions are matched to recipients by display name or SMTP address, as returned by `Get-MailboxFolderPermission`. A display name is resolved through an ANR search and must match exactly one recipient; otherwise the change is blocked.
- **Minimum duration only affects workspaces.** `MinimumDurationInMinutes` is documented to work only on workspace mailboxes; RoomOps shows this note when it is set.
- **No conversion back from room lists.** Converting a room list back to a regular distribution group is not offered.
- **No mailbox descriptions.** The creation wizard maps the "description" requirement to the Places **Label**; RoomOps does not set mailbox descriptions.

## Execution

- **Changes run one at a time.** Execution is sequential, with configurable pacing and exponential back-off for throttling. `Retry-After` hints are not parsed.
- **Provisioning waits are capped.** They are limited by *Provisioning wait* in Settings (default 180 s). Longer delays turn into resumable steps.
- **Revert covers settings only.** Revert is offered for settings changes (booking, working hours, mailbox properties, Places metadata, room-list membership, folder permissions), not for object creation, and created objects are never deleted.
- **Teams Rooms license detection is a pattern match.** It matches SKU part numbers (`*Teams_Rooms*`, `MEETING_ROOM`); the raw SKUs are shown for confirmation.

## Platform and packaging

- **Process execution policy is bypassed, not overridden.** The worker starts with `-ExecutionPolicy Bypass` at process scope. Machine or user policies set by Group Policy (for example `AllSigned`) take precedence, so sign the worker script with your organization's code-signing certificate in that case.
- **No code signing.** The executable, the worker script and the installer are not signed in this repository.
- **Each installer has its own build platform.** The MSI is built by `build.sh` with msitools (Linux, macOS or WSL); `build.ps1` doesn't build it. The per-user Inno Setup installer needs Inno Setup 6 on Windows.
- **The MSI has no setup wizard.** The only dialog set msitools provides requires a license agreement, so the MSI shows the standard Windows Installer progress window. The install folder can be set with `INSTALLDIR` on the command line. No desktop shortcut is created.
- **The MSI doesn't check for WebView2.** The application checks for the WebView2 Runtime when it starts and offers Microsoft's download page if it's missing.
- **Modules are not installed for you.** RoomOps shows the documented install commands but never installs PowerShell modules itself.

## User interface

- **English and Dutch.** The EN | NL switch at the top right changes the whole interface, including explanations, findings and previews produced by the backend. Error messages that come from Microsoft's PowerShell modules stay in the language the module reports them in. Change records keep the language they were written in, and the synthetic demo tenant's sample history and template examples are English.
- **One tenant at a time.** Switching tenants ends the current session.
- **Demo mode throttles on purpose.** It throttles every 15th write once so retry behavior is visible in demonstrations.

## Audit

- **The audit is local only.** The change history is application-local: stored on the administrator's device, not centralized and not tamper-proof. Use the Microsoft Purview unified audit log for authoritative records.
