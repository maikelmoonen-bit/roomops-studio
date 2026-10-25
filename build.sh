#!/usr/bin/env bash
# RoomOps Studio build pipeline (Linux/macOS/CI).
#   1. Contract check   – the TypeScript bridge contract must match the .NET registry
#   2. Frontend         – install, type-check, unit tests, production bundle
#   3. Backend          – build and run all .NET tests (domain, application, integration, worker contract)
#   4. Package          – self-contained Windows (win-x64) desktop build, zipped,
#                         plus an MSI installer when wixl (msitools) is installed
#   5. Optional e2e     – Playwright UI tests against the dev host (--e2e)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
CONFIG="${CONFIGURATION:-Release}"
RID="${RUNTIME:-win-x64}"
RUN_E2E=false
SKIP_PACKAGE=false
for arg in "$@"; do
  case "$arg" in
    --e2e) RUN_E2E=true ;;
    --skip-package) SKIP_PACKAGE=true ;;
  esac
done
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
step() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
need() { command -v "$1" >/dev/null || { echo "Required tool not found: $1"; exit 1; }; }
need dotnet; need node; need npm
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

step "Bridge contract check"
dotnet build "$ROOT/tools/RoomOps.ContractGen" -c "$CONFIG" -v q -nologo
dotnet run --project "$ROOT/tools/RoomOps.ContractGen" -c "$CONFIG" --no-build -- "$ROOT/web/src/bridge/contracts.gen.ts" --check

step "Frontend: install, type-check, unit tests, bundle"
cd "$ROOT/web"
npm ci --no-audit --no-fund
npx tsc -b
npx vitest run
npx vite build
cd "$ROOT"

step "Backend: build and test"
dotnet build "$ROOT/RoomOps.slnx" -c "$CONFIG" -v q -nologo
dotnet test "$ROOT/RoomOps.slnx" -c "$CONFIG" --no-build

if [ "$RUN_E2E" = true ]; then
  step "UI tests (Playwright, demo mode)"
  dotnet build "$ROOT/src/RoomOps.DevHost" -c Debug -v q -nologo
  (cd "$ROOT/web" && npx playwright test e2e/app.spec.ts)
fi

if [ "$SKIP_PACKAGE" = false ]; then
  step "Package: RoomOps Studio $VERSION ($RID, self-contained)"
  OUT="$ROOT/artifacts/publish/$RID"
  rm -rf "$OUT"
  dotnet publish "$ROOT/src/RoomOps.Desktop" -c "$CONFIG" -r "$RID" --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o "$OUT" -v q -nologo
  mkdir -p "$ROOT/artifacts"
  ZIP="$ROOT/artifacts/RoomOpsStudio-$VERSION-$RID.zip"
  rm -f "$ZIP"
  (cd "$OUT" && python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', '.')" "${ZIP%.zip}")
  echo "Package: $ZIP"

  if [ "$RID" = win-x64 ] && command -v wixl >/dev/null && command -v wixl-heat >/dev/null; then
    step "Installer: RoomOps Studio $VERSION (MSI, per-machine)"
    FILES_WXS="$ROOT/artifacts/obj/RoomOpsStudio.Files.wxs"
    mkdir -p "$(dirname "$FILES_WXS")"
    find "$OUT" -type f | LC_ALL=C sort | wixl-heat -p "$OUT/" --directory-ref INSTALLDIR --component-group ProductFiles --var var.SourceDir --win64 > "$FILES_WXS"
    MSI="$ROOT/artifacts/RoomOpsStudio-$VERSION-$RID.msi"
    wixl -a x64 -D Version="$VERSION" -D SourceDir="$OUT" -D Win64=yes -D IconFile="$ROOT/src/RoomOps.Desktop/Assets/roomops.ico" \
      -o "$MSI" "$ROOT/installer/RoomOpsStudio.wxs" "$FILES_WXS"
    echo "Installer: $MSI"
  elif [ "$RID" = win-x64 ]; then
    echo "wixl (msitools) not found; skipping the MSI. Install it with 'sudo apt install wixl' (Debian/Ubuntu), 'sudo dnf install msitools' (Fedora) or 'brew install msitools' (macOS)."
  fi
fi
step "Done"
