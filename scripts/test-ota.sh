#!/usr/bin/env bash
# End-to-end test of the agent's self-update, with real binaries on this operating system.
#
# It builds the agent twice (9.9.0 and 9.9.1) with a throwaway signing key compiled in, lays out a
# pretend release site signed with that key, serves it from 127.0.0.1, and then:
#
#   1. installs 9.9.0 into a temporary folder (with the install script, signature check included);
#   2. runs `devdash-agent update` and checks the folder now holds 9.9.1, with 9.9.0 kept as .old;
#   3. starts 9.9.1 as the agent with a hub that cannot be reached, and checks it puts 9.9.0 back
#      by itself and exits with the "restart me" code;
#   4. offers a release whose program does not run, one changed after signing, one signed by
#      someone else, an unsigned one and a replayed one, and checks each is refused with nothing changed;
#   5. updates again, by version number, starts that version as the agent against a real hub (built
#      from this checkout, on a spare port, with its own empty database), and checks that the hub
#      accepting its snapshot confirms the update and removes the .old.
#
# Nothing is installed as a service and every update runs with --no-restart. On macOS and Linux HOME
# points into the temporary folder for every agent run, so the real configuration, a real installed
# service and ~/.local/bin are never read or touched. (Windows finds the profile folder without
# asking the environment; there this is meant for a CI runner.)
#
# Usage: scripts/test-ota.sh [rid]      (rid defaults to this machine's: osx-arm64, linux-x64, win-x64, ...)
#   DEVDASH_OTA_WORK=<dir>   keep the work folder there instead of a deleted temporary one
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
old="9.9.0"
new="9.9.1"

say() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nFAILED: %s\n' "$*" >&2; exit 1; }

windows=0
case "$(uname -s)" in
  Darwin) os="osx" ;;
  Linux) os="linux" ;;
  MINGW* | MSYS* | CYGWIN*) os="win"; windows=1 ;;
  *) fail "unsupported operating system $(uname -s)" ;;
esac
case "$(uname -m)" in
  x86_64 | amd64) cpu="x64" ;;
  arm64 | aarch64) cpu="arm64" ;;
  *) fail "unsupported architecture $(uname -m)" ;;
esac
rid="${1:-$os-$cpu}"

# On Windows `python3` can be the Microsoft Store placeholder, so `python` is tried first there.
if [ "$windows" = 1 ]; then
  python="$(command -v python || command -v python3)" || fail "python is needed to serve the release folder."
else
  python="$(command -v python3 || command -v python)" || fail "python is needed to serve the release folder."
fi
command -v openssl >/dev/null 2>&1 || fail "openssl is needed to sign the test release."

if [ -n "${DEVDASH_OTA_WORK:-}" ]; then
  work="$DEVDASH_OTA_WORK"
  rm -rf "$work"
  mkdir -p "$work"
else
  work="$(mktemp -d)"
fi
work="$(cd "$work" && pwd)"
server_pid=""
agent_pid=""
hub_pid=""
cleanup() {
  local pid
  for pid in "$server_pid" "$hub_pid" "$agent_pid"; do
    if [ -n "$pid" ]; then kill "$pid" 2>/dev/null || true; fi
  done
  if [ -z "${DEVDASH_OTA_WORK:-}" ]; then rm -rf "$work" 2>/dev/null || true; fi
}
trap cleanup EXIT

