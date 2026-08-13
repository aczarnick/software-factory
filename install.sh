#!/usr/bin/env bash
# Builds the factory CLI and puts `factory` on your PATH.
#
#   ./install.sh              installs to /usr/local/bin
#   PREFIX=~/.local ./install.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="${PREFIX:-/usr/local}"
BIN="$PREFIX/bin"

command -v dotnet >/dev/null 2>&1 || {
  echo "error: .NET 9 SDK is required — https://dot.net" >&2
  exit 1
}

command -v claude >/dev/null 2>&1 || {
  echo "warning: the 'claude' CLI was not found on PATH." >&2
  echo "         The factory drives it as its agent transport and cannot run without it." >&2
}

echo "building…"
dotnet publish "$ROOT/src/Factory.Cli/Factory.Cli.csproj" \
  -c Release -o "$ROOT/dist" --nologo -v quiet

mkdir -p "$BIN"
install -m 0755 "$ROOT/dist/factory" "$BIN/factory"

echo "installed $BIN/factory"
echo
echo "  cd your-project && factory build \"what you want\""
