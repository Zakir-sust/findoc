# TaxDoc Assistant — RAG over public tax treaties in .NET (Stage C)

Status: **scaffold (block C07)**. Upload → queue → worker → PdfPig text works. Chunking (C08) and embeddings (C09) are TODOs.

## Layout
| Path | What |
|---|---|
| `src/TaxDoc.Api` | Minimal API: `POST /documents` (multipart PDF upload), `GET /documents/{id}`. Applies EF migrations on start. |
| `src/TaxDoc.Ingestion` | Worker: consumes RabbitMQ `ingest`, extracts text with PdfPig, saves to Postgres, acks after the commit. |
| `src/TaxDoc.Core` | EF Core model (`Document`, `Chunk` with `vector(1024)` + HNSW cosine index), messaging contract, `IChunker` (TODO C08). |
| `tests/TaxDoc.Tests` | xUnit tests. |
| `evals/` | `golden.draft.json` — 30 questions, **unverified draft**. |
| `data/` | `rates.draft.json` — 5 treaties × 3 income types, **unverified draft**. |
| `scripts/download-sources.sh` | Downloads the public treaty PDFs into `sources/` (gitignored; PDFs are never committed). |
| `docs/learned.md` | Learn-backs. |

Target framework: `net9.0` (set once in `Directory.Build.props`).

## Run it
Prereqs: .NET 9 SDK, Docker, Ollama on the host (`qwen3.5:9b`, `qwen3-embedding:0.6b`; not used yet).

```bash
docker compose up -d                          # Postgres+pgvector :5432, RabbitMQ :5672 (UI :15672, guest/guest)
./scripts/download-sources.sh                 # treaty PDFs -> sources/
dotnet run --project src/TaxDoc.Api           # http://localhost:5080 (terminal 1)
dotnet run --project src/TaxDoc.Ingestion     # worker (terminal 2)

curl -F "file=@sources/us-uk-2001.pdf" http://localhost:5080/documents
# worker log: "Extracted 96574 characters from 39 pages of us-uk-2001.pdf (...)"
curl http://localhost:5080/documents/<id>     # status: Extracted, pageCount, textLength
```

Tests: `dotnet build && dotnet test`.

Uploaded files are stored in `storage/uploads/` (gitignored).
