#!/usr/bin/env bash
# MediaLLMProxy build script (Linux / macOS)
#   ./build.sh          — restore, build, test
#   ./build.sh publish  — also publish to dist/ (self-contained, trimmed)
#
# Self-bootstrapping: if the .NET SDK is missing, it installs .NET 10 locally
# (~/.dotnet, no sudo) via dotnet-install.sh. Set AUTO_INSTALL_DOTNET=0 to
# disable and fail with instructions instead.
set -euo pipefail

cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null 2>&1; then
  if [ "${AUTO_INSTALL_DOTNET:-1}" = "1" ]; then
    echo "dotnet SDK not found — installing .NET 10 locally (no sudo)..."
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
  else
    echo "ERROR: dotnet SDK not found. Install .NET 10 SDK first:" >&2
    echo "  Linux:   curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0" >&2
    echo "  macOS:   brew install --cask dotnet-sdk   (or the same dotnet-install.sh)" >&2
    echo "  or rerun with AUTO_INSTALL_DOTNET=1 to install automatically." >&2
    exit 1
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: dotnet still not on PATH after install. Add ~/.dotnet to PATH or install manually." >&2
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
