#!/usr/bin/env bash
# Compress committed Playwright PNG baselines with pngquant.
# Gentle settings: keeps quality ≥85, strips metadata, in-place rewrite.
# Run after `npm run test:update` whenever new baselines are generated.
set -euo pipefail

if ! command -v pngquant >/dev/null 2>&1; then
  echo "pngquant not found. Install with: brew install pngquant" >&2
  exit 1
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SNAPSHOT_DIR="$ROOT/tests/__snapshots__"

if [ ! -d "$SNAPSHOT_DIR" ]; then
  echo "No snapshots directory at $SNAPSHOT_DIR — nothing to compress."
  exit 0
fi

# --quality=85-100: only compress if ≥85% quality can be preserved
# --skip-if-larger: keep original if compression makes it bigger
# --strip: drop metadata
# --force --ext .png: overwrite in place
find "$SNAPSHOT_DIR" -name "*.png" -type f -print0 \
  | xargs -0 pngquant \
      --quality=85-100 \
      --skip-if-larger \
      --strip \
      --force \
      --ext .png \
      --speed 1

echo "Baselines compressed."
