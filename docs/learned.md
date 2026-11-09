# Learned

Learn-backs: one entry per agent-built piece I reviewed and learned back (what it does, why it's built that way, what I'd change).

<!-- Template:
## YYYY-MM-DD · <block id> · <topic>
- What it does:
- Why this way:
- What I'd change / questions:
-->

## 2026-11-08 · C07 · TaxDoc ingestion scaffold
_From my answers in the review chat (8–9 Nov); written up by Claude._
- What it does: `DocumentEndpoints.UploadAsync` saves the PDF, commits a row with status `Uploaded`, then `RabbitMqIngestPublisher.PublishAsync` sends only the `documentId` (claim-check: the file stays on disk, the row is the source of truth). The worker's `ReceivedAsync` handler (not `ExecuteAsync`, which only sets up the consumer) calls `DocumentIngestor.IngestAsync`, which extracts the text with PdfPig.
- Why ack after the commit: dies before `SaveChangesAsync` → the broker redelivers the message (unacked messages go back when the connection closes). Dies after the commit but before the ack → redelivered, but the status guard at the top skips it. At-least-once + an idempotent consumer. Weak spot: two workers could both pass the status check; fix = claim the row atomically (`UPDATE … WHERE Status = 'Uploaded'`).
- `<=>` returns 1 − cosine similarity, so smaller is better. HNSW trades a little accuracy for a lot of speed, and it's used only when the query orders by `<=>` (`vector_cosine_ops`).
- My change: reject uploads whose first 5 bytes aren't `%PDF-`. Two bugs fixed by Claude: `new Memory<byte>()` is an empty buffer (needs `new byte[5]`), and `!=` on spans compares where the memory is, not its contents (use `SequenceEqual`).
