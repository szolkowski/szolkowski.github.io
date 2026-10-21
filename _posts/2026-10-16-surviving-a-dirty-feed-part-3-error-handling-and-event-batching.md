---
layout: post
title:  "Surviving a Dirty Feed. Part 3: Error Handling and Event Batching"
description: "Committing a 250,000-row Optimizely catalog import row by row, returning a per-row failure report, and coalescing catalog events into one per batch."
date:   2026-10-16 10:00:00 +0200
# Pinned so dateModified cannot precede datePublished on a scheduled post.
last_modified_at: 2026-10-16 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
redirect_from:
  - /2026/10/16/Surviving-a-Dirty-Feed-Part-3-Error-Handling-and-Event-Batching.html
  - /2026/10/16/surviving-a-dirty-feed-part-3-error-handling-and-event-batching.html
image:
   path: assets/img/2026-10-16-surviving-a-dirty-feed-part-3-error-handling-and-event-batching.png
   alt: "Surviving a Dirty Feed. Part 3: Error Handling and Event Batching"
   width: 1200
   height: 630
primary_tag: commerce
tags:
- episerver
- optimizely
- commerce
- catalog
- import
- error handling
- .NET
---

In [Part 2]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %}) we built a writer that upserts one catalog entry through the DTO API. In this part I will cover the other requirement from [Part 1]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %}): commit record by record, and give back a report of every row that failed and why.

That requirement turned out to shape the design more than speed did.

## How should errors be handled?

The main requirement is simple: one bad row can't stop the whole import.

In my implementation, an expected failure is returned as an outcome, and an unexpected failure is an exception. Neither of them is allowed to end the run on its own. The circuit breaker below is the one exception, and it still ends with a report.

What does "record by record" mean in practice?

- There is no transaction for the whole run, and there shouldn't be one for a batch either. Each row is committed on its own, so one bad row won't roll back the other 499 rows in its 500-row batch. The only all-or-nothing scope in the whole design is the create path from [Part 2]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %}), and it covers a single entry.
- A partial run is still a successful run. The report is what we are after here! Whoever reads it (an API caller getting the rejections in the response body, or an editor reading the job summary) needs the counts and the failed rows, and an exception gives them neither.

That gives us three layers. A row can fail at any of them, and it comes out looking the same to whoever reads the report.

Here are the types that carry the report:

```csharp
public sealed record ParsedRow<T>(int RowNumber, T? Record, string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record RejectedRecord(string Identifier, int RowNumber, string Error);

public sealed class ImportSection
{
    // The count is always exact; only the detail list is capped, so a fully-rejecting
    // 250,000-row feed cannot retain a quarter of a million objects for the whole run.
    public const int MaxRetainedRejections = 1_000;

    private readonly List<RejectedRecord> _rejections = new();

    public int Received { get; private set; }
    public int Accepted { get; private set; }
    public int Rejected { get; private set; }
    public IReadOnlyList<RejectedRecord> Rejections => _rejections;

    /// <summary>True when rejections were dropped from the list but still counted.</summary>
    public bool RejectionsTruncated => Rejected > MaxRetainedRejections;

    public void Receive() => Received++;

    public void Accept() => Accepted++;

    public void Reject(string identifier, int rowNumber, string error)
    {
        Rejected++;

        if (_rejections.Count < MaxRetainedRejections)
        {
            _rejections.Add(new RejectedRecord(identifier, rowNumber, error));
        }
    }
}

public sealed class ImportResult
{
    public ImportSection Products { get; } = new();
    public TimeSpan Duration { get; set; }
    public bool Stopped { get; set; }

    // Giving up early is an outcome too - recorded and explained, never thrown, because
    // throwing would destroy the report that is the whole point of the run.
    public bool Aborted { get; set; }
    public string? AbortReason { get; set; }
}

// The job's side of the conversation. Status drives the progress line in the
// scheduled-jobs UI; Rejected and Info become log entries.
public interface IImportObserver
{
    bool StopRequested { get; }

    void Status(string message);

    void Rejected(string message);

    void Info(string message);
}
```

`ParsedRow<T>` is layer one. A CSV reader configured with `BadDataFound` set to record-and-continue never throws for a bad *row*. Instead, it produces a row-shaped error carrying the physical line number.

