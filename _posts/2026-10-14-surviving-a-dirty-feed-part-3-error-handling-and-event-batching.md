---
layout: post
title:  "Surviving a Dirty Feed. Part 3: Error Handling and Event Batching"
description: "Committing a 250,000-row Optimizely catalog import row by row, returning a per-row failure report, and coalescing catalog events into one per batch."
date:   2026-10-14 10:00:00 +0200
# Pinned so dateModified cannot precede datePublished on a scheduled post.
last_modified_at: 2026-10-14 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
redirect_from:
  - /2026/10/14/Surviving-a-Dirty-Feed-Part-3-Error-Handling-and-Event-Batching.html
  - /2026/10/14/surviving-a-dirty-feed-part-3-error-handling-and-event-batching.html
image:
   path: assets/img/2026-10-14-surviving-a-dirty-feed-part-3-error-handling-and-event-batching.png
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

[Part 2]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %}) built a writer that upserts one catalog entry through the DTO API. This part is about the other stated requirement from [Part 1]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %}), the one that turned out to shape the design more than speed did: commit record by record, and hand back a report of every row that failed and why.

## One Bad Row Must Not Stop 249,999 Good Ones

Now the second requirement: commit record by record, and hand back a report of every failed row and why it failed. The rule I settled on is one sentence: **an expected failure is a returned outcome, an unexpected failure is an exception, and neither is allowed to end the run.**

Two consequences of "record by record" that are easy to miss until they bite:

- **There's no run-wide transaction, and there must not be one.** Each row commits on its own. Wrapping the batch would mean one bad row rolling back 499 good ones, which is the behaviour the requirement exists to prevent. The only all-or-nothing scope in the whole design is the create path in the previous section, and it spans a single entry.
- **A partial run is a successful outcome, not an error.** The report is the deliverable. Whoever consumes it—an API caller getting the rejections in the response body, or an editor reading the job summary—needs the counts and the failed rows, not an exception.

That gives three layers, and the useful property is that they stack—a row can fail at any of them and comes out the far side looking the same to whoever reads the report.

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

`ParsedRow<T>` is layer one: a CSV reader configured with `BadDataFound` set to record-and-continue never throws for a bad *row*, it produces a row-shaped error carrying the physical line number. Layer two is the writer returning `WriteOutcome.Failure(...)` for anything it can anticipate. Layer three is one `try`/`catch` per record for everything else.

{% include code-modal.html
   id="2026-10-14-CatalogBulkImporter"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-14-CatalogBulkImporter.cs"
%}

**Key Points:**

- Carry the row number through; the writer knows SKUs, the reader knows lines
- Count and report a rejection in the same place, never separately
- The natural key does the idempotency work, so replay is safe
- Stop is cooperative and checked between batches
- `Stopped` and `Aborted` are different outcomes and both need rendering

**Carry the row number all the way through.** The writer only knows SKUs; the person fixing the feed only knows line numbers. Keeping `RowNumber` on the rejection is the difference between a report that gets acted on and one that gets ignored.

**Count and report in the same place.** A single `Reject` helper means a rejection cannot be counted without being reported, or reported without being counted. Those two drifting apart is a genuinely annoying bug to chase.

**The natural key does the idempotency work.** Because the writer upserts on SKU and guards every field, re-running the same feed is a no-op in effect. There's no run id and no "have I imported this file already?" check, and there doesn't need to be, as long as the key is the feed's own and every write is guarded.

**Stop is cooperative and checked between batches.** Rows already written stay written. A stopped import is a partial success and the report should say exactly that, out loud, rather than looking like a failure. `Stopped` and `Aborted` are separate flags: a human pressing Stop sets the first, the circuit breaker sets the second. Anything rendering "was this a partial run?" needs both, or needs to compare `Received` against `Accepted + Rejected`.

