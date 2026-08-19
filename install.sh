#!/usr/bin/env bash
# Builds the factory CLI and puts `factory` on your PATH.
#
#   ./install.sh                  installs to ~/.local/bin
#   PREFIX=/usr/local ./install.sh   system-wide (needs write access, so usually sudo)
#
# Defaults to a directory you own. Installing a development tool should not require root,
# and asking for it invites running the whole build as root — which the implement station
# then refuses, because `claude --dangerously-skip-permissions` will not run as root.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="${PREFIX:-$HOME/.local}"
BIN="$PREFIX/bin"

command -v dotnet >/dev/null 2>&1 || {
  echo "error: .NET 10 SDK is required — https://dot.net" >&2
  exit 1
}

command -v claude >/dev/null 2>&1 || {
  echo "warning: the 'claude' CLI was not found on PATH." >&2
  echo "         The factory drives it as its agent transport and cannot run without it." >&2
}

if ! mkdir -p "$BIN" 2>/dev/null; then
  echo "error: cannot create $BIN" >&2
  echo "       choose somewhere you own: PREFIX=~/.local ./install.sh" >&2
  exit 1
fi

if [ ! -w "$BIN" ]; then
  echo "error: $BIN is not writable by $(id -un)" >&2
  echo "       install somewhere you own:  PREFIX=~/.local ./install.sh" >&2
  echo "       or elevate deliberately:    sudo PREFIX=$PREFIX ./install.sh" >&2
  exit 1
fi

echo "building…"
dotnet publish "$ROOT/src/Factory.Cli/Factory.Cli.csproj" \
  -c Release -o "$ROOT/dist" --nologo -v quiet

install -m 0755 "$ROOT/dist/factory" "$BIN/factory"
echo "installed $BIN/factory"

# Installing is not the same as being the one that runs. A copy earlier on PATH keeps
# winning silently, which is how this repository ended up building itself with a binary
# 143 commits behind its own source.
RESOLVED="$(command -v factory 2>/dev/null || true)"

if [ -z "$RESOLVED" ]; then
  echo
  echo "warning: $BIN is not on your PATH, so 'factory' will not resolve." >&2
  echo "         add it:  export PATH=\"$BIN:\$PATH\"" >&2
elif [ "$RESOLVED" != "$BIN/factory" ]; then
  echo
  echo "warning: 'factory' still resolves to $RESOLVED, not the copy just installed." >&2
  echo "         that one wins until it is removed or $BIN comes first on PATH:" >&2
  echo "           rm $RESOLVED" >&2
  echo "         check with:  factory version" >&2
else
  echo
  echo "  cd your-project && factory build \"what you want\""
fi
