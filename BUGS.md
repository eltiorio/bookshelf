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
