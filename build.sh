#!/usr/bin/env bash
# Builds and tests inside the official .NET SDK container (no host SDK required).
# Usage: ./build.sh                   tests without the Slow, Benchmark and Performance categories
#        ./build.sh test-all          all tests except [Explicit] ones (benchmarks: --filter TestCategory=Benchmark)
#        ./build.sh [dotnet args...]  any dotnet command, e.g. ./build.sh build PinsGuider.slnx
set -euo pipefail
cd "$(dirname "$0")"
DEFAULT_FILTER="TestCategory!=Slow&TestCategory!=Benchmark&TestCategory!=Performance"
args=("$@")
if [ ${#args[@]} -eq 0 ]; then
  args=(test PinsGuider.slnx --filter "$DEFAULT_FILTER")
elif [ "${args[0]}" = test-all ]; then
  args=(test PinsGuider.slnx "${args[@]:1}")
fi
exec docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e NUGET_PACKAGES=/nuget -v "${NUGET_CACHE:-$HOME/.nuget/packages}:/nuget" \
  -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet "${args[@]}"