# A path the native tools on this OS understand (dotnet and python on Windows want C:/..., not /c/...).
native() { if [ "$windows" = 1 ]; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

if [ "$windows" = 1 ]; then
  exe="devdash-agent.exe"
  programs=(devdash-agent.exe devdash-agentw.exe)
  asset="devdash-agent-$rid.zip"
else
  exe="devdash-agent"
  programs=(devdash-agent)
  asset="devdash-agent-$rid.tar.gz"
fi

site="$work/site"
install="$work/install"
agent="$install/$exe"
state="$install/devdash-agent.update.json"
mkdir -p "$site/download" "$install" "$work/home" "$work/roots/widget"

# ---- a throwaway signing key, compiled into the test builds -----------------------------------
say "Generating a throwaway signing key"
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out "$work/test-private.pem"
openssl pkey -in "$work/test-private.pem" -pubout -out "$work/test-public.pem"
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out "$work/stranger-private.pem"

# Signs a release folder the way the release workflow does, with the test key unless another is given.
sign() { # <release dir> [private key]
  local key="${2:-$work/test-private.pem}"
  openssl pkey -in "$key" -pubout -out "$work/signing-public.pem"
  DEVDASH_RELEASE_SIGNING_KEY="$(cat "$key")" DEVDASH_TEST_PUBLIC_KEY_FILE="$work/signing-public.pem" \
    "$repo/scripts/release-signing-key.sh" sign "$1" >/dev/null
}

sha256() { if command -v sha256sum >/dev/null 2>&1; then sha256sum -b "$1" | awk '{ print $1 }'; else shasum -a 256 -b "$1" | awk '{ print $1 }'; fi; }

# Writes version.txt and checksums.txt for a release folder that already holds the archive.
describe() { # <release dir> <version>
  printf '%s\n' "$2" >"$1/version.txt"
  { printf '%s  %s\n' "$(sha256 "$1/$asset")" "$asset"; printf '%s  %s\n' "$(sha256 "$1/version.txt")" "version.txt"; } >"$1/checksums.txt"
}

# ---- the two real releases, built and packed by the same script the release workflow uses --------
for version in "$old" "$new"; do
  say "Building devdash-agent $version for $rid (test signing key)"
  DEVDASH_DIST="$site/download/v$version" DEVDASH_PUBLISH_ARGS="-p:DevDashUpdatePublicKeyFile=$(native "$work/test-public.pem")" \
    "$repo/scripts/publish-agent.sh" "$version" "$rid" >/dev/null
  sign "$site/download/v$version"
  ls -l "$site/download/v$version"
done
mkdir -p "$site/latest"
cp -R "$site/download/v$new" "$site/latest/download"

# ---- serve it -----------------------------------------------------------------------------------
say "Serving $site on 127.0.0.1"
"$python" -u -c '
import functools, http.server, socketserver, sys
handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=sys.argv[1])
class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
with Server(("127.0.0.1", 0), handler) as httpd:
    print(httpd.server_address[1], flush=True)
    httpd.serve_forever()
' "$(native "$site")" >"$work/port.txt" 2>"$work/server.log" &
server_pid=$!
for _ in $(seq 1 100); do
  [ -s "$work/port.txt" ] && break
  sleep 0.1
done
port="$(tr -d '\r\n' <"$work/port.txt")"
[ -n "$port" ] || fail "the release server did not start: $(cat "$work/server.log")"
base="http://127.0.0.1:$port"
echo "$base"

# Every agent run: its own HOME (no real config, no real service), releases from the local server.
agent_environment() {
  export HOME="$work/home" XDG_CONFIG_HOME="" XDG_STATE_HOME="" DEVDASH_Agent__Updates__BaseUrl="$base"
  if [ "$windows" = 1 ]; then
    USERPROFILE="$(native "$work/home")" LOCALAPPDATA="$(native "$work/home/AppData/Local")"
    export USERPROFILE LOCALAPPDATA
  fi
}
run_agent() { (agent_environment && exec "$@"); }
# In the background, as the process itself (not a shell around it), so it can be waited for and stopped.
start_agent() {
  (agent_environment && exec "$@") >"$agent_log" 2>&1 &
  agent_pid=$!
}
version_of() { run_agent "$1" --version | tr -d '\r'; }
expect_version() { # <version> <what>
  local got
  got="$(version_of "$agent")"
  [ "$got" = "devdash-agent $1" ] || fail "$2: expected devdash-agent $1, the installed program says '$got'"
  echo "ok: installed program is $got"
}
expect_file() { [ -f "$1" ] || fail "expected $1 to exist"; echo "ok: $(basename "$1") exists"; }
expect_no_file() { [ ! -e "$1" ] || fail "expected $1 not to exist"; echo "ok: no $(basename "$1")"; }
expect_in() { grep -q -- "$1" "$2" || { cat "$2"; fail "expected '$1' in $(basename "$2")"; }; echo "ok: says '$1'"; }

