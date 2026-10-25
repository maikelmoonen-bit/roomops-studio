#Requires -Version 7.2
<#
.SYNOPSIS
    RoomOps Studio PowerShell worker. Started by RoomOps Studio with redirected standard streams; not for interactive use.

.DESCRIPTION
    One worker process hosts exactly one Microsoft 365 service session (Exchange Online, Microsoft Places or Microsoft Graph).

    Protocol
      * Requests arrive on stdin, one JSON object per line, and must carry the session nonce.
      * Responses are written to stdout as single lines: "@@RO1:<nonce>:<json>". Any other output (module banners,
        device-code instructions) is not part of the protocol and is treated as diagnostic text by the host.

    Safety
      * Only commands and parameters in $Allowlist can be invoked. Parameters are bound by name through splatting;
        no request content is ever parsed or evaluated as PowerShell.
      * Graph requests are limited to GET.
      * The nonce is removed from the environment immediately after start-up.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
$ConfirmPreference = 'None'
Set-StrictMode -Version 3.0

$script:Nonce = $env:ROOMOPS_WORKER_NONCE
$script:Service = $env:ROOMOPS_WORKER_SERVICE
$script:TestModule = $env:ROOMOPS_WORKER_TEST_MODULE
$script:OwnerWindow = [long]0
if (-not [long]::TryParse([string]$env:ROOMOPS_WORKER_OWNER_WINDOW, [ref]$script:OwnerWindow)) { $script:OwnerWindow = [long]0 }
Remove-Item Env:\ROOMOPS_WORKER_NONCE -ErrorAction SilentlyContinue
Remove-Item Env:\ROOMOPS_WORKER_OWNER_WINDOW -ErrorAction SilentlyContinue

if ([string]::IsNullOrEmpty($script:Nonce) -or $script:Nonce.Length -lt 32) {
    [Console]::Error.WriteLine('RoomOps worker: missing or invalid session nonce.')
    exit 2
}
if ($script:Service -notin @('Exchange', 'Places', 'Graph')) {
    [Console]::Error.WriteLine('RoomOps worker: unknown service.')
    exit 2
}

$script:Prefix = "@@RO1:$($script:Nonce):"
$script:Out = [Console]::Out

# ---------------------------------------------------------------------------------------------------------------- Allowlist
# Must stay identical to RoomOps.Application.Commands.CommandCatalog (verified by an automated test).
$CalendarProcessingParameters = @(
    'Identity', 'AutomateProcessing', 'BookingType', 'AllowRecurringMeetings', 'BookingWindowInDays', 'MaximumDurationInMinutes',
    'MinimumDurationInMinutes', 'ScheduleOnlyDuringWorkHours', 'EnforceSchedulingHorizon', 'EnforceCapacity', 'AllowConflicts',
    'ConflictPercentageAllowed', 'MaximumConflictInstances', 'AllBookInPolicy', 'AllRequestInPolicy', 'AllRequestOutOfPolicy',
    'BookInPolicy', 'RequestInPolicy', 'RequestOutOfPolicy', 'ResourceDelegates', 'ForwardRequestsToDelegates', 'TentativePendingApproval',
    'AddOrganizerToSubject', 'DeleteSubject', 'DeleteComments', 'DeleteAttachments', 'RemovePrivateProperty', 'OrganizerInfo',
    'DeleteNonCalendarItems', 'RemoveCanceledMeetings', 'RemoveOldMeetingMessages', 'RemoveForwardedMeetingNotifications',
    'AddNewRequestsTentatively', 'EnableResponseDetails', 'ProcessExternalMeetingMessages', 'AddAdditionalResponse', 'AdditionalResponse'
)