Layer two is the writer returning `WriteOutcome.Failure(...)` for anything it can anticipate.

Layer three is one `try`/`catch` per record for everything else.

The counts on `ImportSection` are always exact, but the detail list is capped. It keeps the first 1,000 rejections (`MaxRetainedRejections`), so a fully rejected 250,000-row feed doesn't hold a quarter of a million objects for the whole run.

The job log is capped even lower. The importer lists only the first 200 rejections one by one (`MaxIndividuallyReportedRejections`), and then prints the exact total in the summary. So "every row that failed" means every row is counted, but only the first 1,000 come with details.

Here is the full importer:

{% include code-modal.html
   id="2026-10-16-CatalogBulkImporter"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-16-CatalogBulkImporter.cs"
%}

Key points:

- Carry the row number through, since the writer knows SKUs and the reader knows lines
- Count and report a rejection in the same place, never separately
- The natural key does the idempotency work, so replay is safe
- Stop is cooperative and checked between batches
- `Stopped` and `Aborted` are different outcomes and both need rendering
- The circuit breaker is a recorded outcome, so it doesn't throw

### Row numbers

The writer only knows SKUs, and the person fixing the feed only knows line numbers.

If we keep `RowNumber` on the rejection, people act on the report. Without it, the report gets ignored.

### Counting and reporting

A single `Reject` helper means a rejection can't be counted without being reported, or reported without being counted.

When those two drift apart, we get an annoying bug to chase, and I would prefer not to chase it twice.

### Idempotency

The writer upserts on SKU and guards every field, so re-running the same feed in effect changes nothing.

There is no run id and no "have I imported this file already?" check. I don't think we need one, as long as the key is the feed's own and every write is guarded.

### Stop and abort

Stop is cooperative and is checked between batches. Rows already written stay written.

A stopped import is a partial success, and the report should say exactly that, so it doesn't look like a failure.

`Stopped` and `Aborted` are separate flags. A human pressing Stop sets the first one, and the circuit breaker sets the second. Anything that shows "was this a partial run?" needs to check both.

### The circuit breaker

A hundred consecutive rows that *throw* mean a dropped connection or a full disk. Rejecting the remaining 249,000 rows one at a time would take hours and produce a report nobody can read.

So we stop the run and set `Aborted` plus a reason on the result. It doesn't throw, because throwing would destroy the counts and rejections gathered so far, and they are the whole point of the run.

We count throws only. Returned failures are bad data, and a feed can be 100% rejects. That should still end with a report, so the breaker doesn't react to it.

### What would I fix first?

The writer from Part 2 retries one exact FK and nothing else, and here is why that gap matters.

A transient SQL timeout and a really invalid row both land in that last `catch`, and both come out as "unexpected error".

That makes the rejection list a list for diagnosis, and we can't use it as a retry queue.

If we give the two cases separate outcomes, we can retry one and report the other.

## Batching and the event storm

When we bypass the content layer, no catalog events are raised for us.

The obvious fix is to raise one event per row, and it is worse than the problem. On a multi-instance setup every raised catalog event is a message on the bus, so 250,000 rows means 250,000 messages.

What I wanted was one coalesced event per batch. I used an `AsyncLocal` ambient scope, so we don't have to pass a batch object through every method signature:

{% include code-modal.html
   id="2026-10-16-CatalogChangeBatch"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-16-CatalogChangeBatch.cs"
%}

We open exactly one of these per 500-row batch, in the same scope where we elevate the principal, because the DTO API reads the ambient principal and a scheduled job doesn't have one.

Here is a simplified excerpt. The full `CatalogBulkImporter` above also turns a failing `Begin()` into an abort, stops on `result.Aborted`, and counts every received row:

```csharp
var previousPrincipal = _principalAccessor.Principal;
_principalAccessor.Principal = new GenericPrincipal(
    new GenericIdentity(nameof(CatalogBulkImporter)),
    new[] { Roles.Administrators });

try
{
    // An explicit using block, not `using var`. A `using var` disposes at the end of the
    // method - after the finally below has restored the principal - so the broadcast would
    // run unelevated, which is the one thing the elevation exists to prevent.
    using (CatalogChangeBatch.Begin())
    {
        foreach (var row in batch)
        {
            ProcessRow(row, result, observer);
        }
    }
}
finally
{
    _principalAccessor.Principal = previousPrincipal;
}
```

