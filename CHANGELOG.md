# Changelog

All notable changes to RoomOps Studio. Installers for 1.2.0 and later are attached to the [GitHub releases](https://github.com/maikelmoonen-bit/roomops-studio/releases). The repository's history starts at 1.2.0, so earlier versions are described here but their source isn't in git.

## 1.4.0 — 2026-10-25

### Changed
- **Booking policies, Resource delegates and Working hours** use the same room table as **Rooms**: sortable, resizable columns and a search box, with a row opening that room's editor. Each page shows its own columns:
  - *Booking policies:* booking mode, booking window, maximum duration, recurring meetings, working hours only, delegates and validation.
  - *Resource delegates:* booking mode, the booking delegates by name, forward requests to delegates and validation.
  - *Working hours:* time zone (with a label when it isn't in your baseline), work days, hours, working hours only and location.
- The configuration scan's progress bar advances per room instead of in steps of 20. The PowerShell worker reports every completed read while a batch runs.

## 1.3.0 — 2026-10-25

### Changed
- The configuration scan is about three times faster. It reads 20 rooms per worker request, five at a time, in runspaces that share the signed-in Exchange Online connection, so there are no extra sign-ins.
- Booking policies, Resource delegates and Working hours no longer show a room list in a left column. Without a room selected, they show all rooms full-width. The *Delegate issues* and *Rooms with booking delegates* panels were removed; those findings remain in the Validation center.
- Working hours groups rooms by time zone and shows each room's hours.

### Fixed
- *Choose a person* and the other recipient pickers found nobody. In ExchangeOnlineManagement 3.10, `Get-EXORecipient -Anr` ignores the search text, so search now uses `Get-Recipient -Anr`.
- On Windows, names with accented letters were garbled (for example "Entrep�t"). The worker wrote its output in the console's OEM code page; it now escapes all non-ASCII characters.
- In Dutch, the working-hours editor labeled open days "Openen" instead of "Werkdag".

## 1.2.0 — 2026-10-25

### Added
- Change the access rights of existing calendar folder entries in place, including Default and Anonymous (up to LimitedDetails). Demoting a calendar delegate removes the delegate status first (`-SharingPermissionFlags None`).
- People who also have Full Access to the room mailbox are marked in the folder permission list. Full Access and Send As entries show display names.
- A clearer explanation of the view-only mailbox-level permissions (Full Access, Send As, Send on Behalf).

### Fixed
- Recipient lookups now request the display name and address explicitly, because the default property set of `Get-EXORecipient` doesn't include them.
- Changing or removing a folder permission for a user listed by display name could be blocked as "could not be resolved to a single recipient".

## 1.1.0 — 2026-10-25

### Added
- **English and Dutch:** an EN | NL switch at the top right changes the whole interface, including backend explanations, findings, previews and the HTML change report.
- **Booking access models:** *Everyone*, *Selected users only*, *Delegate approval required* and *Hybrid*. Each model sets and clears the underlying `Set-CalendarProcessing` permissions and is recognized again when a room is opened; other configurations show as *Custom configuration*. A live summary shows who may book and who approves, and every parameter is still available under a collapsible *Advanced booking configuration*.
- Microsoft Graph has its own sign-in method on the Connections page, with *Device code* as a fallback.

### Fixed
- Connecting Microsoft Graph failed with "A window handle must be configured". Since Microsoft.Graph.Authentication 2.34, sign-in uses Windows Account Manager, which needs a window. The Graph worker now has a hidden console window owned by the RoomOps window.
- A failed Graph sign-in was reported as an Exchange problem and disconnected Exchange Online.

## 1.0.1 — 2026-10-25

### Fixed
- The application hung on the splash screen on Windows. WebView2 can't start in a hidden window, and the main window stayed hidden until the interface had loaded. The main window now opens first, a 20-second fallback always runs, and a WebView2 start-up failure shows an error naming the log folder. Start-up steps are logged in `%LOCALAPPDATA%\RoomOps Studio\logs`.

## 1.0.0 — 2026-10-25

First release.

- Windows desktop application (WPF with WebView2) for administering Exchange Online room mailboxes, room lists and Microsoft Places locations. Every change goes through a plan → preview → execute → verify → audit pipeline.
- Isolated PowerShell 7 worker per service with a command allowlist, a per-process nonce and literal parameter binding.
- Dashboard, room inventory, create-room wizard, booking policy designer with scenario simulator, resource delegates and calendar folder permissions, working hours editor, room lists, buildings and floors, Microsoft Places, configuration templates, bulk import, validation center, change history and Teams Rooms readiness.
- Demo mode with a synthetic tenant.
- Per-machine MSI installer.