# Runs `update` expecting it to be refused, with a reason, and to leave the installation alone.
expect_refused() { # <version> <reason> <installed version>
  local out="$work/refused-$1.txt" code=0
  run_agent "$agent" update --version "$1" --no-restart >"$out" 2>&1 || code=$?
  [ "$code" = 1 ] || { cat "$out"; fail "update to $1 should have failed with exit code 1, got $code"; }
  expect_in "$2" "$out"
  expect_version "$3" "after the refused $1"
  expect_no_file "$install/.devdash-agent-update"
  if grep -q '"pending"' "$state" 2>/dev/null; then cat "$state"; fail "a refused update left a pending record"; fi
}

# ---- 1. install 9.9.0 with the install script, checking the signature ------------------------------
say "Installing $old with the install script (test key written into a copy of it)"
mkdir -p "$work/keyed/scripts" "$work/keyed/src/DevDash.Agent/Updates"
cp "$repo/scripts/install.sh" "$repo/scripts/install.ps1" "$work/keyed/scripts/"
cp "$repo/src/DevDash.Agent/Updates/ReleaseSigning.cs" "$work/keyed/src/DevDash.Agent/Updates/"
"$repo/scripts/release-signing-key.sh" apply "$work/test-public.pem" --root "$work/keyed"

installer() { # <install dir> <version> <powershell|pwsh|sh>
  if [ "$windows" = 1 ]; then
    DEVDASH_BASE_URL="$base/download/v$2" DEVDASH_INSTALL_DIR="$(cygpath -w "$1")" DEVDASH_NO_SERVICE=1 \
      "$3" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$(cygpath -w "$work/keyed/scripts/install.ps1")"
  else
    DEVDASH_BASE_URL="$base/download/v$2" DEVDASH_INSTALL_DIR="$1" DEVDASH_NO_SERVICE=1 HOME="$work/home" sh "$work/keyed/scripts/install.sh"
  fi
}

if [ "$windows" = 1 ]; then shells=(powershell pwsh); else shells=(sh); fi
installer "$install" "$old" "${shells[0]}" | tee "$work/install.txt"
expect_in "Checksum OK" "$work/install.txt"
expect_in "Signature OK" "$work/install.txt"
expect_version "$old" "after installing"

# The install scripts must refuse a release signed by anyone else (and, on Windows, in both PowerShells).
mkdir -p "$site/download/v9.8.0"
cp "$site/download/v$old/$asset" "$site/download/v9.8.0/"
describe "$site/download/v9.8.0" "9.8.0"
sign "$site/download/v9.8.0" "$work/stranger-private.pem"
for shell in "${shells[@]}"; do
  code=0
  installer "$work/install-$shell" "$old" "$shell" >"$work/install-$shell.txt" 2>&1 || code=$?
  [ "$code" = 0 ] || { cat "$work/install-$shell.txt"; fail "the install script failed under $shell"; }
  expect_in "Signature OK" "$work/install-$shell.txt"
  code=0
  installer "$work/install-$shell-bad" "9.8.0" "$shell" >"$work/install-$shell-bad.txt" 2>&1 || code=$?
  [ "$code" != 0 ] || { cat "$work/install-$shell-bad.txt"; fail "the install script ($shell) accepted a release signed with another key"; }
  expect_in "NOT valid" "$work/install-$shell-bad.txt"
  expect_no_file "$work/install-$shell-bad/$exe"
done

run_agent "$agent" verify-release --allow-test-key "$(native "$site/download/v$new")"
if run_agent "$agent" verify-release "$(native "$site/download/v$new")" >"$work/verify.txt" 2>&1; then
  fail "verify-release must reject a test-key build unless told to allow it"
fi
expect_in "TEST signing key" "$work/verify.txt"

# ---- 2. update to the latest release ----------------------------------------------------------------
say "update --check"
run_agent "$agent" update --check | tee "$work/check.txt"
expect_in "$new is available" "$work/check.txt"
expect_version "$old" "after --check"

say "update (to the latest release)"
run_agent "$agent" update --no-restart | tee "$work/update.txt"
expect_in "Installed $new (was $old)" "$work/update.txt"
expect_version "$new" "after update"
for program in "${programs[@]}"; do
  expect_file "$install/$program"
  expect_file "$install/$program.old"