Key points:

- `HashSet<int>` gives us de-duplication for free, so a SKU touched twice in one batch is reported once
- Without an open scope, the write is announced to nobody
- Clear the `AsyncLocal` before broadcasting, every time
- Reject nested scopes instead of half-supporting them
- Guard the failure handler, because a logging sink that throws undoes the isolation

### The open scope

With no batch open, `EntryChanged` returns silently and the write is announced to nobody.

I made it that way on purpose, since it keeps the writers usable outside an import, but it is exactly the kind of silence I would expect to cause trouble six months later. If you split the writers out for reuse, make the missing scope loud.

### Clearing the AsyncLocal

If a subscriber throws during `Dispose` and the slot is left pointing at a disposed batch, every later write in that flow goes into an object whose `Dispose` is already a no-op. Those writes are announced to nobody, permanently.

Clearing the slot only "if we're still the innermost scope" is exactly how you get there. So `Dispose` clears the slot first, before it raises anything. A subscriber that writes catalog rows of its own then doesn't add them to a batch that has already broadcast, and doesn't hit the "already open" throw from `Begin()`.

### Nested scopes

Letting scopes nest and restoring the parent on dispose works against coalescing. The inner scope raises its own broadcast, and its ids are never folded into the outer one.

So wrapping a run in an outer scope gives you one broadcast per inner scope plus an empty outer one.

That is why we make `Begin()` throw if a scope is already open. Half-supporting a feature that undoes the whole purpose of the class is worse than not having it.

### The failure handler

`OnBroadcastFailed` is where you wire your logging. A logging sink that throws there would bring back the exact failure the isolation is there to prevent, so the class guards that call too, and a handler that throws is swallowed.

### The supporting types

The importer's own result model, the same types as in the first listing above, as one compilable file:

{% include code-modal.html
   id="2026-10-16-ImportContracts"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-16-ImportContracts.cs"
%}

This one doesn't build on its own: `CatalogBulkImporter` also uses `ProductImportItem`, `WriteOutcome`, `ExceptionTree`, and `ICatalogEntryWriter`, which ship with [Part 2]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %}). Take all five files across Parts 2 and 3, and they build together against a clean Commerce 15 project.

## What's next?

At this point the import is correct: it writes what it should, survives what it should not, and explains itself afterward.

Unfortunately, it is still far too slow. I measured about five rows a second on a restored production database, which is about 14 and a half hours for our 252,670-row file. [Part 4]({% post_url 2026-10-21-the-low-level-api-is-not-automatically-fast-part-4-counting-round-trips %}) counts the database round trips behind each row and goes through the four fixes I would apply first.

## Summary

A dirty feed is the normal case, so we build the import to finish in spite of one and to explain itself afterward. What does that ask of the design?

- Three layers of failure with one report shape: a bad CSV row, an anticipated write failure, and an unexpected throw all reach the reader looking the same.
- We don't use a transaction for the whole run, because wrapping a batch would let one bad row roll back 499 good ones.
- Nothing throws out of the run. The per-row `catch` lets `OutOfMemoryException` through on purpose, since there is nothing useful to report after one. The counts and the rejections are the point of the run, so even the circuit breaker is a recorded outcome.
- The row number travels with the rejection, because the writer knows SKUs and whoever fixes the feed knows lines.
- One coalesced catalog event per batch, since raising one per row would put 250,000 messages on the bus.

That's it for error handling! Do you handle per-row import failures differently? Let me know in the comments!

Thank you for reading, and stay tuned for Part 4.

## This Post is Part of a Series

- [Part 1: Why the DTO API]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %})
- [Part 2: The Writer]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %})
- Part 3: Error Handling and Event Batching - (this post)
- [Part 4: Counting Round Trips]({% post_url 2026-10-21-the-low-level-api-is-not-automatically-fast-part-4-counting-round-trips %})
- [Part 5: Process Uptime and the Benchmark]({{ '/2026/10/28/what-mattered-more-than-the-api-part-5-process-uptime-and-the-benchmark/' | relative_url }})
