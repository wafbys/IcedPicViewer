#!/usr/bin/env bash
# Download FFmpeg LGPL shared libraries into src/native/ffmpeg/{rid}/
# Usage: ./tools/Fetch-FFmpegNatives.sh <linux-x64|linux-arm64|osx-arm64|osx-x64>
set -euo pipefail

RID="${1:-}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/src/native/ffmpeg/$RID"

if [[ -z "$RID" ]]; then
  echo "Usage: $0 <linux-x64|linux-arm64|osx-arm64|osx-x64>"
  exit 1
fi

mkdir -p "$DEST"

case "$RID" in
  linux-x64)
    PATTERN='ffmpeg-n8.1-latest-linux64-lgpl-shared'
    EXT='tar.xz'
    ;;
  linux-arm64)
    PATTERN='ffmpeg-n8.1-latest-linuxarm64-lgpl-shared'
    EXT='tar.xz'
    ;;
  osx-arm64|osx-x64)
    echo "BtbN does not ship macOS shared builds in the same feed."
    echo "Recommended:"
    echo "  brew install ffmpeg"
    echo "  export IPV_FFMPEG_ROOT=\$(brew --prefix ffmpeg)/lib"
    echo "Or copy libav*.dylib into: $DEST"
    if command -v brew >/dev/null 2>&1; then
      PREFIX="$(brew --prefix ffmpeg 2>/dev/null || true)"
      if [[ -n "$PREFIX" && -d "$PREFIX/lib" ]]; then
        echo "Found Homebrew ffmpeg at $PREFIX — copying dylibs to $DEST"
        cp -a "$PREFIX/lib"/libav*.dylib "$DEST/" 2>/dev/null || true
        cp -a "$PREFIX/lib"/libsw*.dylib "$DEST/" 2>/dev/null || true
        cp -a "$PREFIX/lib"/libpostproc*.dylib "$DEST/" 2>/dev/null || true
        echo "rid=$RID" > "$DEST/SOURCE.txt"
        echo "source=homebrew:$PREFIX" >> "$DEST/SOURCE.txt"
        echo "fetched=$(date -Iseconds)" >> "$DEST/SOURCE.txt"
        ls -la "$DEST"
        exit 0
      fi
    fi
    exit 1
    ;;
  *)
    echo "Unsupported RID: $RID"
    exit 1
    ;;
esac

API="https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest"
echo "Querying $API ..."
JSON=$(curl -fsSL -H "User-Agent: IcedPicViewer-FetchFFmpeg" "$API")
URL=$(echo "$JSON" | grep -oE "https://[^\"]+${PATTERN}[^\"]+\\.${EXT//./\\.}" | head -n1 || true)
if [[ -z "$URL" ]]; then
  # broader match
  URL=$(echo "$JSON" | grep -oE "https://[^\"]+lgpl-shared[^\"]+${RID/linux-x64/linux64}[^\"]*" | head -n1 || true)
fi
# parse via python if available for robustness
if [[ -z "$URL" ]] && command -v python3 >/dev/null 2>&1; then
  URL=$(echo "$JSON" | python3 -c "
import json,sys
rel=json.load(sys.stdin)
for a in rel.get('assets',[]):
  n=a['name']
  if 'lgpl-shared' in n and ('linux64' in n or 'linuxarm64' in n) and n.endswith('${EXT}'):
    if '${RID}'=='linux-x64' and 'linux64' in n and 'arm' not in n:
      print(a['browser_download_url']); break
    if '${RID}'=='linux-arm64' and 'linuxarm64' in n:
      print(a['browser_download_url']); break
")
fi

if [[ -z "$URL" ]]; then
  echo "Could not resolve download URL for $RID"
  exit 1
fi

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
ARCHIVE="$TMP/$(basename "$URL")"
echo "Downloading $URL ..."
curl -fL "$URL" -o "$ARCHIVE"
mkdir -p "$TMP/extract"
tar -xJf "$ARCHIVE" -C "$TMP/extract"

LIBDIR=$(find "$TMP/extract" -type d \( -name bin -o -name lib \) | while read -r d; do
  if ls "$d"/*avutil* >/dev/null 2>&1; then echo "$d"; break; fi
done)

if [[ -z "${LIBDIR:-}" ]]; then
  echo "Could not find avutil in archive"
  exit 1
fi

echo "Copying from $LIBDIR → $DEST"
cp -a "$LIBDIR"/* "$DEST/"
{
  echo "rid=$RID"
  echo "source=$URL"
  echo "fetched=$(date -Iseconds)"
} > "$DEST/SOURCE.txt"
echo "Done."
ls -la "$DEST"