done
expect_in "\"toVersion\": \"$new\"" "$state"
expect_in '"pending"' "$state"
expect_no_file "$install/.devdash-agent-update"
if [ "$windows" = 0 ]; then
  [ -x "$agent" ] || fail "the new program is not executable"
  cp "$install/$exe.old" "$work/previous"
  [ "$(version_of "$work/previous")" = "devdash-agent $old" ] || fail ".old is not $old"
  echo "ok: .old is $old"
fi

say "update again: already up to date"
run_agent "$agent" update --no-restart | tee "$work/again.txt"
expect_in "Already on $new" "$work/again.txt"

# ---- 3. the new version cannot reach its hub: it rolls itself back -------------------------------------
say "Starting $new as the agent with an unreachable hub: it must roll back to $old and exit 75"
code=0
agent_log="$work/run.log"
DEVDASH_Agent__Updates__VerifyMinutes=0.15 start_agent "$agent" run \
  --hub-url http://127.0.0.1:9 --api-key ddk_ota_test --root "$(native "$work/roots")" --no-fetch --no-watch
for _ in $(seq 1 180); do
  kill -0 "$agent_pid" 2>/dev/null || break
  sleep 1
done
if kill -0 "$agent_pid" 2>/dev/null; then
  kill "$agent_pid" 2>/dev/null || true
  cat "$work/run.log"
  fail "the agent was still running after 3 minutes; it should have rolled back and exited"
fi
wait "$agent_pid" || code=$?
agent_pid=""
tail -n 12 "$work/run.log"
[ "$code" = 75 ] || fail "expected exit code 75 (restart me) after rolling back, got $code"
echo "ok: exited 75"
expect_in "Rolled back from $new to $old" "$work/run.log"
expect_version "$old" "after the roll back"
expect_in '"outcome": "rolledBack"' "$state"
expect_in "\"failedVersion\": \"$new\"" "$state"
if grep -q '"pending"' "$state"; then cat "$state"; fail "the roll back left a pending record"; fi
for program in "${programs[@]}"; do
  expect_file "$install/$program"
  expect_no_file "$install/$program.old"
done
if [ "$windows" = 1 ]; then
  # The program that rolled itself back was running, so it could only be moved aside.
  expect_file "$install/$exe.bad"
fi

# ---- 4. releases that must be refused ----------------------------------------------------------------
say "A release whose program does not run"
bad="$site/download/v9.9.2"
mkdir -p "$bad" "$work/broken"
for program in "${programs[@]}"; do
  printf '#!/bin/sh\necho "this build is broken" >&2\nexit 3\n' >"$work/broken/$program"
  chmod 755 "$work/broken/$program"
done
"$python" -c '
import sys, tarfile, zipfile
out, folder, names = sys.argv[1], sys.argv[2], sys.argv[3:]
if out.endswith(".zip"):
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for name in names:
            z.write(folder + "/" + name, name)
else:
    with tarfile.open(out, "w:gz") as t:
        for name in names:
            t.add(folder + "/" + name, name)
' "$(native "$bad/$asset")" "$(native "$work/broken")" "${programs[@]}"
describe "$bad" "9.9.2"
sign "$bad"
expect_refused "9.9.2" "did not run correctly here" "$old"

say "A release changed after it was signed"
tampered="$site/download/v9.9.3"
mkdir -p "$tampered"
cp "$site/download/v$new/$asset" "$tampered/"
describe "$tampered" "9.9.3"
sign "$tampered"
printf 'x' >>"$tampered/$asset"
expect_refused "9.9.3" "checksum mismatch" "$old"

say "A release signed by someone else"
foreign="$site/download/v9.9.4"
mkdir -p "$foreign"
cp "$site/download/v$new/$asset" "$foreign/"
describe "$foreign" "9.9.4"
sign "$foreign" "$work/stranger-private.pem"
expect_refused "9.9.4" "not valid for this agent's release key" "$old"

say "A release with no signature"
unsigned="$site/download/v9.9.5"
mkdir -p "$unsigned"
cp "$site/download/v$new/$asset" "$unsigned/"
describe "$unsigned" "9.9.5"
expect_refused "9.9.5" "no signature" "$old"