$Allowlist = @{
    Exchange = @{
        'Get-ConnectionInformation'        = @()
        'Get-EXOMailbox'                   = @('Identity', 'RecipientTypeDetails', 'ResultSize', 'Properties')
        'Get-EXORecipient'                 = @('Identity', 'Anr', 'RecipientTypeDetails', 'ResultSize', 'Properties')
        'Get-EXOMailboxPermission'         = @('Identity', 'ResultSize')
        'Get-EXORecipientPermission'       = @('Identity', 'ResultSize')
        'Get-CalendarProcessing'           = @('Identity')
        'Get-MailboxCalendarConfiguration' = @('Identity')
        'Get-MailboxFolderStatistics'      = @('Identity', 'FolderScope')
        'Get-MailboxFolderPermission'      = @('Identity')
        'Get-AcceptedDomain'               = @()
        'Get-DistributionGroup'            = @('Identity', 'RecipientTypeDetails', 'ResultSize', 'Anr')
        'Get-DistributionGroupMember'      = @('Identity', 'ResultSize')
        'Set-CalendarProcessing'           = $CalendarProcessingParameters
        'Set-MailboxCalendarConfiguration' = @('Identity', 'WorkDays', 'WorkingHoursStartTime', 'WorkingHoursEndTime', 'WorkingHoursTimeZone')
        'Add-MailboxFolderPermission'      = @('Identity', 'User', 'AccessRights', 'SharingPermissionFlags')
        'Set-MailboxFolderPermission'      = @('Identity', 'User', 'AccessRights', 'SharingPermissionFlags')
        'Remove-MailboxFolderPermission'   = @('Identity', 'User', 'Confirm')
        'New-Mailbox'                      = @('Name', 'Room', 'Alias', 'DisplayName', 'PrimarySmtpAddress')
        'Set-Mailbox'                      = @('Identity', 'DisplayName', 'ResourceCapacity')
        'New-DistributionGroup'            = @('Name', 'RoomList', 'Alias', 'DisplayName', 'PrimarySmtpAddress')
        'Set-DistributionGroup'            = @('Identity', 'DisplayName', 'RoomList', 'BypassSecurityGroupManagerCheck')
        'Add-DistributionGroupMember'      = @('Identity', 'Member', 'BypassSecurityGroupManagerCheck')
        'Remove-DistributionGroupMember'   = @('Identity', 'Member', 'BypassSecurityGroupManagerCheck', 'Confirm')
    }
    Places = @{
        'Get-PlaceV3'        = @('Identity', 'Type', 'AncestorId')
        'Get-PlacesSettings' = @()
        'New-Place'          = @('DisplayName', 'Type', 'ParentId', 'Label', 'SortOrder', 'Street', 'City', 'State', 'PostalCode', 'CountryOrRegion', 'Capacity', 'Mailbox')
        'Set-PlaceV3'        = @('Identity', 'DisplayName', 'ParentId', 'Label', 'Capacity', 'IsWheelChairAccessible', 'AudioDeviceName', 'VideoDeviceName',
                                 'DisplayDeviceName', 'MTREnabled', 'Tags', 'Street', 'City', 'State', 'PostalCode', 'CountryOrRegion', 'SortOrder', 'FloorLabel')
    }
    Graph = @{
        'Get-MgContext'         = @()
        'Invoke-MgGraphRequest' = @('Method', 'Uri', 'OutputType')
    }
}

$ModuleName = @{ Exchange = 'ExchangeOnlineManagement'; Places = 'MicrosoftPlaces'; Graph = 'Microsoft.Graph.Authentication' }

# ---------------------------------------------------------------------------------------------------------------- Output
function Write-Frame([System.Collections.IDictionary] $Frame) {
    $json = ConvertTo-Json -InputObject $Frame -Depth 12 -Compress
    # A leading newline guarantees the frame starts on its own line even if a module left a partial line on stdout.
    $script:Out.Write("`n" + $script:Prefix + $json + "`n")
    $script:Out.Flush()
}

function ConvertTo-RoValue($Value, [int] $Depth) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [string] -or $Value -is [bool] -or $Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) { return $Value }
    if ($Value -is [datetime]) { return $Value.ToUniversalTime().ToString('o') }
    if ($Value -is [datetimeoffset]) { return $Value.ToString('o') }
    if ($Value -is [enum] -or $Value -is [guid] -or $Value -is [timespan] -or $Value -is [char]) { return $Value.ToString() }
    if ($Value -is [System.Collections.IDictionary]) {
        if ($Depth -ge 4) { return $Value.ToString() }
        $map = [ordered]@{}
        foreach ($key in $Value.Keys) { $map[[string]$key] = ConvertTo-RoValue $Value[$key] ($Depth + 1) }
        return $map
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        if ($Depth -ge 4) { return @() }
        return , @(foreach ($item in $Value) { ConvertTo-RoValue $item ($Depth + 1) })
    }
    if ($Depth -ge 1) { return $Value.ToString() }
    $map = [ordered]@{}
    foreach ($property in $Value.PSObject.Properties) {
        if ($property.Name -like 'PS*' -or $property.Name -eq 'RunspaceId') { continue }
        try { $map[$property.Name] = ConvertTo-RoValue $property.Value 1 } catch { $map[$property.Name] = $null }
    }
    return $map
}

function ConvertTo-RoParameter($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Collections.IDictionary]) {
        if ($Value.Contains('$switch')) { return [System.Management.Automation.SwitchParameter][bool]$Value['$switch'] }
        throw [System.ArgumentException]::new('Structured parameter values are not accepted.')
    }
    if ($Value -is [long] -and $Value -ge [int]::MinValue -and $Value -le [int]::MaxValue) { return [int]$Value }
    if ($Value -is [System.Collections.IList]) { return [string[]]@($Value | ForEach-Object { [string]$_ }) }
    if ($Value -is [string] -or $Value -is [bool] -or $Value -is [int] -or $Value -is [long]) { return $Value }
    throw [System.ArgumentException]::new('Unsupported parameter value type.')
}

