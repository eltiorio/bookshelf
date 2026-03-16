# Known Bugs

## BookNotFoundException crashes entire Import List Sync

**Affects:** `pennydreadful/bookshelf` (upstream) — not specific to any fork or import list plugin

**Severity:** High — one bad book ID aborts sync for all remaining items

### Problem

`ImportListSyncService.MapBookReport()` does not catch `BookNotFoundException` when looking up a book by ID. If any import list item has a `BookGoodreadsId` that references a book deleted from the metadata server, the exception propagates up and kills the entire `ImportListSync` command. No further items are processed.

### Reproduction

1. Configure any import list that provides `BookGoodreadsId` values (e.g., Hardcover Import List)
2. Have at least one item where the book ID no longer exists on the metadata server
3. Trigger Import List Sync
4. Sync aborts with: `BookNotFoundException: Book with id XXXXX was not found, it may have been removed from metadata server.`

### Root Cause

In `src/NzbDrone.Core/ImportLists/ImportListSyncService.cs`, the edition ID lookup path (line ~178) has a try/catch for `BookNotFoundException`, but the book ID lookup path (line ~197) does not:

```csharp
// Edition path — has try/catch (correct)
if (report.EditionGoodreadsId.IsNotNullOrWhiteSpace() && int.TryParse(...))
{
    try
    {
        var remoteBook = _goodreadsProxy.GetBookInfo(report.EditionGoodreadsId);
        // ...
    }
    catch (BookNotFoundException)
    {
        _logger.Debug($"Nothing found for edition [{report.EditionGoodreadsId}]");
        report.EditionGoodreadsId = null;
    }
}
// Book ID path — NO try/catch (bug)
else if (report.BookGoodreadsId.IsNotNullOrWhiteSpace())
{
    var mappedBook = _bookInfoProxy.GetBookInfo(report.BookGoodreadsId); // throws!
    // ...
}
```

### Fix

Wrap the book ID lookup in the same try/catch pattern as the edition path:

```csharp
else if (report.BookGoodreadsId.IsNotNullOrWhiteSpace())
{
    try
    {
        var mappedBook = _bookInfoProxy.GetBookInfo(report.BookGoodreadsId);
        report.BookGoodreadsId = mappedBook.Item2.ForeignBookId;
        report.Book = mappedBook.Item2.Title;
        report.AuthorGoodreadsId = mappedBook.Item3.First().ForeignAuthorId;
    }
    catch (BookNotFoundException)
    {
        _logger.Debug($"Nothing found for book [{report.BookGoodreadsId}]");
        report.BookGoodreadsId = null;
    }
}
```

### Status

- Fixed in our fork: `eltiorio/bookshelf` branch `feature/calibre-import-list` (commit fde32d9e6)
- TODO: File issue on `pennydreadful/bookshelf`
- TODO: Submit PR to upstream

---

## Any unhandled exception in ProcessListItems kills entire Import List Sync

**Affects:** `pennydreadful/bookshelf` (upstream) — not specific to any fork or import list plugin

**Severity:** High — a single metadata API failure aborts processing for all remaining items

### Problem

`ImportListSyncService.ProcessListItems()` iterates over all import list items sequentially with no per-item error handling. Any exception from metadata service calls (`MapBookReport`, `MapAuthorReport`, `ProcessBookReport`) propagates up and aborts the entire sync. Items after the failure are never processed.

This is a broader version of the `BookNotFoundException` bug above. Even after fixing that specific case, other transient failures crash the sync:

- Hardcover metadata API returning 500 errors (observed: `GoodreadsException: Search for 'X' failed`)
- Malformed JSON responses from metadata API (observed: `JsonException: '0x00' is an invalid start of a value`)
- Any network timeout or transient error during book/author lookups

### Observed Impact

With 369 import list items, the sync repeatedly crashed partway through — first on a `BookNotFoundException`, then on a `JsonException` — adding only a handful of books per attempt instead of processing all 369.

### Root Cause

In `src/NzbDrone.Core/ImportLists/ImportListSyncService.cs`, the `foreach` loop in `ProcessListItems()` (line ~112) has no try/catch around the per-item processing:

