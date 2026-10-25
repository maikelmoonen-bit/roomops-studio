# User guide

RoomOps Studio is for Microsoft 365 administrators who manage meeting rooms. This guide walks through each area of the application.

## Getting started

1. Install PowerShell 7 and the Microsoft modules (see the [README](../README.md#requirements)).
2. Install RoomOps Studio with the MSI (see [Installing](../README.md#installing)) and start it from the Start menu. The **Connections** page shows whether PowerShell and each module were found, with the exact install command for anything missing.
3. Choose one of the following:
   - **Connect Exchange Online.** Optionally enter your administrator account and choose a sign-in method: system browser (recommended), Windows account (WAM) or device code. MFA and Conditional Access prompts appear in the Microsoft sign-in window. Exchange establishes which tenant you are working in.
     Microsoft Graph has its own sign-in method: *Windows sign-in* (the module always uses WAM on Windows) or *Device code*.
   - **Explore demo mode.** This opens a synthetic tenant, clearly marked by a striped band. Nothing is sent to Microsoft 365.
4. Optionally connect **Microsoft Places** (buildings, floors, room metadata) and **Microsoft Graph** (Teams Rooms readiness). Each service has its own session and status in the top bar.

The tenant switcher in the top-left always shows which tenant you are working in. Switching tenants closes every session and discards all cached data first.

## How changes work

Every change follows the same steps, wherever you start it:

1. Edit settings on any screen and choose **Preview changes**.
2. RoomOps reads the current configuration, validates your request and shows a **preview**:
   - the **target tenant** (and a DEMO label in demo mode);
   - each object with its current and new values;
   - blocking issues (red) and warnings (amber);
   - the **Technical Execution Preview**, which contains the exact PowerShell commands. You can copy them to run yourself.
3. Choose **Apply configuration**. If the preview removes memberships or permissions, you must tick the acknowledgement first.
4. RoomOps applies the changes one object at a time and reads each result back:
   - **Verified**: the values read back match.
   - **Accepted, propagation pending**: Microsoft Places accepted the change, but its experiences can take up to 24 hours to show it.
   - **Waiting for provisioning**: a newly created object isn't available yet. The remaining steps are saved, and you can resume them from Change History.
   - **Stale**: the configuration changed after you previewed it, so nothing was applied. Create a new preview.
   - **Failed**: the error is explained. Use **Retry unresolved items** to re-plan only the items that didn't complete.

Settings that already have the requested value are reported as **No change required** and are never written.

## Dashboard

The dashboard shows the room count, room lists, buildings, validation findings and changes made in the last seven days. **Configuration readiness** lists common gaps; select a row to open the matching rooms.

Metrics that depend on booking policies only count rooms whose configuration has been read. To read every room, choose **Run scan**. The scan reads each room in turn and changes nothing.

## Rooms

- Search by name, address, building or tag. Use **Filters** for building, booking mode and room list, and the quick-filter chips for common problems.
- **Views** saves the current filters, sorting, columns and layout. **Columns** shows or hides columns; drag a column edge to resize it.
- Select rooms to apply a template, change booking settings or add them to a room list in bulk.
- Right-click a row for shortcuts. The download button exports the filtered rooms as CSV.
- **Cards** shows rooms with their capacity, facilities and booking mode.

### Room detail

The header shows the room plate, address, location, capacity, booking mode, delegates, working hours and validation status. The tabs are:

- **Overview**: the policy in plain language, findings, and the Exchange mailbox and Places room side by side. These are separate objects; source badges (EXO, PLC, GRA) show which system owns each value.
- **Booking rules**: the policy designer (see below).
- **Delegates & access**: booking delegates, calendar folder permissions, and read-only Full Access, Send As and Send on Behalf.
- **Working hours**: the weekly editor.
- **Location & facilities**: the Places metadata editor, Exchange name and capacity, and Teams Rooms readiness.
- **Room lists**: memberships, which you can add or remove.
- **Advanced**: identifiers and additional properties with their sources.
- **Configuration history**: changes to this room.

## Booking policy designer

- The **policy sentence** at the top describes what the room will do with the current draft. Highlighted values such as "90 days" jump to the setting that controls them.
- **Booking access** lets you choose who can book with one of four models, without knowing the Exchange parameters:
  - *Everyone – Automatic booking*: everyone can book within the booking limits.
  - *Selected users only – Automatic booking*: only the people or groups you add can book; everyone else is declined.
  - *Delegate approval required*: everyone can request the room, and a delegate approves.
  - *Hybrid booking*: the people or groups you add book automatically; everyone else requests and a delegate approves.

  A model sets `AutomateProcessing`, the book-in and request policies, `ResourceDelegates`, `ForwardRequestsToDelegates` and `TentativePendingApproval`, and clears lists the model doesn't use. Booking limits, privacy and every other setting stay as they are. The panel asks only for what the model needs: who can book automatically and/or who approves requests. It lists the settings that will change, and the **Booking policy summary** next to it shows the effective result as you edit. When you open a room, RoomOps recognizes the model its configuration matches. A configuration that matches none is shown as **Custom configuration** and nothing is changed until you choose a model or edit it yourself.
- **Advanced booking configuration** (collapsed unless the room has a custom configuration) holds every permission parameter individually: the permission matrix, delegates, forwarding and the tentative state.
- **Sections**:
  - General behavior
  - Scheduling constraints (booking window, maximum and minimum duration, working-hours restriction, scheduling horizon, capacity)
  - Conflicts: these apply to recurring meetings; a single conflicting meeting is always declined unless conflicts are allowed
  - Privacy, with an illustrative calendar entry
  - External requests
  - Automated responses, with a preview
  - Advanced settings
- Each setting shows its PowerShell parameter name. Settings that only take effect with AutoAccept are marked when calendar processing is not AutoAccept.
- **Pending changes** lists every changed setting, with Reset and Preview buttons.
- The **Scenario simulator** describes how the draft is expected to treat a request: organizer, date and time in the room's time zone, duration, recurrence, conflicts and attendees. It labels each step as *from configuration*, *assumption* or *needs live data*. It is not a booking and never guarantees acceptance.

## Resource delegates

Booking delegates approve requests. Calendar folder permissions control who can see or edit the room calendar. RoomOps reads the calendar folder's real name, so localized names such as "Agenda" work. Change a role by picking another one in the *Access rights* column (None, AvailabilityOnly, LimitedDetails or Editor; Default and Anonymous stop at LimitedDetails). Each change is previewed first; demoting a calendar delegate removes the delegate status first.

Full Access, Send As and Send on Behalf apply to the whole room mailbox, not just its calendar. They are shown for reference, never changed by RoomOps and never granted as a side effect. Someone with Full Access can open the entire mailbox with full rights, whatever their folder role; RoomOps marks such people in the folder permission list.

## Working hours

Select the work days, then drag the time range, or type the start and end times and pick a Windows time zone. Exchange stores one range for all work days, so the editor never offers a separate range per day. The side panel shows daylight-saving details and whether booking is limited to working hours.

## Room lists

- **Lists** view: expand a list to see its members. Non-room members are flagged. You can rename a list or remove members here.
- **Board** view: drag rooms between lists. A drag moves the room; hold Alt or Ctrl to add it to the target list without removing it from the current one. Proposed changes collect in a tray until you preview them.
- **Create room list** creates a new list. **Convert a group** turns a distribution group into a room list, but only if it is a regular distribution group whose members are all room mailboxes.

## Buildings & floors

The explorer shows the Microsoft Places hierarchy: Organization → Building → Floor → Section → Room.

- Select a building, floor or section to edit it. You can add floors to buildings and sections to floors.
- Drag a room onto a floor or section, or a section onto a floor, to reparent it. Only valid targets accept the drop, and every move is previewed.
- **Rooms without a location** lists rooms you can drag into place.

## Microsoft Places

This page compares discovery through Room Finder (room lists) with discovery through Places Finder (the location hierarchy). It shows coverage across both, checks whether each building has a room list with the same name, and lists the tenant's Places settings read-only. Changing who sees Places Finder is a tenant-wide decision. The page shows Microsoft's documented commands for it but does not run them.

## Configuration templates

A template contains only the settings you include. Applying it shows a before/after comparison per room. Unticked settings are preserved as exceptions. Assigning a template lets the Validation Center report drift from it later. Templates can be exported and imported as JSON. The example templates are starting points, not Microsoft defaults.

## Bulk operations

1. Download the sample file, or prepare a CSV (comma, semicolon or tab separated) or JSON file with one row per room. Identify rooms by address, alias or id.
2. Choose the file. RoomOps maps the columns automatically; adjust the mapping if needed.
3. Review the row validation. Rows with errors, such as an unknown room, a duplicate row, an invalid value or a value out of range, are excluded.
4. Preview and apply. Executions in this session list the progress of each run. You can export a failure report as CSV and retry unresolved items after revalidation.

Supported columns:
- `Room`
- `Capacity`
- every non-recipient booking setting, by parameter name (for example `BookingWindowInDays`, `AllowConflicts`)
- `WorkDays`, `WorkingHoursStartTime`, `WorkingHoursEndTime` and `WorkingHoursTimeZone` (all four together)
- `Places.Label`, `Places.Tags` (separated by `;` or `|`), `Places.IsWheelChairAccessible`, `Places.AudioDeviceName`, `Places.VideoDeviceName`, `Places.DisplayDeviceName`, `Places.MTREnabled`, `Places.FloorLabel`
- `RoomList`

Yes/no columns accept `yes`/`no`, `true`/`false` and `1`/`0`.

## Validation center

Findings are grouped by rule and classified as Critical, Warning, Recommendation, Informational or **Unable to verify**. Unable to verify means the data hasn't been read, for example because no configuration scan has run.

- **Technical** rules describe documented Exchange and Places behavior.
- **Organizational baseline** rules encode your own conventions: expected time zones, a privacy baseline, capacity and room-list expectations. You configure them in Settings.

Findings with a deterministic fix offer **Remediate**, which goes through the normal preview. Select several to remediate them together.

## Change history

Every change made through RoomOps is recorded with:
- the administrator and timestamp;
- the before and after values;
- the executed commands;
- the outcome and the verification result.

From a record you can:
- **Restore previous values**, which is re-planned against the current configuration;
- **Resume pending steps**, after a provisioning delay;
- **Verify again**, for booking and Places changes that were still propagating.

**Export** produces an HTML change report suitable for change-management records, or JSON. This history is local to your device and is not the Microsoft 365 audit log.

## Settings

| Area | What you can set |
|---|---|
| Appearance | Theme, density, motion, language (also the EN \| NL switch at the top right) |
| Connections | PowerShell path, sign-in method, default account |
| Execution | Pause between objects, throttling retries, provisioning wait |
| Validation baseline | Expected time zones, privacy baseline, enabling and severity of each baseline rule |

## Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl K | Command palette (rooms, pages, actions) |
| Ctrl B | Collapse or expand the sidebar |
| / | Focus the page search |
| G then D / R / L / F / B / T / V / H / C | Go to Dashboard, Rooms, Room lists, Buildings, Booking, Templates, Validation, History, Connections |
| ? | Show all shortcuts |

## Troubleshooting

| Symptom | What to do |
|---|---|
| "PowerShell 7 (pwsh) was not found" | Install PowerShell 7 or set its path in Settings. The path must point to `pwsh.exe`. |
| Module "Not installed" | Run the install command shown on the Connections page. |
| Sign-in window doesn't appear (WAM) | Exchange: switch the sign-in method to *System browser* in Connections or Settings. Microsoft Graph: choose *Device code* on the Graph card in Connections. |
| "Session expired" | Reconnect the service on the Connections page. |
| Places writes are blocked ("tenant not verified") | The Places session couldn't be matched to the Exchange tenant. Make sure you signed in to Places with an account in the same tenant and that the tenant has at least one room in Places. |
| A change shows "propagation pending" | Normal for Microsoft Places. Use **Verify again** in Change History later. |
| Scripts are blocked by execution policy | Your organization enforces signed scripts. Sign `RoomOps.Worker.ps1` (see Known limitations). |

Logs are in `%LOCALAPPDATA%\RoomOps Studio\logs`.
