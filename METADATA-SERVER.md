# Metadata Server: rreading-glasses / bookinfo.pro

Bookshelf (and Readarr) rely on an external metadata server for book/author/edition data. This document covers the architecture, ID systems, and how it connects to Hardcover.

## What is it?

The metadata server is **[rreading-glasses](https://github.com/blampe/rreading-glasses)** by blampe — an open source (Go) replacement for Readarr's defunct metadata service.

**Hosted instances** (operated by blampe):
- `https://api.bookinfo.pro` — Goodreads metadata (~12k daily users as of Jan 2026)
- `https://hardcover.bookinfo.pro` — Hardcover metadata (used by Bookshelf Hardcover fork)

**Self-hostable**: Docker image `blampe/rreading-glasses:hardcover`, requires Postgres backend (used as key-value store).

## ID System

**rreading-glasses uses Hardcover native IDs directly.** No mapping, no internal ID space.

From the source (`internal/hardcover.go`):
```go
ForeignID: work.Id        // Hardcover book/work ID
ForeignID: author.Id      // Hardcover author ID
ForeignID: edition.Id     // Hardcover edition ID
```

These are the same IDs that:
- The Hardcover GraphQL API returns
- The Calibre-Hardcover Sync plugin stores as `hardcover-id` (book), `hardcover-edition` (edition)
- The Hardcover Import List parser extracts from GraphQL responses
- Bookshelf stores as `ForeignBookId`, `ForeignAuthorId`, `ForeignEditionId`

## API Endpoints

Bookshelf talks to rreading-glasses via these endpoints (see `BookInfoProxy.cs`):

| Endpoint | Purpose | Called by |
|----------|---------|----------|
| `GET /author/{authorId}` | Author metadata + all works | `PollAuthorUncached()` |
| `GET /work/{bookId}` | Book/work metadata + editions | `PollBook()` |
| `GET /book/{editionId}` | Edition lookup (redirects to work/author) | `SearchByGoodreadsBookId()` |
| `POST /book/bulk` | Bulk book lookup by IDs | `MapSearchResult()` |
| `GET /author/changed?since=` | Changed authors since timestamp | `RefreshAuthorService` |
| `GET /api/book/basic_book_data/{editionId}` | Legacy Goodreads-style edition lookup | `GoodreadsProxy.GetBookInfo()` |

**Polling behavior**: Author and work endpoints may return incomplete data (e.g. `Works: null`) while rreading-glasses fetches from Hardcover in the background. Bookshelf polls with 2s sleeps until data is populated (capped at 60s by our fix).

**Rate limiting**: Returns `429 Too Many Requests` with `Retry-After` header.

## Edition Handling

rreading-glasses deliberately limits editions:

> "Books are no longer returned with every edition ever released, because that makes manual edition selection difficult to impossible. Instead, at most 20 of the top editions are included and de-duplicated by language and title."

This explains why Bookshelf shows fewer editions (e.g. 12) than the Hardcover website or Calibre-Hardcover Sync plugin (e.g. 41) — the plugin queries the Hardcover GraphQL API directly, while Bookshelf goes through rreading-glasses which caps and deduplicates.

## Configuration

Bookshelf determines the metadata URL via (`ConfigService.cs`):
1. Database setting `MetadataSource`
2. Environment variable `METADATA_URL`
3. Default: `https://api.bookinfo.pro`

Our `Dockerfile.patch` sets `METADATA_URL=https://hardcover.bookinfo.pro`.

## Relevant Source Files

- **rreading-glasses repo**: https://github.com/blampe/rreading-glasses
  - `internal/hardcover.go` — Hardcover GraphQL integration, ID mapping
  - `internal/resources.go` — API response types (WorkResource, AuthorResource, etc.)
  - `hardcover/queries.graphql` — GraphQL queries sent to Hardcover API
- **Bookshelf**:
  - `src/NzbDrone.Core/MetadataSource/BookInfo/BookInfoProxy.cs` — main client
  - `src/NzbDrone.Core/MetadataSource/Goodreads/GoodreadsProxy.cs` — legacy client (uses `/api/book/basic_book_data/`)
  - `src/NzbDrone.Core/Configuration/ConfigService.cs` — `MetadataSource` config