```csharp
foreach (var report in items)
{
    // No try/catch — any exception here kills the loop
    var importList = _importListFactory.Get(report.ImportListId);
    MapBookReport(report);        // can throw
    ProcessBookReport(...);       // can throw
}
```

### Fix

Wrap the per-item processing body in a try/catch that logs the error and continues:

```csharp
foreach (var report in items)
{
    try
    {
        var importList = _importListFactory.Get(report.ImportListId);
        // ... mapping and processing ...
    }
    catch (Exception e)
    {
        _logger.Error(e, "Failed to process import list item {0} [{1}] by [{2}], skipping",
            report.BookGoodreadsId, report.Book, report.Author);
    }
}
```

This matches the resilience pattern already used in `FetchAndParseImportListService.Fetch()` where per-list exceptions are caught and logged.

### Status

- Fixed in our fork: `eltiorio/bookshelf` branch `feature/calibre-import-list`
- Subsumes the `BookNotFoundException` fix (per-item catch handles all exception types)
- TODO: File issue on `pennydreadful/bookshelf`
- TODO: Submit PR to upstream

---

## Import List books that already exist in DB lose their monitored state

**Affects:** `pennydreadful/bookshelf` (upstream) — not specific to any fork or import list plugin

**Severity:** High — import list `shouldMonitor: specificBook` is silently ignored for most books

### Problem

When an Import List adds a book that already exists in Bookshelf's database (e.g. the author was added via file scan and their full catalog was pulled from metadata), the book's `Monitored = true` flag set by the import list is overwritten to `false` by `AddBookService.AddBook()`.

This means books from the import list are added but NOT monitored, so they never appear in Wanted/Missing and are never searched for download. Only books whose author was completely new to Bookshelf get correctly monitored.

### Observed Impact

369 empty Calibre entries processed by Import List → only 23 ended up monitored+missing. The ~300 that mapped to authors already in Bookshelf (from file scan) were silently de-monitored.

### Root Cause

`AddBookService.AddBook()` at line 50-54 of `src/NzbDrone.Core/Books/Services/AddBookService.cs`:

```csharp
var dbBook = _bookService.FindById(book.ForeignBookId);
if (dbBook != null)
{
    book.UseDbFieldsFrom(dbBook);  // <-- overwrites Monitored
}
```

`Book.UseDbFieldsFrom()` at line 87-97 of `src/NzbDrone.Core/Books/Model/Book.cs`:

```csharp
public override void UseDbFieldsFrom(Book other)
{
    Id = other.Id;
    AuthorMetadataId = other.AuthorMetadataId;
    Monitored = other.Monitored;  // <-- Monitored from DB (false) overwrites import list's true
    ...
}
```