function New-ErrorFrame($Id, [string] $Message, [string] $ErrorId, [string] $Category) {
    return [ordered]@{ id = $Id; ok = $false; error = [ordered]@{ message = $Message; errorId = $ErrorId; category = $Category } }
}

# ---------------------------------------------------------------------------------------------------------------- Operations
function Import-ServiceModule {
    if ($script:TestModule) {
        Import-Module -Name $script:TestModule -Force -DisableNameChecking
        return 'test'
    }
    $name = $ModuleName[$script:Service]
    $available = Get-Module -ListAvailable -Name $name | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $available) {
        throw [System.IO.FileNotFoundException]::new("The PowerShell module '$name' is not installed for this user. Install it with: Install-Module $name -Scope CurrentUser")
    }
    Import-Module -Name $name -RequiredVersion $available.Version -DisableNameChecking
    return $available.Version.ToString()
}

function Initialize-SignInWindow {
    # Microsoft.Graph.Authentication 2.34+ always signs in through Web Account Manager (WAM) on Windows and parents the WAM dialog
    # to GetAncestor(GetConsoleWindow(), GA_ROOTOWNER). RoomOps starts the Graph worker with a hidden console window. Making the
    # RoomOps window its owner lets that lookup resolve to RoomOps, so the dialog opens in front of the application.
    # Returns whether a window is available for the dialog.
    try {
        Add-Type -Namespace RoomOps -Name SignInWindow -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
'@
        $console = [RoomOps.SignInWindow]::GetConsoleWindow()
        if ($console -eq [IntPtr]::Zero) { return $false }
        # SW_HIDE (0): keeps the console out of sight, also when a default terminal application opened it visibly.
        [void][RoomOps.SignInWindow]::ShowWindow($console, 0)
        # GWLP_HWNDPARENT (-8) sets the owner of a top-level window.
        if ($script:OwnerWindow -ne 0) { [void][RoomOps.SignInWindow]::SetWindowLongPtr($console, -8, [IntPtr]$script:OwnerWindow) }
        return $true
    } catch {
        return $false
    }
}

function Invoke-Connect([System.Collections.IDictionary] $Request) {
    $version = Import-ServiceModule
    switch ($script:Service) {
        'Exchange' {
            $p = @{ ShowBanner = $false; SkipLoadingFormatData = $true }
            if ($Request['upn']) { $p['UserPrincipalName'] = [string]$Request['upn'] }
            $connect = Get-Command -Name 'Connect-ExchangeOnline'
            switch ([string]$Request['signIn']) {
                'Browser' { if ($connect.Parameters.ContainsKey('DisableWAM')) { $p['DisableWAM'] = $true } }
                'DeviceCode' { $p['Device'] = $true }
            }
            & $connect @p | Out-Null
            $info = @(Get-ConnectionInformation | Where-Object { $_.State -eq 'Connected' }) | Select-Object -First 1
            if (-not $info) { throw 'Exchange Online did not report an active connection.' }
            return [ordered]@{ tenantId = [string]$info.TenantID; account = [string]$info.UserPrincipalName; moduleVersion = $version; scopes = @() }
        }
        'Places' {
            Connect-MicrosoftPlaces | Out-Null
            return [ordered]@{ tenantId = $null; account = $null; moduleVersion = $version; scopes = @() }
        }
        'Graph' {
            $p = @{ Scopes = @('User.Read.All'); NoWelcome = $true; ContextScope = 'Process' }
            if ($Request['tenantId']) { $p['TenantId'] = [string]$Request['tenantId'] }
            if ([string]$Request['signIn'] -eq 'DeviceCode') {
                $p['UseDeviceCode'] = $true
            } elseif ($IsWindows -and -not $script:SignInWindow) {
                throw 'Windows sign-in for Microsoft Graph needs a window, and this PowerShell session has none. Choose Device code as the Graph sign-in method and connect again.'
            }
            try {
                Connect-MgGraph @p | Out-Null
            } catch {
                if ([string]$_.Exception.Message -match 'window handle') {
                    throw "Windows sign-in for Microsoft Graph could not open its dialog ($($_.Exception.Message)) Choose Device code as the Graph sign-in method and connect again."
                }
                throw
            }
            $context = Get-MgContext
            if (-not $context) { throw 'Microsoft Graph did not return a session context.' }
            return [ordered]@{ tenantId = [string]$context.TenantId; account = [string]$context.Account; moduleVersion = $version; scopes = @($context.Scopes) }
        }
    }
}

