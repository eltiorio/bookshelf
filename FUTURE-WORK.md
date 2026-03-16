# Future Work

Follow-up improvements and investigations for the Calibre Import List and related upstream fixes.

## Restore Edition Preference from Calibre

**Priority:** Medium — functional workaround in place, but user intent is lost

### Current State

The Calibre-Hardcover Sync plugin stores the user's selected Hardcover edition as `hardcover-edition` in Calibre identifiers. This captures which specific edition the user wants — e.g., trade paperback vs Kindle vs audiobook.

Currently, the Calibre Import List does NOT pass this edition to Bookshelf. Instead, `MapBookReport` extracts the first edition from the rreading-glasses metadata response (via `_bookInfoProxy.GetBookInfo`). This is whatever rreading-glasses considers the "best" edition (cover → ebook → audio → physical), not the user's choice.

### Why We Don't Pass It Today

We originally passed `hardcover-edition` as `EditionGoodreadsId`. This caused a cascading failure:

1. `MapBookReport` has an `if/else if` structure: edition path vs book ID path
2. The edition path uses `_goodreadsProxy.GetBookInfo()` which hits the `/api/book/basic_book_data/{editionId}` endpoint
3. This endpoint does NOT recognize Hardcover edition IDs → `BookNotFoundException`
4. The catch sets `EditionGoodreadsId = null` and returns
5. Because it's `if/else if`, the book ID path **never runs** → `AuthorGoodreadsId` never gets set
6. Downstream: author processing skipped, book added with no author, `AddBookService` crashes

Removing `EditionGoodreadsId` entirely was the fix — lets the book ID path run correctly. The "first edition from metadata" workaround ensures `AddBookService` has an edition to work with.

### Investigation: Alternative Approaches

**Option A: Use `/book/{editionId}` endpoint instead of `basic_book_data`**

rreading-glasses has a `/book/{editionId}` endpoint that DOES accept Hardcover edition IDs (confirmed in `internal/hardcover.go` line 205: `GetEdition` query). This endpoint redirects to the work/author. If we route the edition lookup through `_bookInfoProxy` (which uses the correct base URL) instead of `_goodreadsProxy` (which uses the legacy `basic_book_data` path), the edition should resolve correctly.

Investigation needed:
- [ ] Does `BookInfoProxy` already have a method for edition lookup by ID?
- [ ] Can we add one that calls `/book/{editionId}` and follows the redirect?
- [ ] Does the redirect response include both the work ID and edition details?

**Option B: Fix the `if/else if` to fall through on edition failure**

Change `MapBookReport` so that when the edition lookup fails, it falls through to the book ID path instead of returning. This would let both IDs be attempted:

```csharp
// Instead of if/else if:
if (report.EditionGoodreadsId.IsNotNullOrWhiteSpace() && ...)
{
    // try edition path...
    // on failure: EditionGoodreadsId = null, DON'T return
}
if (report.BookGoodreadsId.IsNotNullOrWhiteSpace() &&
    report.AuthorGoodreadsId.IsNullOrWhiteSpace())
{
    // try book ID path...
}
```

This is a more invasive change to upstream code but fixes the structural issue for all import lists.

**Option C: Pass edition in `CalibreImport.Fetch()` but use `_bookInfoProxy` path**

Set `EditionGoodreadsId` from Calibre AND ensure it goes through the correct API endpoint. This might require adding a new code path in `MapBookReport` that uses `_bookInfoProxy` for edition lookups instead of `_goodreadsProxy`.

**Option D: Set edition post-add**

After the book is added via `AddBookService`, look up the Calibre edition ID and switch the monitored edition. This avoids changing `MapBookReport` entirely but requires a post-processing step.

### Recommendation

Option A is cleanest — it uses the existing rreading-glasses endpoint that already works with Hardcover edition IDs. It doesn't require changing `MapBookReport`'s structure. Investigate whether `BookInfoProxy` can be extended with an edition lookup method.

Option B is the best upstream fix — the `if/else if` structure is a latent bug that affects any import list where the edition lookup fails. Worth submitting as a PR to `pennydreadful/bookshelf` independently.

---

## Upstream PRs to Submit

The following fixes are in our fork and should be submitted as PRs to `pennydreadful/bookshelf`:

### 1. Per-item error handling in ProcessListItems
**File:** `ImportListSyncService.cs`
**Impact:** Any import list sync crashing on a single bad item kills the entire batch.
**Fix:** try/catch per item in the foreach loop.

### 2. BookNotFoundException in MapBookReport book ID path
**File:** `ImportListSyncService.cs`
**Impact:** Missing book crashes sync. Edition path already has this catch, book ID path didn't.
**Fix:** Add matching try/catch.

### 3. Time-bound metadata polling loops
**File:** `BookInfoProxy.cs`
**Impact:** `PollAuthorUncached`/`PollBook` can loop for hours with rate limiting.
**Fix:** Replace iteration count with 60s deadline. Cap `Retry-After` at 30s.

### 4. Preserve monitored state in AddBookService
**File:** `AddBookService.cs`
**Impact:** `UseDbFieldsFrom` overwrites caller's `Monitored = true` with DB's `Monitored = false`.
**Fix:** Preserve caller's monitored flag after `UseDbFieldsFrom`.

### 5. Post-add monitoring for import list books
**File:** `ImportListSyncService.cs`
**Impact:** `AddAuthorService` pulls full catalog with default monitoring, overriding import list intent.
**Fix:** Post-add loop explicitly monitors import list books per `shouldMonitor` setting.

### 6. Deduplicate authors before batch insert
**File:** `AddAuthorService.cs`
**Impact:** Duplicate `AuthorMetadataId` crashes batch insert with UNIQUE constraint.
**Fix:** Filter existing DB authors + deduplicate within batch before `InsertMany`.

### 7. Set EditionGoodreadsId from book metadata in MapBookReport
**File:** `ImportListSyncService.cs`
**Impact:** Book ID path doesn't set edition → `AddBookService.AddSkyhookData` crashes on empty editions.
**Fix:** Extract first edition from `GetBookInfo` response.

---

## Open Investigation: Author Import Failures

See `BUGS.md` section "OPEN: Many authors fail to import during Import List Sync".

Initial hypothesis was ID mismatch between Hardcover native IDs and bookinfo.pro IDs. **This was disproven** — rreading-glasses uses Hardcover native IDs directly (confirmed by reading the source).

The actual root cause was the `if/else if` code path in `MapBookReport` — passing `EditionGoodreadsId` blocked the book ID path from running, leaving `AuthorGoodreadsId` unset. After removing the edition pass-through, author imports work correctly.

Some authors still fail with `AuthorNotFoundException` — these may be genuinely missing from rreading-glasses' cache (it fetches on-demand from Hardcover and may timeout), or the Hardcover author record may have been merged/deleted. These failures are now handled gracefully (per-item error handling) and don't crash the sync.

---

## Library Field UX Improvement

The Calibre Import List settings require typing the library name (e.g., "Books") manually. The Calibre AJAX API has a `/ajax/library-info` endpoint that returns `{"default_library": "Books"}`. A small improvement: make the library field optional — if blank, fetch `/ajax/library-info` and use `default_library`.
