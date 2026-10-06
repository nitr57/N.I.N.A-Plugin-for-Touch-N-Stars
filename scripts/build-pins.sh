#!/usr/bin/env bash
set -euo pipefail
SOURCE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PINS="${1:-$HOME/pins}"
DOTNET="${DOTNET:-$HOME/ai-guiding-test/dotnet/dotnet}"
test -f "$PINS/NINA.Core.dll"
"$DOTNET" build "$SOURCE/Touch-N-Stars/Touch-N-Stars.csproj" -c Release \
  -p:PinsReferenceDirectory="$PINS" -p:SkipPluginDeploy=true
"$DOTNET" test "$SOURCE/tests/PHD2AI/PHD2AI.Tests.csproj" -c Release
