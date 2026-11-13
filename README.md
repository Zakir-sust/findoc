# TaxDoc Assistant — RAG over public tax treaties in .NET (Stage C)

Status: **scaffold (block C07)**. Upload → queue → worker → PdfPig text works. Chunking (C08) and embeddings (C09) are TODOs.

## Layout
| Path | What |
|---|---|
| `src/TaxDoc.Api` | Minimal API: `POST /documents` (multipart PDF upload), `GET /documents/{id}`, `POST /chat` (SSE stream). Applies EF migrations on start. |
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

## Chat (`POST /chat`, block C10)
Streams the answer as Server-Sent Events. **Stub prompt for now** (fixed system message + your question; retrieval and citations come in C13).

```bash
curl -N -X POST http://localhost:5080/chat -H 'Content-Type: application/json' \
  -d '{"question":"Say hello in five words."}'
# data: {"text":"Hello"}
# data: {"text":","}
# ...
# event: done
# data: {}
```
Each token chunk is one `data:` event holding JSON (`{"text":...}`), so a newline inside a token can't break the event. The stream ends with `event: done`, or `event: error` if the model call fails after the stream started. An empty question returns 400.

**Switch provider** with `Chat:Provider` in `src/TaxDoc.Api/appsettings.json` (`Ollama` or `AzureOpenAI`), or for one run with an env var: `Chat__Provider=AzureOpenAI dotnet run --project src/TaxDoc.Api`. The API refuses to start if the chosen provider's settings are missing.

| Provider | Settings |
|---|---|
| `Ollama` (default) | `Chat:Ollama:Endpoint` (`http://localhost:11434`), `Chat:Ollama:Model` (`qwen3.5:9b`). Sent with `num_ctx` 16384 and thinking off. |
| `AzureOpenAI` | `Chat:AzureOpenAI:Endpoint` (`https://<resource>.openai.azure.com/`), `Chat:AzureOpenAI:Deployment` (your deployment name), `Chat:AzureOpenAI:ApiKey` (**secret**, see below). Not tested against a real Azure resource yet (C14). |

The Azure key never goes in `appsettings.json`. Keep it in user secrets (stored outside the repo, loaded in Development):
```bash
dotnet user-secrets set "Chat:AzureOpenAI:ApiKey" "<key>" --project src/TaxDoc.Api
dotnet user-secrets list --project src/TaxDoc.Api
```
or set the env var `Chat__AzureOpenAI__ApiKey`.

Logs: `appsettings.Development.json` sets `Microsoft.Extensions.AI` to `Debug` (a line when each model call starts and ends). Set it to `Trace` to also log the full messages and options sent to the model (contains user data, dev only).
