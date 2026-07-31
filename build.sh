#!/usr/bin/env bash
# MediaLLMProxy build script (Linux / macOS)
#   ./build.sh          — restore, build, test
#   ./build.sh publish  — also publish to dist/ (self-contained, trimmed)
set -euo pipefail

cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: dotnet SDK not found. Install .NET 10 SDK: https://dotnet.microsoft.com/download" >&2
  exit 1
fi

echo "== restore =="
dotnet restore KineticLLM.sln

echo "== build (Release) =="
dotnet build KineticLLM.sln -c Release --no-restore

echo "== test =="
dotnet test OAIPreRouter.Tester -c Release --no-build

if [ "${1:-}" = "publish" ]; then
  echo "== publish to dist/ =="
  dotnet publish OAIPreRouter.Cli -c Release --no-build \
    -o dist --self-contained true -r "$(dotnet --info | awk '/RID:/{print $2; exit}')" \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
  echo "done: dist/"
fi
