# Calibre Import List

A Bookshelf Import List plugin that discovers empty Calibre entries (metadata-only, no files) and imports them into Bookshelf as "Missing" books — searchable and downloadable.

**Branch:** `feature/calibre-import-list` on [eltiorio/bookshelf](https://github.com/eltiorio/bookshelf/tree/feature/calibre-import-list)

## Problem

Calibre supports empty library entries — books with metadata but no files, created via `calibredb add --empty`. These serve as wishlists or placeholders (e.g., synced from Hardcover via the [Calibre-Hardcover Sync](https://git.tpg.la/eltiorio/Calibre-Hardcover-sync) plugin).

Bookshelf's file scan pipeline requires real files at every station. It cannot discover empty entries because the pipeline starts with file detection and every downstream service assumes a `BookFile` record exists. These books are invisible to Bookshelf.

## Solution

The Calibre Import List bypasses the file scan pipeline entirely. It queries Calibre's Content Server AJAX API for entries with no formats, then feeds them through the standard **Import List** pathway — the same mechanism used by Hardcover and Goodreads import lists. This creates Author/Book/Edition records with full metadata but **no BookFile**, so books appear as "Missing" in Bookshelf, ready for search and download.

## How It Works

```
1. Import List Sync triggers (every 12h or manual)
       ↓
2. Fetch() calls Calibre AJAX API:
   - GET /ajax/category/616c6c626f6f6b73/{library}  → all book IDs (paginated)
   - GET /ajax/books/{library}?ids=1,2,3,...         → book details (batches of 750)
       ↓
3. Filter: keep only entries where Formats is empty
   - Skip entries with no author (can't match in Bookshelf)
   - Extract: Author, Title, GoodreadsId (if present in Calibre identifiers)
       ↓
4. ImportListSyncService matches each entry:
   - Searches Hardcover/Goodreads by title+author
   - Creates Author via AddAuthorService (full metadata catalog)
   - Creates Book/Edition via AddBookService
       ↓
5. Result: books appear in Bookshelf as "Missing" (no BookFile)
   - Searchable and downloadable through Bookshelf's normal search/grab flow
```

## Architecture Decision

**Why Import List instead of modifying the file pipeline?**

The first approach (Approach 1) attempted to make the file scan pipeline handle empty entries by modifying `DiskScanService`, `CalibreProxy`, and related files. This hit 5+ crash points where the pipeline assumes real files exist — null `BookFile` references, missing file paths in download verification, calibre integration calls expecting physical files. Each fix exposed another assumption deeper in the stack.

The Import List pathway was designed from the start to create books from external metadata without files. Working WITH the architecture instead of against it required only 2 new files (219 lines) vs. patching 3+ files with fragile workarounds.

## Calibre AJAX API

The plugin uses two endpoints on Calibre's Content Server:

| Endpoint | Purpose | Pagination |
|----------|---------|------------|
| `GET /ajax/category/616c6c626f6f6b73/{library}?num=750&offset=0` | List all book IDs | Yes — repeat with increasing offset until empty |
| `GET /ajax/books/{library}?ids=1,2,3` | Book details (title, authors, formats, identifiers) | Batch by IDs (750 per request) |

The hex string `616c6c626f6f6b73` is the Calibre internal category for "allbooks".

**Authentication:** HTTP Basic Auth via `NetworkCredential` if username is configured.

## Configuration

In Bookshelf UI: **Settings → Import Lists → Add → Calibre**

| Field | Default | Description |
|-------|---------|-------------|
| Calibre Host | `localhost` | Hostname or IP of Calibre Content Server |
| Port | `8081` | Content Server port |
| URL Base | *(empty)* | Optional path prefix (advanced) |
| Username | *(empty)* | Content Server auth username |
| Password | *(empty)* | Content Server auth password |
| Library | `Calibre_Library` | Calibre library name |
| Use SSL | `false` | Connect over HTTPS (advanced) |

- **ListType:** Program
- **MinRefreshInterval:** 12 hours
- Manual sync: **Settings → Import Lists → Test** (connection test) or let the scheduled sync run

## Build

From the bookshelf repo root (requires Docker):

```bash
docker build -f Dockerfile.patch -t bookshelf:patched .
```

This is a multi-stage build: .NET SDK 6 (build) → Node 20 (frontend) → Alpine runtime. Output is ~188MB.

## Deploy

In your `docker-compose.yml`, replace the upstream image:

```yaml
# Before
image: ghcr.io/pennydreadful/bookshelf:hardcover

# After
image: bookshelf:patched
```

Then configure the import list in the Bookshelf UI as described above.

## Testing Results

Tested against the [books-testing stack](https://github.com/eltiorio/homelab) on wideserv (192.168.2.50):

- 10 empty Calibre entries (created via `calibredb add --empty` with Hardcover identifiers)
- Import List sync discovered all 10
- Bookshelf created 10 new authors with full Hardcover metadata catalogs
- 0 `BookFile` records — all books show as "Missing"
- Books are searchable and grabbable through Bookshelf's normal UI

## Files Changed

```
src/NzbDrone.Core/ImportLists/Calibre/CalibreImport.cs      — 166 lines (Import List implementation)
src/NzbDrone.Core/ImportLists/Calibre/CalibreImportSettings.cs — 53 lines (Settings/validation)
Dockerfile.patch                                              — Multi-stage build file
```

The implementation reuses existing models (`CalibreBook`, `CalibreCategory`) from `NzbDrone.Core.Books.Calibre`.

## Dev Notes

- **StyleCop SA1203:** Const fields must appear before non-const fields in C# classes. The `PAGE_SIZE` const is declared at the top of `CalibreImport` for this reason.
- The `616c6c626f6f6b73` hex category was found by inspecting Calibre Content Server's AJAX responses — it's not documented.
- `CleanupListItems()` is called on the result to deduplicate and normalize before handoff to `ImportListSyncService`.

## Related Projects

- **[Calibre-Hardcover Sync](https://git.tpg.la/eltiorio/Calibre-Hardcover-sync)** — Calibre desktop plugin that syncs reading statuses, lists, and ratings with Hardcover. Creates the empty Calibre entries that this Import List discovers.
- **[homelab/stacks/books-testing](https://github.com/eltiorio/homelab)** — Dev/test stack on wideserv with Calibre, CWA, and Bookshelf. Used for developing and testing both projects.
