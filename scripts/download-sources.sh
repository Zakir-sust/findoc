#!/usr/bin/env bash
# Downloads the public treaty PDFs TaxDoc ingests into ./sources/ (gitignored — never commit the PDFs).
# All URLs are official sources: U.S. Treasury, IRS, United Nations.
# Usage (from the repo root):  ./scripts/download-sources.sh
# Re-running is safe: files already downloaded are skipped.
set -euo pipefail

cd "$(dirname "$0")/.."
DEST="sources"
mkdir -p "$DEST"

TREASURY="https://home.treasury.gov/system/files/131"
IRS="https://www.irs.gov/pub/irs-trty"

# local-file-name|url
SOURCES=(
  # Model conventions (the templates bilateral treaties follow)
  "us-model-2016.pdf|$TREASURY/Treaty-US-Model-2016_1.pdf"
  "un-model-2017.pdf|https://www.un.org/esa/ffd/wp-content/uploads/2018/05/MDT_2017.pdf"
  # Bilateral treaties (the 5 in data/rates.draft.json). Protocols change some rates, so they're included.
  "us-uk-2001.pdf|$TREASURY/Treaty-UK-7-24-2001.pdf"
  "us-germany-1989.pdf|$IRS/germany.pdf"
  "us-germany-protocol-2006.pdf|$TREASURY/Treaty-Germany-Protocol-6-1-2006.pdf"
  "us-india-1989.pdf|$IRS/india.pdf"
  "us-japan-2003.pdf|$TREASURY/Treaty-Japan-11-6-2003.pdf"
  "us-japan-protocol-2013.pdf|$TREASURY/Treaty-Japan-Pr2-1-24-2013.pdf"
  "us-bangladesh-2004.pdf|$TREASURY/Treaty-Bangladesh-1-27-2004.pdf"
)

failed=0
for entry in "${SOURCES[@]}"; do
  name="${entry%%|*}"
  url="${entry#*|}"
  out="$DEST/$name"

  if [[ -s "$out" ]]; then
    echo "skip  $name (already downloaded)"
    continue
  fi

  echo "get   $name  <-  $url"
  if curl -fsSL --retry 3 --max-time 180 -A "Mozilla/5.0 (TaxDoc downloader)" -o "$out.part" "$url" \
     && [[ "$(head -c 4 "$out.part")" == "%PDF" ]]; then
    mv "$out.part" "$out"
  else
    echo "FAIL  $name (download failed or not a PDF)" >&2
    rm -f "$out.part"
    failed=$((failed + 1))
  fi
done

echo
ls -lh "$DEST"/*.pdf 2>/dev/null || true
if (( failed > 0 )); then
  echo "$failed download(s) failed." >&2
  exit 1
fi
