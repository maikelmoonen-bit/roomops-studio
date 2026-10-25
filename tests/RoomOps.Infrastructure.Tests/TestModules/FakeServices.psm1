# Stand-in for the Microsoft modules in worker contract tests. Functions mirror the parameter names of the real cmdlets and echo
# back exactly what they received, so tests can prove values are bound literally (never evaluated) by the worker.

function Connect-ExchangeOnline {
    [CmdletBinding()]
    param([string] $UserPrincipalName, [switch] $ShowBanner, [switch] $SkipLoadingFormatData, [switch] $DisableWAM, [switch] $Device)
    Write-Host 'Fake banner text written to the host stream'
    Write-Host 'To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ABCD1234 to authenticate.'
}

function Get-ConnectionInformation {
    [CmdletBinding()]
    param()
    [pscustomobject]@{ State = 'Connected'; TenantID = '11111111-2222-3333-4444-555555555555'; UserPrincipalName = 'admin@fake.example' }
}

function Get-EXOMailbox {
    [CmdletBinding()]
    param([string] $Identity, [string[]] $RecipientTypeDetails, [string] $ResultSize, [string[]] $Properties)
    if ($Identity -eq 'missing@fake.example') { throw "The specified mailbox Identity:`"$Identity`" doesn't exist." }
    if ($Identity -eq 'busy@fake.example') { throw 'Server busy: request was throttled (micro delay applied).' }
    [pscustomobject]@{
        DisplayName               = 'Room A'
        PrimarySmtpAddress        = 'a@fake.example'
        Alias                     = 'a'
        ExternalDirectoryObjectId = [guid]'aaaaaaaa-0000-0000-0000-000000000001'
        ResourceCapacity          = 8
        WhenCreated               = [datetime]::new(2024, 1, 2, 3, 4, 5, [System.DateTimeKind]::Utc)
        EmailAddresses            = [System.Collections.ArrayList]@('SMTP:a@fake.example', 'smtp:alias@fake.example')
        RecipientTypeDetails      = 'RoomMailbox'
        IsDirSynced               = $false
    }
}

function Set-CalendarProcessing {
    [CmdletBinding()]
    param([string] $Identity, [string] $AdditionalResponse, [string[]] $ResourceDelegates, [bool] $AllowConflicts, [int] $BookingWindowInDays)
    Write-Warning 'Fake warning from Set-CalendarProcessing'
    [pscustomobject]@{
        ReceivedIdentity       = $Identity
        ReceivedResponse       = $AdditionalResponse
        ReceivedDelegates      = $ResourceDelegates
        ReceivedAllowConflicts = $AllowConflicts
        ReceivedWindow         = $BookingWindowInDays
        ReceivedKeys           = @($PSBoundParameters.Keys | Sort-Object)
    }
}

function New-DistributionGroup {
    [CmdletBinding()]
    param([string] $Name, [switch] $RoomList, [string] $Alias, [string] $DisplayName, [string] $PrimarySmtpAddress)
    [pscustomobject]@{ Name = $Name; IsRoomList = [bool]$RoomList }
}

function Remove-DistributionGroupMember {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param([string] $Identity, [string] $Member, [switch] $BypassSecurityGroupManagerCheck)
    if ($PSCmdlet.ShouldProcess($Member, 'Remove')) { [pscustomobject]@{ Removed = $Member } }
}

# A command that exists in the session but is not on the allowlist.
function Get-SecretData {
    [CmdletBinding()]
    param()
    'this must never be returned'
}

Export-ModuleMember -Function *
