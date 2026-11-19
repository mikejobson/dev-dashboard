#!/usr/bin/env bash
# Publishes devdash-agent as a self-contained single-file executable for each platform and packs the
# release assets into dist/:
#
#   devdash-agent-<rid>.tar.gz   (macOS, Linux)
#   devdash-agent-<rid>.zip      (Windows: devdash-agent.exe + devdash-agentw.exe)
#   version.txt                  (the version, so an agent can ask "what is the latest release?")
#   checksums.txt                (SHA-256 of every archive and of version.txt)
#
# The release workflow then signs checksums.txt (scripts/release-signing-key.sh sign dist), which is
# what makes the release installable by `devdash-agent update`.
#
# Usage: scripts/publish-agent.sh <version> [rid...]
#   scripts/publish-agent.sh 0.2.0                  # all platforms
#   scripts/publish-agent.sh 0.2.0 osx-arm64        # just this one
#
# Everything cross-publishes from any OS with the .NET SDK: there is no native compilation, only a
# prebuilt runtime per platform fetched from NuGet. Used by .github/workflows/release.yml.
#
# Environment (for scripts/test-ota.sh; a release sets neither):
#   DEVDASH_DIST          write the assets here instead of dist/
#   DEVDASH_PUBLISH_ARGS  extra arguments for `dotnet publish`, space-separated
set -euo pipefail

version="${1:?usage: scripts/publish-agent.sh <version> [rid...]}"
shift
if [ "$#" -gt 0 ]; then
  rids=("$@")
else
  rids=(osx-arm64 osx-x64 win-x64 win-arm64 linux-x64 linux-arm64)
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
project="$root/src/DevDash.Agent/DevDash.Agent.csproj"
dist="${DEVDASH_DIST:-$root/dist}"
work="$dist/work"
rm -rf "$work"
mkdir -p "$work"
dist="$(cd "$dist" && pwd)"
work="$dist/work"
extra=()
if [ -n "${DEVDASH_PUBLISH_ARGS:-}" ]; then
  read -r -a extra <<<"$DEVDASH_PUBLISH_ARGS"
fi

# zip is not on every machine that can build (Git Bash on Windows has none).
make_zip() { # <zip file> <dir> <file>...
  local out="$1" dir="$2"
  shift 2
  rm -f "$out"
  if command -v zip >/dev/null 2>&1; then
    (cd "$dir" && zip -q -X "$out" "$@")
  elif command -v 7z >/dev/null 2>&1; then
    (cd "$dir" && 7z a -tzip -bso0 -bsp0 "$out" "$@" >/dev/null)
  else
    local python
    python="$(command -v python3 || command -v python)" || { echo "need zip, 7z or python to make $out" >&2; exit 1; }
    (cd "$dir" && "$python" -c 'import sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED) as z:
    for name in sys.argv[2:]:
        z.write(name)' "$out" "$@")
  fi
}

publish() { # <rid> <output dir> [extra msbuild args...]
  local rid="$1" out="$2"
  shift 2
  # Not trimmed: configuration binding and the snapshot JSON serializer both use reflection, and a
  # trimmed build would drop members they need.
  dotnet publish "$project" -c Release -r "$rid" --self-contained true -o "$out" --nologo -v quiet \
    -p:Version="$version" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -p:GenerateDocumentationFile=false \
    ${extra[@]+"${extra[@]}"} \
    "$@"
}

for rid in "${rids[@]}"; do
  out="$work/$rid"
  echo "==> $rid"
  publish "$rid" "$out"
  case "$rid" in
    win-*)
      # The same program built for the Windows subsystem, so the scheduled task shows no console window.
      publish "$rid" "$work/$rid-w" -p:DevDashWindowless=true
      cp "$work/$rid-w/devdash-agentw.exe" "$out/"
      make_zip "$dist/devdash-agent-$rid.zip" "$out" devdash-agent.exe devdash-agentw.exe
      ;;
    *)
      chmod 755 "$out/devdash-agent"
      tar -C "$out" -czf "$dist/devdash-agent-$rid.tar.gz" devdash-agent
      ;;
  esac
done

rm -rf "$work"
cd "$dist"
# version.txt is covered by the checksums, and so by the signature: it is how an agent learns, and
# can trust, which version a folder of release files is.
printf '%s\n' "$version" >version.txt
assets=()
for file in devdash-agent-*.tar.gz devdash-agent-*.zip version.txt; do
  if [ -f "$file" ]; then assets+=("$file"); fi
done
# --binary / -b so Git Bash on Windows hashes the bytes and writes the same lines as everywhere else.
if command -v sha256sum >/dev/null 2>&1; then
  sha256sum -b "${assets[@]}" | sed 's/ \*/  /' >checksums.txt
else
  shasum -a 256 -b "${assets[@]}" | sed 's/ \*/  /' >checksums.txt
fi
echo
cat checksums.txt
