#!/usr/bin/env bash
# The key that signs DevDash releases.
#
# A release is signed in the release workflow: checksums.txt.sig is an ECDSA P-256 / SHA-256
# signature over checksums.txt, made with a private key that lives only in the GitHub secret
# DEVDASH_RELEASE_SIGNING_KEY. Agents refuse to install a release whose signature does not match the
# public key they were built with, so that public key is written into the source, in three places:
#
#   src/DevDash.Agent/Updates/ReleaseSigning.cs   what `devdash-agent update` and the dashboard's Update trust
#   scripts/install.sh, scripts/install.ps1       what the install one-liners check
#
# Each has the key between two marker lines (devdash-release-public-key:begin / :end). This script
# keeps the three identical, so nobody pastes a key by hand.
#
# Usage:
#   scripts/release-signing-key.sh generate [private-key-file]
#       Make a new key pair, write the public key into the three files, and say what to do next.
#       The private key goes to the file (default ~/devdash-release-signing-key.pem, never into the repository).
#   scripts/release-signing-key.sh apply <public-key.pem> [--root <dir>]
#       Write an existing public key into the three files (under <dir> instead of this repository, if given).
#   scripts/release-signing-key.sh public <private-key-file>
#       Print the public key of a private key.
#   scripts/release-signing-key.sh check [--require-key]
#       Check that the three copies are identical. With --require-key, also that it is a real key
#       and not the placeholder. Used by CI.
#   scripts/release-signing-key.sh check-private
#       Check that the private key in $DEVDASH_RELEASE_SIGNING_KEY is the other half of the public key
#       in the source. Used by the release workflow before it builds anything.
#   scripts/release-signing-key.sh sign <dir>
#       Sign <dir>/checksums.txt with the private key in $DEVDASH_RELEASE_SIGNING_KEY, writing
#       <dir>/checksums.txt.sig, and verify the result. Used by the release workflow.
#
# The same thing by hand, for the curious:
#   openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out private.pem
#   openssl pkey -in private.pem -pubout -out public.pem
#   openssl dgst -sha256 -sign private.pem -out checksums.txt.sig checksums.txt
#   openssl dgst -sha256 -verify public.pem -signature checksums.txt.sig checksums.txt
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
marker="devdash-release-public-key"
key_files=(src/DevDash.Agent/Updates/ReleaseSigning.cs scripts/install.sh scripts/install.ps1)
secret_name="DEVDASH_RELEASE_SIGNING_KEY"

fail() { printf 'release-signing-key.sh: %s\n' "$*" >&2; exit 1; }
command -v openssl >/dev/null 2>&1 || fail "openssl is required."

# The base64 body of the key between the markers of one file, on one line.
embedded_body() { # <file>
  awk -v marker="$marker" '
    index($0, marker ":begin") { inside = 1; next }
    index($0, marker ":end") { inside = 0 }
    inside && /BEGIN PUBLIC KEY/ { body = 1; next }
    inside && /END PUBLIC KEY/ { body = 0 }
    inside && body { gsub(/[ \t\r]/, ""); printf "%s", $0 }
  ' "$1"
}

# The base64 body of a PEM public key file, on one line. Fails unless it is a P-256 public key.
pem_body() { # <public-key.pem>
  openssl pkey -pubin -in "$1" -noout -text 2>/dev/null | grep -Eq 'prime256v1|P-256' \
    || fail "$1 is not an ECDSA P-256 (prime256v1) public key."
  openssl pkey -pubin -in "$1" -pubout 2>/dev/null | grep -v -- '-----' | tr -d ' \t\r\n'
}

# Replaces the lines between BEGIN PUBLIC KEY and END PUBLIC KEY inside the markers, keeping the indentation.
write_key() { # <file> <public-key.pem>
  local file="$1" pem="$2" tmp
  [ -f "$file" ] || fail "$file does not exist."
  if ! grep -q "$marker:begin" "$file" || ! grep -q "$marker:end" "$file"; then fail "$file has no $marker markers."; fi
  tmp="$(mktemp)"
  awk -v marker="$marker" -v pem="$pem" '
    index($0, marker ":begin") { inside = 1 }
    index($0, marker ":end") { inside = 0 }
    inside && /END PUBLIC KEY/ {
      indent = $0; sub(/[^ \t].*$/, "", indent)
      while ((getline line < pem) > 0) {
        gsub(/\r/, "", line)
        if (line != "" && line !~ /^-----/) { print indent line }
      }
      close(pem)
      body = 0
    }
    inside && body { next }
    { print }
    inside && /BEGIN PUBLIC KEY/ { body = 1 }
  ' "$file" >"$tmp"
  cat "$tmp" >"$file" # keeps the file mode (install.sh is executable)
  rm -f "$tmp"
}

apply_key() { # <public-key.pem> <root>
  local pem="$1" root="$2" want file
  want="$(pem_body "$pem")"
  for file in "${key_files[@]}"; do
    write_key "$root/$file" "$pem"
    [ "$(embedded_body "$root/$file")" = "$want" ] || fail "could not write the key into $root/$file."
    echo "  wrote the public key into $file"
  done
}

# Prints the embedded key body, after checking the three copies agree.
checked_body() {
  local first="" body file
  for file in "${key_files[@]}"; do
    body="$(embedded_body "$repo/$file")"
    [ -n "$body" ] || fail "$file has no key between its $marker markers."
    if [ -z "$first" ]; then
      first="$body"
    elif [ "$body" != "$first" ]; then
      fail "$file has a different release public key from ${key_files[0]}. Run: scripts/release-signing-key.sh apply <public-key.pem>"
    fi
  done
  printf '%s' "$first"
}