say "A genuine release offered under another version's address"
cp -R "$site/download/v$new" "$site/download/v9.9.6"
expect_refused "9.9.6" "the signed files at that address are for $new" "$old"

say "An older release"
out="$work/downgrade.txt"
code=0
run_agent "$agent" update --version 9.8.0 --no-restart >"$out" 2>&1 || code=$?
[ "$code" = 1 ] || fail "a downgrade should be refused"
expect_in "does not downgrade" "$out"

# ---- 5. and a normal update still works afterwards, by version -------------------------------------------
say "update --version $new"
run_agent "$agent" update --version "v$new" --no-restart | tee "$work/update2.txt"
expect_in "Installed $new (was $old)" "$work/update2.txt"
expect_version "$new" "after the second update"
for program in "${programs[@]}"; do
  expect_file "$install/$program.old"
done
run_agent "$agent" status | tee "$work/status.txt" || true
expect_in "waiting to be confirmed" "$work/status.txt"
expect_in "TEST KEY" "$work/status.txt"

# ---- 6. the hub accepts a snapshot from the new version: the update is confirmed ---------------------------
say "Building the hub and starting it on a spare port with an empty database"
dotnet build "$repo/src/DevDash.Hub/DevDash.Hub.csproj" -c Release --nologo -v quiet -o "$(native "$work/hub")" >"$work/hub-build.log" 2>&1 \
  || { cat "$work/hub-build.log"; fail "could not build the hub"; }
hub_port="$("$python" -c 'import socket
s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()' | tr -d '\r\n')"
hub="http://127.0.0.1:$hub_port"
mkdir -p "$work/hubdata"
(cd "$work/hub" && ASPNETCORE_ENVIRONMENT=Development Urls="$hub" Hub__DataDirectory="$(native "$work/hubdata")" \
  GitHub__Enabled=false Hub__Updates__Enabled=false exec dotnet DevDash.Hub.dll) >"$work/hub.log" 2>&1 &
hub_pid=$!
for _ in $(seq 1 90); do
  [ -s "$work/hubdata/dev-agent-key.txt" ] && curl -fsS "$hub/healthz" >/dev/null 2>&1 && break
  kill -0 "$hub_pid" 2>/dev/null || { cat "$work/hub.log"; fail "the hub exited"; }
  sleep 1
done
curl -fsS "$hub/healthz" >/dev/null 2>&1 || { cat "$work/hub.log"; fail "the hub did not start on $hub"; }
echo "$hub"

say "Starting $new as the agent against that hub: its first accepted snapshot must confirm the update"
agent_log="$work/confirm.log"
start_agent "$agent" run --hub-url "$hub" --api-key-file "$(native "$work/hubdata/dev-agent-key.txt")" --machine-id ota-test \
  --root "$(native "$work/roots")" --no-fetch --no-watch
for _ in $(seq 1 120); do
  grep -q '"outcome": "succeeded"' "$state" 2>/dev/null && break
  kill -0 "$agent_pid" 2>/dev/null || break
  sleep 1
done
# Give it a moment to send the snapshot that carries the outcome, then stop it.
sleep 3
kill "$agent_pid" 2>/dev/null || true
wait "$agent_pid" 2>/dev/null || true
agent_pid=""
tail -n 8 "$work/confirm.log"
expect_in '"outcome": "succeeded"' "$state"
expect_in "Update confirmed" "$work/confirm.log"
if grep -q '"pending"' "$state"; then cat "$state"; fail "a confirmed update left a pending record"; fi
expect_version "$new" "after the update was confirmed"
for program in "${programs[@]}"; do
  expect_no_file "$install/$program.old"
done
curl -fsS "$hub/api/v1/machines" >"$work/machines.json"
expect_in '"agentVersion":"'"$new"'"' "$work/machines.json"
expect_in '"updateMode":"notify"' "$work/machines.json"
expect_in '"outcome":"succeeded"' "$work/machines.json"
kill "$hub_pid" 2>/dev/null || true
wait "$hub_pid" 2>/dev/null || true
hub_pid=""

say "All update checks passed on $rid"