The book already exists in the DB as unmonitored (part of the author's catalog pulled during metadata refresh). `UseDbFieldsFrom` copies the DB's `Monitored = false` over the import list's `Monitored = true`.

Note: `ImportListSyncService.ProcessListItems()` has a separate "Book Exists in DB" path (line ~261) that correctly handles monitoring for existing books. But that path uses `_bookService.FindById(report.BookGoodreadsId)` where the ID may have been remapped by `MapBookReport`, causing a miss. The book then falls through to the "add new" path in `ProcessBookReport`, which hits the `UseDbFieldsFrom` override.

### Fix (TBD)

Options:
1. In `AddBookService.AddBook()`, preserve the caller's `Monitored` state after `UseDbFieldsFrom`
2. In `ProcessBookReport`, after `_addBookService.AddBooks()`, explicitly set monitoring on the added books
3. In `ProcessListItems`, use the "Book Exists" path more reliably by checking both the original and mapped IDs

### Status

- Partially addressed: `AddBookService` preserves caller's Monitored flag (commit 23a2f9c0b)
- Partially addressed: post-add monitoring loop in `ProcessListItems` (commit 078bfefdc)
- Full fix blocked by metadata polling hang (see below) — AddBooks fails before the post-add loop can run
- TODO: File issue on `pennydreadful/bookshelf`

---

## Metadata API polling loop causes multi-hour hangs during batch operations

**Affects:** `pennydreadful/bookshelf` (upstream) — any operation that adds multiple authors

**Severity:** Critical — import list sync, bulk author adds, and library refreshes can hang for hours

### Problem

`BookInfoProxy.PollAuthorUncached()` and `PollBook()` use a polling loop that retries up to **60 times** with sleeps between each attempt. When processing hundreds of authors in a batch (e.g. import list sync), the cumulative sleep time can reach hours, causing the sync to appear hung.

### Root Cause

In `src/NzbDrone.Core/MetadataSource/BookInfo/BookInfoProxy.cs`:

**`PollAuthorUncached()` (line ~599):**
```csharp
for (var i = 0; i < 60; i++)
{
    var httpResponse = _cachedHttpClient.Get(httpRequest, false, TimeSpan.FromMinutes(30));

    if (httpResponse.HasHttpError)
    {
        if (httpResponse.StatusCode == HttpStatusCode.TooManyRequests)
        {
            WaitUntilRetry(httpResponse);  // Thread.Sleep(Retry-After seconds, default 5s)
            continue;
        }
        // ... other error handling
    }

    resource = JsonSerializer.Deserialize<AuthorResource>(httpResponse.Content);

    if (resource.Works != null)
    {
        break;  // success
    }

    Thread.Sleep(2000);  // works not ready yet, poll again
}
```

Three compounding factors:

1. **Polling sleep:** Even on success, if `Works` is null (metadata server still processing), it sleeps 2s and retries. 60 iterations x 2s = **120s per author**.

2. **Rate limit sleep:** On 429, `WaitUntilRetry()` calls `Thread.Sleep(Retry-After)` (default 5s, but server can set any value). 60 retries x 5s = **300s per author**. If `Retry-After` is longer (e.g. 60s), it's 60 x 60s = **3600s (1 hour) per author**.

3. **No overall timeout:** The loop has no total elapsed time check. It will always run up to 60 iterations regardless of how long each iteration takes.

**`PollBook()` (line ~655):** Same pattern, same problem.

### Observed Impact

Import list sync with 369 items → ~170 new authors to add. Each author triggers `PollAuthorUncached`. With rate limiting from the Hardcover metadata API:

- Last observed hang: 7+ hours on a single sync, stuck on `GetAuthorDetails`
- Previous hang: sync stuck for 15+ minutes on `Processing list item 369/369`
- Pattern: sync processes items quickly, then hangs indefinitely during `AddAuthors` phase

### Fix Options

**Option A: Add overall timeout to polling loop**
Replace the fixed 60-iteration loop with a time-bounded loop (e.g. 60s total per author). If the metadata server can't provide Works within 60s, skip the author.

```csharp
var deadline = DateTime.UtcNow.AddSeconds(60);
while (DateTime.UtcNow < deadline)
{
    // ... existing poll logic ...
}
```

**Option B: Reduce retry count and sleep duration**
Lower from 60 to 10 iterations, cap `Retry-After` sleep at 10s. Reduces worst case from hours to minutes.

**Option C: Async polling with cancellation**
Replace `Thread.Sleep` with `Task.Delay(timeout, cancellationToken)` so the operation can be cancelled by the caller. Requires making `PollAuthorUncached` async.

**Recommendation:** Option A is the simplest and most effective. It bounds the worst case regardless of server behavior. Options B and C are complementary improvements.

### Status

- Fixed in our fork: `eltiorio/bookshelf` branch `feature/calibre-import-list`
  - `PollAuthorUncached`: replaced `for (i < 60)` with `while (UtcNow < deadline)` (60s)
  - `PollBook`: same change
  - `WaitUntilRetry`: capped `Retry-After` at 30s via `Math.Min(seconds, 30)`
- TODO: File issue on `pennydreadful/bookshelf`
- TODO: Submit PR to upstream

---

## Duplicate AuthorMetadataId crashes AddAuthors batch insert

**Affects:** `pennydreadful/bookshelf` (upstream) — any batch author add (import lists, bulk operations)

**Severity:** High — crashes the entire import list sync after spending minutes on metadata lookups

### Problem

`AddAuthorService.AddAuthors()` batch-inserts authors into the DB. Multiple import list items can resolve to the same author after metadata lookup (e.g. different books by the same author, or different Hardcover author IDs mapping to the same canonical author). The batch insert hits a `UNIQUE constraint failed: Authors.AuthorMetadataId` and crashes.

### Observed Error

```
System.Data.SQLite.SQLiteException: constraint failed
UNIQUE constraint failed: Authors.AuthorMetadataId
   at AddAuthorService.AddAuthors()
```

### Root Cause

In `src/NzbDrone.Core/Books/Services/AddAuthorService.cs` line ~89:

```csharp
// add metadata — UpsertMany handles duplicates correctly
_authorMetadataService.UpsertMany(authorsToAdd.Select(x => x.Metadata.Value).ToList());
authorsToAdd.ForEach(x => x.AuthorMetadataId = x.Metadata.Value.Id);

// batch insert — does NOT handle duplicates, crashes on UNIQUE constraint
return _authorService.AddAuthors(authorsToAdd, doRefresh);
```

`UpsertMany` handles duplicate metadata correctly (upsert). But `AddAuthors` does a plain INSERT, so two authors with the same `AuthorMetadataId` cause a constraint violation.

`ImportListSyncService.ProcessAuthorReport()` tries to prevent duplicates via `authorsToAdd.Find(i => i.ForeignAuthorId == report.AuthorGoodreadsId)`, but different `ForeignAuthorId` values can resolve to the same canonical author after `AddSkyhookData`.

### Fix

After the metadata upsert (which assigns IDs), before the author insert:
1. Filter out authors whose `AuthorMetadataId` already exists in the DB (from previous syncs)
2. Deduplicate remaining by `AuthorMetadataId` (within-batch duplicates)

### Status

- Fixed in our fork: `eltiorio/bookshelf` branch `feature/calibre-import-list`
- TODO: File issue on `pennydreadful/bookshelf`
- TODO: Submit PR to upstream

---

## OPEN: Many authors fail to import during Import List Sync

**Status:** Under investigation — root cause NOT confirmed

### Observed Behavior

During import list sync, many `AddAuthorService` calls fail with:
```
ReadarrId XXXXXXX was not found, it may have been removed from Goodreads.
Failed to import id: XXXXXXX - [Author Name]
```

These are known authors (e.g. Bill Bryson, Isaac Asimov, Agatha Christie) whose books resolve successfully via `GetBookInfo()` — the book endpoint returns an author ID, but then `GetAuthorInfo()` with that same author ID fails.

### Example

- Calibre entry: "The Body" by Bill Bryson, `hardcover-id: 427677`
- `GetBookInfo("427677")` succeeds, returns `AuthorGoodreadsId = "3050980"`
- `GetAuthorInfo("3050980")` fails with `AuthorNotFoundException`

### Investigation Findings

**The failing author IDs don't match what Bookshelf has in its DB:**

| Author | ID from GetBookInfo (fails) | ID in Bookshelf DB (works) |
|--------|---------------------------|---------------------------|
| Bill Bryson | 3050980 | 60210 |
| Alton Brown | 41121 | 238429 |
| Isaac Asimov | 5763329 | 224110 |

**The `hardcover-id` in Calibre is a Hardcover native `book_id`** (from the GraphQL API, written by Calibre-Hardcover Sync plugin). Bookshelf's metadata proxy (`hardcover.bookinfo.pro`) may use a different internal ID space.

**Book lookup via Bookshelf API also fails:** `author/lookup?term=readarr:3050980` returns empty. But `author/lookup?term=Bill+Bryson` returns the author with `foreignAuthorId=60210`.

**The Hardcover Import List uses the same Hardcover native IDs** (`HardcoverImportParser.cs` gets `id` from GraphQL response). If those IDs don't work with `bookinfo.pro`, the Hardcover Import List would have the same problem — but it may not have been tested at this scale.

### Remaining Questions

- [ ] Does `hardcover.bookinfo.pro` use Hardcover native IDs or its own internal IDs?
- [ ] When the Hardcover Import List adds an author successfully, what ID does it use?
- [ ] Is the `GetBookInfo("427677")` call actually succeeding (returning author ID 3050980), or is our code falling through to the fuzzy search path which assigns a different ID?
- [ ] Add debug logging to trace the full ID flow: Calibre hardcover-id → MapBookReport → GetBookInfo → returned AuthorGoodreadsId → AddAuthorService → GetAuthorInfo