is_placeholder() { case "$1" in *PLACEHOLDER*) return 0 ;; *) return 1 ;; esac; }

# Writes the embedded public key as a PEM file.
embedded_pem() { # <out file>
  local body
  body="$(checked_body)"
  is_placeholder "$body" && fail "the source still has the placeholder release key. Run: scripts/release-signing-key.sh generate"
  { echo "-----BEGIN PUBLIC KEY-----"; printf '%s\n' "$body" | fold -w 64; echo "-----END PUBLIC KEY-----"; } >"$1"
  openssl pkey -pubin -in "$1" -noout 2>/dev/null || fail "the release public key in the source is not a valid key."
}

# Puts the private key from the environment into a file only this user can read; removed on exit.
private_key_from_env() {
  [ -n "${DEVDASH_RELEASE_SIGNING_KEY:-}" ] \
    || fail "the $secret_name secret is not set. A release must be signed: see 'Signing key' in the README."
  work="$(mktemp -d)"
  trap 'rm -rf "$work"' EXIT
  (umask 077 && printf '%s\n' "$DEVDASH_RELEASE_SIGNING_KEY" >"$work/private.pem")
  openssl pkey -in "$work/private.pem" -noout 2>/dev/null || fail "$secret_name does not hold a PEM private key."
  openssl pkey -in "$work/private.pem" -pubout -out "$work/derived.pem" 2>/dev/null
}

check_private_matches_source() {
  embedded_pem "$work/embedded.pem"
  [ "$(pem_body "$work/derived.pem")" = "$(pem_body "$work/embedded.pem")" ] \
    || fail "$secret_name is not the private half of the public key in ${key_files[0]}. Agents built from this source would refuse the release."
}

command="${1:-}"
[ "$#" -gt 0 ] && shift
case "$command" in
  generate)
    out="${1:-$HOME/devdash-release-signing-key.pem}"
    [ -e "$out" ] && fail "$out already exists; refusing to overwrite a private key. Move it away, or name another file."
    case "$(cd "$(dirname "$out")" && pwd)/" in "$repo"/*) fail "put the private key outside the repository, not in $out." ;; esac
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    (umask 077 && openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out "$out")
    openssl pkey -in "$out" -pubout -out "$work/public.pem"
    echo "Private key: $out (readable only by you)"
    apply_key "$work/public.pem" "$repo"
    echo
    echo "Public key:"
    cat "$work/public.pem"
    cat <<NEXT

Next:
  1. Store the private key in the repository's secrets:
       gh secret set $secret_name < "$out"
  2. Commit the three changed files (they hold the public key only) and push.
  3. Keep a copy of $out somewhere safe and offline, then delete it from this machine.
     If the private key is lost, agents already installed can never verify another release and
     each needs one reinstall; if it leaks, anyone holding it can sign a release they will accept.
NEXT
    ;;

  apply)
    pem="${1:-}"
    root="$repo"
    [ -f "$pem" ] || fail "usage: release-signing-key.sh apply <public-key.pem> [--root <dir>]"
    if [ "${2:-}" = "--root" ]; then
      root="${3:?--root needs a directory}"
    fi
    apply_key "$pem" "$root"
    ;;

  public)
    [ -f "${1:-}" ] || fail "usage: release-signing-key.sh public <private-key-file>"
    openssl pkey -in "$1" -pubout
    ;;

  check)
    body="$(checked_body)"
    if is_placeholder "$body"; then
      [ "${1:-}" = "--require-key" ] && fail "the source still has the placeholder release key. Run: scripts/release-signing-key.sh generate"
      echo "The three copies agree: all hold the placeholder (no release key has been generated yet)."
    else
      work="$(mktemp -d)"
      trap 'rm -rf "$work"' EXIT
      embedded_pem "$work/embedded.pem"
      echo "The three copies agree. Release public key (SHA-256 of its DER form):"
      openssl pkey -pubin -in "$work/embedded.pem" -outform DER | openssl dgst -sha256 | awk '{ print "  " $NF }'
    fi
    ;;

  check-private)
    private_key_from_env
    check_private_matches_source
    echo "$secret_name matches the release public key in the source."
    ;;

  sign)
    dir="${1:-}"
    [ -f "$dir/checksums.txt" ] || fail "usage: release-signing-key.sh sign <dir containing checksums.txt>"
    private_key_from_env
    # A test build signs with its own throwaway key and passes its public half here; a release never sets this.
    if [ -n "${DEVDASH_TEST_PUBLIC_KEY_FILE:-}" ]; then
      cp "$DEVDASH_TEST_PUBLIC_KEY_FILE" "$work/embedded.pem"
    else
      check_private_matches_source
    fi
    openssl dgst -sha256 -sign "$work/private.pem" -out "$dir/checksums.txt.sig" "$dir/checksums.txt"
    openssl dgst -sha256 -verify "$work/embedded.pem" -signature "$dir/checksums.txt.sig" "$dir/checksums.txt" >/dev/null \
      || fail "the signature just made does not verify against the public key."
    echo "Signed $dir/checksums.txt -> checksums.txt.sig"
    ;;

  *)
    sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'
    exit 64
    ;;
esac
