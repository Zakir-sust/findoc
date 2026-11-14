# FinDoc Assistant — RAG over public company annual reports (10-K) in .NET (Stage C)

Ask questions about four companies' latest annual reports (Apple, Alphabet, Amazon, Tesla) and get answers that cite
the report and the 10-K item they came from, e.g. `[apple-fy2026, Item 7]`. Anyone can check an answer: open the PDF at
the cited page and read the number.

Status: ingestion works end to end (upload → RabbitMQ → worker → PdfPig text → 10-K item chunks → embeddings in
pgvector), retrieval with a report filter works, and `/chat` streams a cited answer from the top 5 chunks (C13).

## Layout
| Path | What |
|---|---|
| `src/FinDoc.Api` | Minimal API: `POST /documents` (multipart PDF upload), `GET /documents/{id}`, `POST /chat` (SSE stream). Applies EF migrations on start. |
| `src/FinDoc.Ingestion` | Worker: consumes RabbitMQ `ingest`, extracts text with PdfPig, chunks by 10-K item, embeds in batches of 32, saves to Postgres, acks after the commit. |
| `src/FinDoc.Core` | EF Core model (`Document`, `Chunk` with `vector(1024)` + HNSW cosine index), `ItemChunker`, `Retriever` (pgvector top-k + report filter), messaging contract. |
| `tests/FinDoc.Tests` | xUnit tests. `Category=Integration` ones need the local stack + Ollama. |
| `evals/` | `golden.draft.json`: 30 questions with the PDF page of each answer, **draft until checked**. |
| `data/` | `metrics.draft.json`: 4 companies × revenue, net income, R&D, employees, with pages, **draft until checked**. |
| `scripts/download-sources.sh` | Downloads the four 10-K PDFs from the companies' investor-relations sites into `sources/` (gitignored; PDFs are never committed). |
| `docs/learned.md` | Learn-backs. |

Target framework: `net9.0` (set once in `Directory.Build.props`).

## Run it
Prereqs: .NET 9 SDK, Docker, Ollama on the host (`qwen3.5:9b` for chat, `qwen3-embedding:0.6b` for embeddings).

```bash
docker compose up -d                          # Postgres+pgvector :5432, RabbitMQ :5672 (UI :15672, guest/guest)
./scripts/download-sources.sh                 # 4 annual reports -> sources/
dotnet run --project src/FinDoc.Api           # http://localhost:5080 (terminal 1)
dotnet run --project src/FinDoc.Ingestion     # worker (terminal 2)

for f in sources/*.pdf; do curl -s -F "file=@$f" http://localhost:5080/documents; echo; done
# worker log: "Embedded 164 chunks of apple-fy2026 in 25.9 s"
curl http://localhost:5080/documents/<id>     # status: Ingested, pageCount, textLength
```
Expected after ingestion: 610 chunks (Apple 164, Alphabet 133, Amazon 130, Tesla 183), every one embedded, all 23 items found in each report.

Tests: `dotnet build && dotnet test --filter "Category!=Integration"` (unit), `dotnet test --filter Category=Integration` (needs the stack, Ollama and the 4 reports ingested).

Uploaded files are stored in `storage/uploads/` (gitignored).

## Chat (`POST /chat`, blocks C10 + C13)
Answers from the reports, streamed as Server-Sent Events: the question is embedded (same model as the chunks) → the top 5
chunks from pgvector, optionally from one report → the C12 citation prompt → the model's tokens.

```bash
curl -N -X POST http://localhost:5080/chat -H 'Content-Type: application/json' \
  -d '{"question":"How many shares did Tesla grant in its 2025 CEO Performance Award?","report":"tesla-fy2025"}'
# data: {"text":"Approximately"}
# ...
# event: sources
# data: [{"label":"sources-[tesla-fy2025]-[Item 8]","item":"Item 8","text":"..."}]
# event: done
# data: {}
```
- Each token chunk is one `data:` event holding JSON (`{"text":...}`), so a newline inside a token can't break the event.
- `event: sources` comes after the tokens: every chunk whose label the answer cited (two chunks of one item share a label), with its text. A citation that matches no source sent is dropped and logged as a warning. A refusal (`Not in Source`) cites nothing: `[]`.
- No matching chunks (e.g. an unknown `report`) still goes to the model, which answers `Not in Source`.
- The stream ends with `event: done`, or `event: error` if the search or the model call fails after the stream opened. An empty question returns 400.

**Switch provider** with `Chat:Provider` in `src/FinDoc.Api/appsettings.json` (`Ollama` or `AzureOpenAI`), or for one run with an env var: `Chat__Provider=AzureOpenAI dotnet run --project src/FinDoc.Api`. The API refuses to start if the chosen provider's settings are missing.

| Provider | Settings |
|---|---|
| `Ollama` (default) | `Chat:Ollama:Endpoint` (`http://localhost:11434`), `Chat:Ollama:Model` (`qwen3.5:9b`). Sent with `num_ctx` 16384 and thinking off. |
| `AzureOpenAI` | `Chat:AzureOpenAI:Endpoint` (`https://<resource>.openai.azure.com/`), `Chat:AzureOpenAI:Deployment` (your deployment name), `Chat:AzureOpenAI:ApiKey` (**secret**, see below). Not tested against a real Azure resource yet (C14). |

The Azure key never goes in `appsettings.json`. Keep it in user secrets (stored outside the repo, loaded in Development):
```bash
dotnet user-secrets set "Chat:AzureOpenAI:ApiKey" "<key>" --project src/FinDoc.Api
dotnet user-secrets list --project src/FinDoc.Api
```
or set the env var `Chat__AzureOpenAI__ApiKey`.

Logs: `appsettings.Development.json` sets `Microsoft.Extensions.AI` to `Debug` (a line when each model call starts and ends). Set it to `Trace` to also log the full messages and options sent to the model (contains user data, dev only).
