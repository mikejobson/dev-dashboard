#!/usr/bin/env bash
# Publishes devdash-agent as a self-contained single-file executable for each platform and packs the
# release assets into dist/:
#
#   devdash-agent-<rid>.tar.gz   (macOS, Linux)
#   devdash-agent-<rid>.zip      (Windows: devdash-agent.exe + devdash-agentw.exe)
#   checksums.txt                (SHA-256 of every archive)
#
# Usage: scripts/publish-agent.sh <version> [rid...]
#   scripts/publish-agent.sh 0.2.0                  # all platforms
#   scripts/publish-agent.sh 0.2.0 osx-arm64        # just this one
#
# Everything cross-publishes from any OS with the .NET SDK: there is no native compilation, only a
# prebuilt runtime per platform fetched from NuGet. Used by .github/workflows/release.yml.
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
dist="$root/dist"
work="$dist/work"
rm -rf "$work"
mkdir -p "$work"

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
      rm -f "$dist/devdash-agent-$rid.zip"
      (cd "$out" && zip -q -X "$dist/devdash-agent-$rid.zip" devdash-agent.exe devdash-agentw.exe)
      ;;
    *)
      chmod 755 "$out/devdash-agent"
      tar -C "$out" -czf "$dist/devdash-agent-$rid.tar.gz" devdash-agent
      ;;
  esac
done

rm -rf "$work"
cd "$dist"
if command -v sha256sum >/dev/null 2>&1; then
  sha256sum devdash-agent-*.tar.gz devdash-agent-*.zip 2>/dev/null >checksums.txt || true
else
  shasum -a 256 devdash-agent-*.tar.gz devdash-agent-*.zip 2>/dev/null >checksums.txt || true
fi
echo
cat checksums.txt
