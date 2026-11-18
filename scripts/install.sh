#!/bin/sh
# Installs the DevDash agent on macOS or Linux and registers it as a background service that runs as
# you (a launchd agent on macOS, a systemd user unit on Linux). No sudo.
#
#   curl -fsSL https://raw.githubusercontent.com/mikejobson/dev-dashboard/main/scripts/install.sh \
#     | sh -s -- --hub-url https://devdash.<your-tailnet>.ts.net --api-key <key>
#
# Everything after `--` goes to `devdash-agent install` (see `devdash-agent install --help`).
# Run it again with no arguments to upgrade: the saved configuration is kept.
#
# Environment:
#   DEVDASH_VERSION      release tag to install, e.g. v0.2.0 (default: the latest release)
#   DEVDASH_INSTALL_DIR  where the binary goes (default: ~/.local/bin)
#   DEVDASH_REPO         GitHub repository to download from (default: mikejobson/dev-dashboard)
#   DEVDASH_BASE_URL     download from this URL prefix instead of GitHub Releases (mirrors, testing)
#   DEVDASH_NO_SERVICE   set to 1 to only download the binary and skip `devdash-agent install`
set -eu

repo="${DEVDASH_REPO:-mikejobson/dev-dashboard}"
version="${DEVDASH_VERSION:-latest}"
install_dir="${DEVDASH_INSTALL_DIR:-$HOME/.local/bin}"

say() { printf '%s\n' "$*"; }
fail() { printf 'install.sh: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || fail "$1 is required but was not found."; }

need curl
need tar
need uname

os="$(uname -s)"
arch="$(uname -m)"
case "$os" in
  Darwin)
    # A shell running under Rosetta reports x86_64 on Apple silicon; install the native build anyway.
    if [ "$arch" = "x86_64" ] && [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = "1" ]; then
      arch="arm64"
    fi
    case "$arch" in
      arm64) rid="osx-arm64" ;;
      x86_64) rid="osx-x64" ;;
      *) fail "unsupported macOS architecture: $arch" ;;
    esac
    ;;
  Linux)
    if ldd --version 2>&1 | grep -qi musl; then
      fail "musl-based Linux (such as Alpine) is not supported; the agent is built for glibc."
    fi
    case "$arch" in
      x86_64 | amd64) rid="linux-x64" ;;
      aarch64 | arm64) rid="linux-arm64" ;;
      *) fail "unsupported Linux architecture: $arch" ;;
    esac
    ;;
  *)
    fail "unsupported operating system: $os. On Windows, use scripts/install.ps1."
    ;;
esac

asset="devdash-agent-$rid.tar.gz"
proto="=https"
if [ -n "${DEVDASH_BASE_URL:-}" ]; then
  base="${DEVDASH_BASE_URL%/}"
  proto="=https,http"
elif [ "$version" = "latest" ]; then
  base="https://github.com/$repo/releases/latest/download"
else
  base="https://github.com/$repo/releases/download/$version"
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT INT TERM

say "Downloading $asset ($version) from $base"
curl -fsSL --proto "$proto" --tlsv1.2 -o "$tmp/$asset" "$base/$asset" \
  || fail "could not download $base/$asset"
curl -fsSL --proto "$proto" --tlsv1.2 -o "$tmp/checksums.txt" "$base/checksums.txt" \
  || fail "could not download $base/checksums.txt"

# checksums.txt has one "<sha256>  <file name>" line per asset.
expected="$(awk -v name="$asset" '{ file = $2; sub(/^\*/, "", file); if (file == name) { print $1; exit } }' "$tmp/checksums.txt")"
[ -n "$expected" ] || fail "checksums.txt has no entry for $asset"
if command -v sha256sum >/dev/null 2>&1; then
  actual="$(sha256sum "$tmp/$asset" | awk '{ print $1 }')"
elif command -v shasum >/dev/null 2>&1; then
  actual="$(shasum -a 256 "$tmp/$asset" | awk '{ print $1 }')"
else
  fail "neither sha256sum nor shasum was found, so the download cannot be verified."
fi
[ "$actual" = "$expected" ] || fail "checksum mismatch for $asset: expected $expected, got $actual"
say "Checksum OK"

tar -xzf "$tmp/$asset" -C "$tmp" devdash-agent
chmod 755 "$tmp/devdash-agent"
if [ "$os" = "Darwin" ]; then
  # curl does not quarantine downloads, but a copy that came through a browser would be.
  xattr -d com.apple.quarantine "$tmp/devdash-agent" 2>/dev/null || true
fi

mkdir -p "$install_dir"
# Rename into place so a running agent keeps its old file until the service restarts.
mv -f "$tmp/devdash-agent" "$install_dir/.devdash-agent.new"
mv -f "$install_dir/.devdash-agent.new" "$install_dir/devdash-agent"
agent="$install_dir/devdash-agent"
say "Installed $("$agent" --version) to $agent"

case ":$PATH:" in
  *":$install_dir:"*) ;;
  *) say "Note: $install_dir is not on your PATH; add it, or run $agent by its full path." ;;
esac

if [ "${DEVDASH_NO_SERVICE:-0}" = "1" ]; then
  say "DEVDASH_NO_SERVICE=1: skipped the service. Run: $agent install --hub-url <url> --api-key <key>"
  exit 0
fi

exec "$agent" install "$@"