**The circuit breaker is an outcome too, not an exception.** A hundred consecutive rows that *throw* is not a dirty feed, it's a dropped connection or a full disk, and rejecting the remaining 249,000 one at a time would take hours and produce a report nobody can read. So the run stops and sets `Aborted` plus a reason on the result. It doesn't throw, because throwing would destroy the counts and rejections gathered so far, which are the entire deliverable. Note that it counts throws only: returned failures are bad data, and a feed can legitimately be 100% rejects. That's a report, not an outage.

And the honest weakness, which is the first thing I'd fix: **a transient SQL timeout and a genuinely invalid row both land in that last `catch` and both come out as "unexpected error".** The rejection list is therefore a *diagnosis* list, not a retry queue. Give the two cases distinct outcomes and you can retry one and report the other; leave them merged and you can only do the second.

## Batching and the Event Storm

Bypassing the content layer means no catalog events are raised for you. The naive fix—raise one per row—is worse than the disease: on a multi-instance setup, every raised catalog event is a message on the bus, and 250,000 of them queued as fire-and-forget tasks is a thread-pool saturation mechanism, not a notification strategy.

What you want is one coalesced event per batch. An `AsyncLocal` ambient scope gets you that without threading a batch object through every method signature:

{% include code-modal.html
   id="2026-10-14-CatalogChangeBatch"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-14-CatalogChangeBatch.cs"
%}

The importer opens exactly one of these per 500-row batch, in the same scope where it elevates the principal—the DTO API reads the ambient principal and a scheduled job has not got one:

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

**Key Points:**

- `HashSet<int>` gives you de-duplication for free
- The open scope is a precondition, not a convenience
- Clear the `AsyncLocal` in a `finally`, unconditionally
- Reject nested scopes rather than half-supporting them
- Guard the failure handler; a logging sink that throws undoes the isolation

**A SKU touched twice in one batch is reported once**, because the set does the work.

**The scope is a precondition, not a convenience.** With no batch open, `EntryChanged` returns silently and the write is announced to nobody. That's deliberate, since it keeps the writers usable outside an import, but it's exactly the kind of quiet that bites six months later. If you split the writers out for reuse, make the missing scope loud.

**Clear the `AsyncLocal` in a `finally`, unconditionally.** If a subscriber throws during `Dispose` and the slot is left pointing at a disposed batch, every later write in that flow accumulates into an object whose `Dispose` is already a no-op—announced to nobody, permanently. Clearing the slot only "if we're still the innermost scope" is precisely how you get that.

**Don't support nesting; reject it.** Letting scopes nest and restoring the parent on dispose sounds accommodating and is the opposite of coalescing: the inner scope raises its own broadcast and its ids are never folded into the outer one, so wrapping a run in an outer scope gets you one broadcast per inner scope plus an empty outer one. `Begin()` throws if a scope is already open. Half-supporting a feature that undoes the class's entire purpose is worse than not having it.

**Guard the failure handler too.** `OnBroadcastFailed` is where you wire your logging, and a logging sink that throws recreates the exact failure the isolation exists to prevent.

### The Supporting Types

The importer's own result model—the parse-and-report types introduced in prose above—lives in one file:

{% include code-modal.html
   id="2026-10-14-ImportContracts"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-14-ImportContracts.cs"
%}

This one is **not** self-sufficient: `CatalogBulkImporter` also uses `ProductImportItem`, `WriteOutcome`, `ExceptionTree` and `ICatalogEntryWriter`, which ship with [Part 2]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %}). Take both files and the three snippets across the two parts build together against a clean Commerce 15 project.


## What's Next?

At this point the import is correct: it writes what it should, survives what it should not, and explains itself afterwards. It is also still far too slow—about five rows a second on a restored production database, which is many hours for a quarter of a million of them. Part 4 counts the database round trips hiding behind each row and works through the four fixes worth making.

Do you handle per-row import failures differently? Let me know in the comments!

Thank you for reading, and stay tuned for Part 4.

## This Post is Part of a Series

- [Part 1: Why the DTO API]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %})
- [Part 2: The Writer]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %})
- Part 3: Error Handling and Event Batching - (this post)
- Part 4: Counting Round Trips - coming soon
- Part 5: Process Uptime and the Benchmark - coming soon
