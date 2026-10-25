<#
.SYNOPSIS
    RoomOps Studio build pipeline for Windows.
.DESCRIPTION
    Runs the contract check, frontend checks and bundle, backend build and tests, creates the self-contained
    win-x64 package and, when Inno Setup (ISCC.exe) is installed, the installer.
.PARAMETER E2E
    Also run the Playwright UI tests against the development host (demo mode).
#>
[CmdletBinding()]
param([switch] $E2E, [switch] $SkipPackage, [string] $Configuration = 'Release', [string] $Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version | Select-Object -First 1
function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Run([scriptblock] $block) { & $block; if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE" } }

Step 'Bridge contract check'
Run { dotnet build "$root\tools\RoomOps.ContractGen" -c $Configuration -v q -nologo }
Run { dotnet run --project "$root\tools\RoomOps.ContractGen" -c $Configuration --no-build -- "$root\web\src\bridge\contracts.gen.ts" --check }

Step 'Frontend: install, type-check, unit tests, bundle'
Push-Location "$root\web"
try {
    Run { npm ci --no-audit --no-fund }
    Run { npx tsc -b }
    Run { npx vitest run }
    Run { npx vite build }
} finally { Pop-Location }

Step 'Backend: build and test'
Run { dotnet build "$root\RoomOps.slnx" -c $Configuration -v q -nologo }
Run { dotnet test "$root\RoomOps.slnx" -c $Configuration --no-build }

if ($E2E) {
    Step 'UI tests (Playwright, demo mode)'
    Run { dotnet build "$root\src\RoomOps.DevHost" -c Debug -v q -nologo }
    Push-Location "$root\web"; try { Run { npx playwright test e2e/app.spec.ts } } finally { Pop-Location }
}

if (-not $SkipPackage) {
    Step "Package: RoomOps Studio $version ($Runtime, self-contained)"
    $out = "$root\artifacts\publish\$Runtime"
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    Run { dotnet publish "$root\src\RoomOps.Desktop" -c $Configuration -r $Runtime --self-contained true -p:DebugType=none -o $out -v q -nologo }
    $zip = "$root\artifacts\RoomOpsStudio-$version-$Runtime.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path "$out\*" -DestinationPath $zip
    Write-Host "Package: $zip"

    $iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if (-not $iscc) { $iscc = Get-Item "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" -ErrorAction SilentlyContinue }
    if ($iscc) {
        Step 'Installer (Inno Setup)'
        Run { & $iscc.Source "/DAppVersion=$version" "/DSourceDir=$out" "/O$root\artifacts" "$root\installer\RoomOpsStudio.iss" }
    } else {
        Write-Host 'Inno Setup not found; skipping the installer. The zip package is self-contained.' -ForegroundColor Yellow
    }
    Write-Host 'The MSI installer is built by build.sh with msitools (Linux, macOS or WSL).'
}
Step 'Done'
