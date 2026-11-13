#!/usr/bin/env bash
# Downloads the public annual reports (Form 10-K) FinDoc ingests into ./sources/ (gitignored: never commit the PDFs).
# Every URL is the company's own investor-relations copy of its latest 10-K (checked 13 Nov 2026: each downloads a PDF
# whose cover says "FORM 10-K" for the fiscal year in the file name). Fiscal years differ: Apple's ends in September.
# Usage (from the repo root):  ./scripts/download-sources.sh
# Re-running is safe: files already downloaded are skipped.
set -euo pipefail

cd "$(dirname "$0")/.."
DEST="sources"
mkdir -p "$DEST"

# local-file-name|url   (the file name without .pdf is the report name used in filters and citations)
SOURCES=(
  # Apple, fiscal year ended 26 Sep 2026 (123 pages)
  "apple-fy2026.pdf|https://s2.q4cdn.com/470004039/files/doc_earnings/2026/q4/filing/10K-Q4-2026-as-filed.pdf"
  # Alphabet (Google), fiscal year ended 31 Dec 2025 (99 pages)
  "alphabet-fy2025.pdf|https://s206.q4cdn.com/479360582/files/doc_financials/2025/q4/GOOG-10-K-2025.pdf"
  # Amazon, fiscal year ended 31 Dec 2025 (92 pages: the 12-page shareholder letter, then the 10-K)
  "amazon-fy2025.pdf|https://s2.q4cdn.com/299287126/files/doc_financials/2026/ar/Amazon-2025-Annual-Report.pdf"
  # Tesla, fiscal year ended 31 Dec 2025 (139 pages)
  "tesla-fy2025.pdf|https://ir.tesla.com/_flysystem/s3/sec/000162828026003952/tsla-20251231-gen.pdf"
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
  if curl -fsSL --retry 3 --max-time 180 -A "Mozilla/5.0 (FinDoc downloader)" -o "$out.part" "$url" \
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