function Invoke-Disconnect {
    try {
        switch ($script:Service) {
            'Exchange' { if (Get-Command Disconnect-ExchangeOnline -ErrorAction SilentlyContinue) { Disconnect-ExchangeOnline -Confirm:$false | Out-Null } }
            'Places' { if (Get-Command Disconnect-MicrosoftPlaces -ErrorAction SilentlyContinue) { Disconnect-MicrosoftPlaces | Out-Null } }
            'Graph' { if (Get-Command Disconnect-MgGraph -ErrorAction SilentlyContinue) { Disconnect-MgGraph | Out-Null } }
        }
    } catch { }
}

function Invoke-Allowed([System.Collections.IDictionary] $Request) {
    $name = [string]$Request['command']
    $allowed = $Allowlist[$script:Service][$name]
    if ($null -eq $allowed) { throw [System.UnauthorizedAccessException]::new("Command '$name' is not allowed for $($script:Service).") }

    $splat = @{}
    $parameters = $Request['parameters']
    if ($parameters) {
        foreach ($key in $parameters.Keys) {
            if ($allowed -notcontains $key) { throw [System.UnauthorizedAccessException]::new("Parameter '$key' is not allowed for $name.") }
            $splat[$key] = ConvertTo-RoParameter $parameters[$key]
        }
    }
    if ($name -eq 'Invoke-MgGraphRequest' -and [string]$splat['Method'] -cne 'GET') {
        throw [System.UnauthorizedAccessException]::new('Only GET requests are allowed for Microsoft Graph.')
    }

    # Resolve to a module command (cmdlet or function). Aliases and external scripts are never executed.
    $command = Get-Command -Name $name -CommandType Cmdlet, Function -ErrorAction Stop | Select-Object -First 1
    $warnings = [System.Collections.Generic.List[string]]::new()
    $output = & $command @splat -WarningVariable +roWarnings -WarningAction SilentlyContinue -ErrorAction Stop
    foreach ($w in @(Get-Variable -Name roWarnings -ValueOnly -ErrorAction SilentlyContinue)) { if ($w) { $warnings.Add([string]$w) } }

    $data = [System.Collections.Generic.List[object]]::new()
    foreach ($item in @($output)) {
        if ($null -eq $item) { continue }
        if ($name -eq 'Invoke-MgGraphRequest' -and $item -is [string]) {
            $data.Add((ConvertFrom-Json -InputObject $item -AsHashtable -Depth 12))
        } else {
            $data.Add((ConvertTo-RoValue $item 0))
        }
    }
    return @{ data = $data; warnings = $warnings }
}

# ---------------------------------------------------------------------------------------------------------------- Main loop
$script:SignInWindow = $false
if ($IsWindows -and $script:Service -eq 'Graph' -and -not $script:TestModule) { $script:SignInWindow = Initialize-SignInWindow }

Write-Frame ([ordered]@{ id = $null; ok = $true; type = 'ready'; service = $script:Service; ps = $PSVersionTable.PSVersion.ToString(); pid = $PID })

$stdin = [Console]::In
while ($true) {
    $line = $stdin.ReadLine()
    if ($null -eq $line) { break }
    if ($line.Length -eq 0) { continue }
    $id = $null
    try {
        if ($line.Length -gt 4MB) { throw [System.ArgumentException]::new('Request too large.') }
        $request = ConvertFrom-Json -InputObject $line -AsHashtable -Depth 16
        $id = $request['id']
        if ([string]$request['nonce'] -cne $script:Nonce) { throw [System.UnauthorizedAccessException]::new('Request not authenticated.') }
        $started = [System.Diagnostics.Stopwatch]::StartNew()
        switch ([string]$request['op']) {
            'ping' { Write-Frame ([ordered]@{ id = $id; ok = $true; data = @(); warnings = @() }) }
            'connect' {
                $result = Invoke-Connect $request
                Write-Frame ([ordered]@{ id = $id; ok = $true; data = @($result); warnings = @() })
            }
            'invoke' {
                $result = Invoke-Allowed $request
                Write-Frame ([ordered]@{ id = $id; ok = $true; data = $result.data; warnings = $result.warnings; ms = $started.ElapsedMilliseconds })
            }
            'disconnect' {
                Invoke-Disconnect
                Write-Frame ([ordered]@{ id = $id; ok = $true; data = @(); warnings = @() })
            }
            'shutdown' {
                Invoke-Disconnect
                Write-Frame ([ordered]@{ id = $id; ok = $true; data = @(); warnings = @() })
                exit 0
            }
            default { throw [System.ArgumentException]::new("Unknown operation.") }
        }
    } catch {
        $record = $_
        $category = if ($record.CategoryInfo) { [string]$record.CategoryInfo.Category } else { '' }
        Write-Frame (New-ErrorFrame $id ([string]$record.Exception.Message) ([string]$record.FullyQualifiedErrorId) $category)
    } finally {
        Remove-Variable -Name roWarnings -ErrorAction SilentlyContinue
    }
}
Invoke-Disconnect
