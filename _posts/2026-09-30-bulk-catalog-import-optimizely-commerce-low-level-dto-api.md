---
layout: post
title:  "Bulk Catalog Import in Optimizely Commerce: the Low-Level DTO API"
description: "Bulk-importing 250,000 products through the Commerce DTO API instead of IContentRepository — measured 3x faster, what you give up, and per-row error reporting."
date:   2026-09-30 10:00:00 +0200
# Pinned so dateModified cannot precede datePublished on a scheduled post.
# Remove (or bump) if the post is substantively edited after publication.
last_modified_at: 2026-09-30 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
redirect_from:
  - /2026/09/30/Bulk-Catalog-Import-in-Optimizely-Commerce-the-Low-Level-DTO-API.html
  - /2026/09/30/bulk-catalog-import-optimizely-commerce-low-level-dto-api.html
image:
   path: assets/img/2026-09-30-bulk-catalog-import-optimizely-commerce-low-level-dto-api.png
   alt: "Bulk Catalog Import in Optimizely Commerce: the Low-Level DTO API"
   width: 1200
   height: 630
primary_tag: commerce
tags:
- episerver
- optimizely
- commerce
- catalog
- performance
- import
- .NET
---

A while back I wrote about [reading a large catalog without running out of memory]({% post_url 2026-02-18-memory-efficient-catalog-traversal-in-optimizely-commerce-part-1-building-the-service %}). This post is about the other direction, which turns out to be the harder one: **writing** a quarter of a million products into Optimizely Commerce, nightly, from a PIM feed you do not control.

The project I have in mind receives a product export that started at 252,670 rows and later grew past a million. It arrives as a CSV, or in 50-item chunks over an API, and it is exactly as clean as any feed produced by a system somebody else owns—which is to say, some rows are wrong. A missing price here, an unescaped quote there, a category that does not exist yet.

Two requirements fall out of that, and they were both written down before a line of code was:

1. The write path has to be **fast enough** that a full feed finishes overnight.
2. **Commit record by record, and return a report of every row that failed, with the reason it failed.** Not "the import succeeded" or "the import failed"—a per-row verdict. A run that writes 249,500 rows and hands back a list of the 500 it could not write, each with its reason and its line in the file, is a success. A run that stops at row 148,213 because one price was missing is a failure, even though it hit a genuine problem.

That second one is worth dwelling on, because it is a *product* requirement rather than an engineering preference. Somebody has to fix the feed, and that somebody does not have access to your logs. What they need is a list they can act on. Everything in the error-handling section below exists to produce that list.

The answer to the first one is to stop using `IContentRepository`. Let me explain why, and then what it costs you—because it does cost you something, and most write-ups skip that part.

## The Problem with the Content API

The obvious way to create a variant in Optimizely Commerce is the content way:

```csharp
// MyVariant is your own [CatalogContentType] VariationContent subclass
var variant = _contentRepository.GetDefault<MyVariant>(categoryLink);
variant.Code = "SKU-1234";
variant.Name = "Widget, 10 ml";
_contentRepository.Save(variant, SaveAction.Publish, AccessLevel.NoAccess);
```

This is correct, readable, and exactly what you want when an editor changes one product in the UI. It is also doing a great deal of work you did not ask for. Per row, `Save(…, SaveAction.Publish)`:

- writes a **new content version** (and, on an update, supersedes the previous one);
- runs the **publish pipeline**—validation, access checks, property save handlers;
- fires **`IContentEvents`**, which your own subscribers and half the platform are listening to;
- invalidates content caches, locally and across instances;
- queues the item for **indexing**.

For one editorial change that is the whole point. For 250,000 rows of machine-generated feed data it is 250,000 version rows describing a history nobody will ever open, plus 250,000 trips through a pipeline whose every stage is irrelevant to a row that came out of a spreadsheet.

Commerce has a second, older door into the same tables, and for bulk writes it is the right one.

## Two APIs, One Catalog

Underneath the content layer, catalog entries and nodes are ordinary SQL rows, reached through `Mediachase.Commerce.Catalog`—`CatalogContext.Current`, strongly typed `DataSet`s (`CatalogEntryDto`, `CatalogNodeDto`, `CatalogRelationDto`), and `MetaDataPlus` for the custom fields. This is the API Commerce Manager was built on. It predates the content model, it is still present and un-deprecated in Commerce 15—nothing in my solution suppresses an `[Obsolete]` to use it—and Commerce Manager still runs on it.

| | `IContentRepository` | `ICatalogSystem` DTO API |
| ---------- | ---------- | ---------- |
| What it writes | a content version **and** the catalog rows | the catalog rows |
| Version history | a new version per save | none—rows are updated in place |
| Publish events | `IContentEvents` fire | no `IContentEvents`, no catalog event—but see the note below |
| Content-type validation | runs | does not exist |
| Access checks | enforced | you are on your own |
| Search / Graph indexing | triggered by publish | **verify this yourself**—see below |
| Good for | an editor changing one product | a feed changing 250,000 |

### So How Much Faster Is It, Actually?

I got tired of seeing this asserted and never measured, so I measured it. Two scheduled jobs, the same catalog content type, the same generated rows, the same batch loop, the same throwaway category—the only difference is what happens inside one row's write. Both jobs are at the end of this post; run them and you will get a number that is true for *your* database rather than mine.

Writing 5,000 variants into an empty category, prices excluded on both sides. I ran the whole thing four times over five days, because the first result deserved a second opinion and the second one disagreed with the first:

| 5,000 rows | Content API | DTO API | Ratio (total) | Ratio (median batch) |
| ---------- | ---------- | ---------- | ---------- | ---------- |
| **Pair A**—31 Aug | 64.05 min | 19.59 min | **3.27×** | 3.15× |
| **Pair B**—1–2 Sep | 59.92 min | 21.87 min | **2.74×** | 2.72× |
| **Pair C**—3–4 Sep | 64.66 min | 21.28 min | **3.04×** | 3.08× |
| **Pair D**—4 Sep | 62.11 min | 22.03 min | **2.82×** | 2.90× |

**Call it three times faster. Not a hundred.** I am quoting it plainly because the folklore around this API is wilder than the measurement, and a 3× claim you can reproduce is worth more than a 100× claim you cannot. Four independent pairs landed between **2.74× and 3.27×**, averaging just under 3×. Treat that spread as the result: no single figure here is a constant, and if I had run it once I would have published whichever number I happened to get.

Pairs C and D are the ones I would trust most, and it is worth saying why: they are the only pairs where **both** arms were started on a freshly restarted application. In Pair A neither was, and in Pair B only the DTO arm was—which, as a later section explains at some length, turns out to matter more than anything else I measured. All four pairs were nonetheless run on comparatively young processes, which is why they agree as closely as they do.

One caution about reading the table: within a pair, total and median are not two results. Throughput is the total over the row count, and the batch times all but sum to the total—3,842.6 s of batches against a recorded 3,843.2 s in Pair A, the 0.6 s gap being the per-batch progress write, which sits inside the run clock but outside the batch clock—so the median is a second *statistic* on one dataset. It tells you no single outlier batch is dragging the mean, and nothing more. The replication that counts is having four pairs, not having two numbers per pair.

Read the absolute figures with care. This was a Debug build on a developer machine against a local SQL Server, which inflates both paths; the **ratio** is the transferable part, not the rows per second. And prices were deliberately excluded from both sides, because `IPriceService` is the same call either way—turning it on adds the same constant to both and pushes the ratio *down*, closer to 1. If most of your per-row cost is the price write, the layer you write entries through matters proportionally less.

**One asymmetry I could not remove, and it probably favours the DTO side.** My DTO job writes no SEO row for the entries it creates—that much I can state flatly, it is my code. What I have *not* verified is the other half: whether `Save(…, Publish)` writes one for you when you never populate `SeoInformation`. I assumed it did, wrote that here as fact, and on checking found I had no evidence for it. So take it as an open question with a direction: if the content path does write a URL segment per entry, then it is doing work my DTO path skips, the two paths are not producing equivalent entries, and the ratio above is a **ceiling** rather than a midpoint for anyone whose catalog needs URLs. Worth ten minutes with a row counter before you lean on the number.

So the case for the DTO path is not that it is dramatically faster. It is that it is meaningfully faster *and* it stops writing a version row per product you never intend to version—with everything in the next section as the price.

### What You Give Up

This is the part that belongs in the same breath as the speed argument, because it is a real trade and I have watched people discover it the expensive way.

- **No version history means no rollback.** Content-version rollback is the obvious recovery mechanism for "the feed was wrong last night", and it simply does not apply to rows that never became a content version. Catalog export/import is a bulk mechanism, not a point-in-time restore. If you need a lever, you have to build one—a snapshot, a reversible feed, something.
- **No publish events means nothing downstream hears you.** If your front end revalidates its cache off `IContentEvents.PublishedContent`, an import writes 250,000 rows and purges nothing. Anything hooked to publishing has to be re-hooked to whatever you raise instead. One qualification, because "nothing fires" is not quite true of the whole write: **`IPriceService` still broadcasts, once per SKU.** Setting prices purges caches, replicates to the price-detail tables and raises a price-updated event which, on a multi-instance setup, is a message on the bus. So the entry write is silent and the price write is not—and that asymmetry is exactly why the price call turns up as the top item on the fix list further down.
- **Search almost certainly goes stale, and you should confirm exactly how stale.** Follow the chain: the Graph CMS integration indexes off content events, the DTO path raises none, so nothing pushes these writes into the index as they happen. Scanning the Graph assemblies in my own solution, I found nothing subscribing to the Commerce catalog events either. The remaining mechanism is the scheduled indexing job—which would mean your catalog is stale in search for up to one job interval after every import. That is a conclusion from evidence rather than a measurement: I have not timed a write end-to-end and I am not going to present a deduction as a stopwatch. Do that timing on a test environment before you rely on it, because if it holds, "search is up to an interval behind the catalog" is a fact your business stakeholders need before go-live, not after.
- **No validation.** No `[Required]`, no `IValidate<T>`, no content-type rules. A missing mandatory field becomes a `NULL` column, not a rejected save. Every check you care about is now yours to write—which is the subject of the section after next.

## Writing an Entry the Low-Level Way

Here is the full upsert. It is longer than the content-API version, and that is fair: most of the extra lines are things the content layer was doing for you.

{% include code-modal.html
   id="2026-09-30-CatalogEntryWriter"
   lang="csharp"
   file="post_assets/code-snippets/2026-09-30-CatalogEntryWriter.cs"
%}

It works against a small, deliberately boring input model:

```csharp
public sealed class ProductImportItem
{
    public string? Sku { get; set; }            // the natural key - this is what makes replay safe
    public string? DisplayName { get; set; }
    public string? CategoryCode { get; set; }
    public bool? IsActive { get; set; }
    public decimal? RetailPrice { get; set; }
    public decimal? ListPrice { get; set; }
    public double? Weight { get; set; }
    public string? Volume { get; set; }
}

public sealed record WriteOutcome(bool Succeeded, string? Error)
{
    public static WriteOutcome Success() => new(true, null);

    public static WriteOutcome Failure(string error) => new(false, error);
}
```

`Product` is your own catalog content type—the thing `MyVariant` was in the first example, named for what the feed carries:

```csharp
[CatalogContentType(GUID = "...")]
public class Product : VariationContent
{
    // Only the fields the feed adds. DisplayName is already on EntryContentBase -
    // redeclare it and you shadow the framework's property rather than using it.
    public virtual bool IsActiveInFeed { get; set; }
    public virtual string Volume { get; set; }
}
```

The writer never news one up. It only ever uses the type's *name*—`nameof(Product)` to load the meta class, `nameof(Product.Volume)` to set a field—which is the seam described in the key points below.

Plus three small collaborators, none of which are interesting enough to dwell on here:

```csharp
public interface ICatalogEntryWriter
{
    WriteOutcome Upsert(ProductImportItem item);
}

// Caches code -> (catalogId, nodeId), because resolving it per row is a
// round trip you pay 250,000 times. Invalidate exists for the stale-node retry.
public interface ICategoryResolver
{
    (int CatalogId, int NodeId)? Resolve(string? categoryCode);

    void Invalidate(string categoryCode);
}

// Wraps IPriceService. See the throughput section for why this one deserves
// a batching overload rather than the per-SKU call the writer makes here.
public interface ICatalogPriceWriter
{
    void SetPrices(string entryCode, decimal? listPrice, decimal salePrice);
}

// Load-or-create a MetaObject and persist it only when it is actually dirty.
public interface IMetaObjectWriter
{
    MetaObject LoadOrCreate(int objectId, MetaClass metaClass);

    void Persist(MetaObject metaObject);
}
```

**Key Points:**

- **`ContentGuid` is yours to set.** `entryRow.ContentGuid = Guid.NewGuid()` is not decoration—it is the field the content layer would normally own, and it is what makes the row addressable as `IContent` afterwards. Skip it and you get an entry that the catalog is happy with and the content model cannot see.
- **`SetXxxNull()`, not `= null`.** Strongly typed `DataSet` columns reject a null assignment. And the trap in the other direction is worse: *reading* a NULL column throws `StrongTypingException` rather than returning null, so `?? string.Empty` on the property does nothing—the exception fires before the `??` is ever reached. Guard with the generated `IsXxxNull()` first.
- **The new id is not reliably handed back.** `entryRow.CatalogEntryId` is often still 0 after `SaveCatalogEntry`. Re-read it with `CatalogEntryInfo`, the cheapest response group there is, rather than the full one you used to look it up.
- **The meta class is the seam between the two layers, and it is named more subtly than it looks.** `MetaClass.Load(metaDataContext, nameof(Product))` is what keeps the DTO layer and the content layer describing the same thing: your `[CatalogContentType]` class defines the properties, the content-type sync provisions them as MetaDataPlus fields at startup, and this writer then sets them by `nameof`. The usual warning is "rename the class and the import stops finding its own fields"—but that only holds while `MetaClassName` on the attribute is left unset. Set it, as the benchmark jobs later in this post do, and the meta class is named by the attribute and renaming the type is harmless. Know which of the two you are relying on.

- **Resolve the meta class once per run—but not for the reason I first wrote down.** I originally justified caching it with "it cannot change while the process is up." That is wrong: Commerce Manager and the MetaDataPlus admin pages can add or drop fields on a live context while your import is running. Caching is still right, and the real reason is better—it *pins* the definition for the run, so a schema change landing at row 120,000 cannot silently split your feed into rows written against two different shapes.
- **The DTO *is* the change tracker.** `SaveCatalogEntry(existing)` inspects `DataRow.RowState` and issues statements only for dirty rows. That is why hydrating the DTO, mutating it, and saving it is the update pattern—and why guarding every assignment with `if (item.X is not null)` turns a partial payload into a partial update instead of nulling out every column the feed happened not to send that night. Note the distinction, because it matters later: skipping *clean rows* is not the same as skipping the *call*. Invoke it on an entirely unchanged DTO and you still pay a round trip, a transaction scope and the cache work—which is why "do not save an unchanged entry" is on the fix list below.
- **Response groups are the cost knob, and the right setting is not always the cheapest one.** `CatalogEntryFull | Variations` is what makes an update expensive—but the update path genuinely needs the variation row, and a nightly feed is mostly updates. Probing with a cheap group and re-reading on a hit would add a round trip to the common case to save one on the rare case. Size the read for the path you actually take most often, and be able to say why.
- **The statics are injected, and it took three passes to get that honest.** `CatalogContext.Current` and `CatalogContext.MetaDataContext` are what you will see everywhere, including in my own first draft. My first correction injected only the first, which moved the wall a few lines down and fixed nothing. Both are constructor parameters now, and the result is that the **validation branches are genuinely unit-testable** where before nothing was. I am not going to claim more than that: `MetaClass.Load` is still a static that does I/O, `MetaClass` and `MetaObject` are concrete types with no interface behind them, and the change notifier is reached statically too. Getting the write paths under test means your own `IMetaClassProvider` and a sink interface—a bigger change than this post is making, and worth knowing before you start.

- **Injecting `MetaDataContext` is not the no-op refactor it looks like, so take the `ServiceAccessor`.** `CatalogContext.MetaDataContext` is a memoised, connection-string-keyed instance shared by everything in the process. `MetaDataContext` itself is registered **transient**—so `ctor(MetaDataContext)` hands your writer a *private* one, with its own MetaDataPlus cache and, more to the point, its own `Language`. That is the property deciding which language row `SetMetaField` writes to. Capture one in a writer you registered as a singleton and you have quietly changed behaviour while believing you tidied a static away. `ServiceAccessor<MetaDataContext>` resolves per call, which is what Optimizely's own `CatalogMetaObjectRepository` takes, and for the same reason.
- **Moving an entry between categories is a four-step dance.** Delete the primary relation, save, **re-read the DTO**, add the new one. The `DataSet` cannot hold a deleted primary row and a replacement at the same time. Note the short-circuit when the entry is already in the right place—over a quarter of a million rows, that is most of them, and it saves three round trips each time: the delete-and-save, the re-read, and the add-and-save. A move that actually happens costs four calls in total; one that does not costs one.
- **The create path is the one place all-or-nothing is right—with one hole I could not close.** The entry row commits before the node relation, meta fields and prices do. If any of those throw, a half-created entry with no category and no price is worse than no entry, so it is deleted. A failed rollback becomes an `AggregateException` naming both causes, because "the cleanup also failed" is a different incident from "the write failed".

  The hole: rolling back needs the entry id, and occasionally the id is not handed back after the save and the re-read cannot identify the row. When that happens the writer **refuses to guess**—it will not hand an unverified id to a recursive delete—so it reports a failure and leaves the row alone. That is a genuine orphan: an entry with no category, no meta fields and no price, and no event raised for it, so nothing downstream will ever hear about it. Two things make it tolerable rather than alarming. It is rare, and it is usually *self-healing*: because the writer upserts on SKU, the next run finds that row, takes the update path, and fills in everything that was missing. "Usually" because there is one gap—if the SKU next arrives with IsActive false, the writer takes the deactivate path, which only flips a meta flag, and the entry stays uncategorised and unpriced until it comes back active. Worth knowing about; not worth building a second recovery mechanism for.
- **One retry, and it is a correctness fix.** `SqlException { Number: 547 }` naming the node-entry FK means a cached category id outlived its category—someone deleted it, or the database was restored under a running app. Invalidate and try once. This is *not* a transient-failure retry, and I will come back to why that gap matters.

## One Bad Row Must Not Stop 249,999 Good Ones

Now the second requirement: commit record by record, and hand back a report of every failed row and why it failed. The rule I settled on is one sentence: **an expected failure is a returned outcome, an unexpected failure is an exception, and neither is allowed to end the run.**

Two consequences of "record by record" that are easy to miss until they bite:

- **There is no run-wide transaction, and there must not be one.** Each row commits on its own. Wrapping the batch would mean one bad row rolling back 499 good ones, which is the behaviour the requirement exists to prevent. The only all-or-nothing scope in the whole design is the create path in the previous section, and it spans a single entry.
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
   id="2026-09-30-CatalogBulkImporter"
   lang="csharp"
   file="post_assets/code-snippets/2026-09-30-CatalogBulkImporter.cs"
%}

**Key Points:**

- **Carry the row number all the way through.** The writer only knows SKUs; the person fixing the feed only knows line numbers. Keeping `RowNumber` on the rejection is the difference between a report that gets acted on and one that gets ignored.
- **Count and report in the same place.** A single `Reject` helper means a rejection can never be counted without being reported, or reported without being counted. Those two drifting apart is a genuinely annoying bug to chase.
- **The natural key does the idempotency work.** Because the writer upserts on SKU and guards every field, re-running the same feed is a no-op in effect. There is no run id and no "have I imported this file already?" check—and there does not need to be, as long as the key is the feed's own and every write is guarded.
- **Stop is cooperative and checked between batches.** Rows already written stay written. A stopped import is a partial success and the report should say exactly that, out loud, rather than looking like a failure.

- **`Stopped` and `Aborted` are different outcomes.** A human pressing Stop sets the first; the breaker sets the second. Anything rendering "was this a partial run?" needs both, or needs to compare `Received` against `Accepted + Rejected`.
- **There is a circuit breaker, and it is also an outcome rather than an exception.** A hundred consecutive rows that *throw* is not a dirty feed, it is a dropped connection or a full disk, and rejecting the remaining 249,000 one at a time would take hours and produce a report nobody can read. So the run stops and sets `Aborted` plus a reason on the result—it does not throw, because throwing would destroy the counts and rejections gathered so far, which are the entire deliverable. Note it counts throws only: returned failures are bad data, and a feed can legitimately be 100% rejects. That is a report, not an outage.

And the honest weakness, because it is the first thing I would fix: **a transient SQL timeout and a genuinely invalid row both land in that last `catch` and both come out as "unexpected error".** The rejection list is therefore a *diagnosis* list, not a retry queue. Give the two cases distinct outcomes and you can retry one and report the other; leave them merged and you can only do the second.

## Batching and the Event Storm

Bypassing the content layer means no catalog events are raised for you. The naive fix—raise one per row—is worse than the disease: on a multi-instance setup, every raised catalog event is a message on the bus, and 250,000 of them queued as fire-and-forget tasks is a thread-pool saturation mechanism, not a notification strategy.

What you want is one coalesced event per batch. An `AsyncLocal` ambient scope gets you that without threading a batch object through every method signature:

{% include code-modal.html
   id="2026-09-30-CatalogChangeBatch"
   lang="csharp"
   file="post_assets/code-snippets/2026-09-30-CatalogChangeBatch.cs"
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

- **`HashSet<int>` gives you de-duplication for free.** A SKU touched twice in one batch is reported once.
- **The scope is a precondition, not a convenience.** With no batch open, `EntryChanged` returns silently and the write is announced to nobody. That is deliberate—it keeps the writers usable outside an import—but it is exactly the kind of quiet that bites six months later. If you split the writers out for reuse, make the missing scope loud.
- **Clear the `AsyncLocal` in a `finally`, unconditionally.** If a subscriber throws during `Dispose` and the slot is left pointing at a disposed batch, every later write in that flow accumulates into an object whose `Dispose` is already a no-op—announced to nobody, permanently. I had a version that only cleared the slot "if we are still the innermost scope", which is precisely how you get that.
- **Do not support nesting—reject it.** An earlier draft of this class let scopes nest and restored the parent on dispose, which sounds accommodating and is the opposite of coalescing: the inner scope raises its own broadcast and its ids are never folded into the outer one, so wrapping a run in an outer scope gets you one broadcast per inner scope plus an empty outer one. `Begin()` now throws if a scope is already open. Half-supporting a feature that undoes the class's entire purpose is worse than not having it.
- **Guard the failure handler too.** `OnBroadcastFailed` is where you wire your logging—and a logging sink that throws recreates the exact failure the isolation exists to prevent.

### The Supporting Types, in One File

The three snippets above are the interesting parts, and I have introduced their companions in prose as they came up—which reads better than a wall of DTOs, but does mean the three files do not compile on their own. So here is everything they lean on, in one place: the import item and its outcome, the four collaborator interfaces, and the parse-and-report types from the section before this one.

{% include code-modal.html
   id="2026-09-30-CatalogImportContracts"
   lang="csharp"
   file="post_assets/code-snippets/2026-09-30-CatalogImportContracts.cs"
%}

Drop that in alongside `CatalogEntryWriter`, `CatalogBulkImporter` and `CatalogChangeBatch` and all four build against a clean Commerce 15 project. One deliberate difference from the post above: `Product` is a **plain class** here, not a `[CatalogContentType] VariationContent` subclass. The writer only ever reaches it through `nameof`, and shipping a live catalog content type in a set of article snippets would provision a meta class in your Commerce database the moment you pressed F5. In your own solution it is the real content type—the one thing that will not work against the stand-in is the `GetDefault<...>()` call from the very first example, which needs a genuine one.

## The Low-Level API Is Not Automatically Fast

Here is the part I would most want to read in somebody else's post, so I will not skip it in mine.

On a restored production database, this import measured **500 products in 1.73 minutes—roughly 5 rows per second.** At that rate a 252,670-row file runs for many hours. The low-level API removed the content pipeline, and the result was *still* far too slow, because the cost had moved somewhere else entirely: into the number of database round trips per row.

The benchmark above landed at **3.9 to 4.3 rows/s** across its three DTO arms, which is close enough to be worth mentioning—but I want to be careful about how much weight that carries, because the two are not like-for-like and they are not independent. Both ran against a local database on my own machine. Both are the same DTO API, just two implementations of it. And they differ in two ways that should have pushed them *apart*: the project measurement wrote prices and ran against a full production catalog, while the benchmark had prices off and a far smaller node. On my own argument, those two costs should have made the production figure the slower of the pair, and it was marginally the faster.

So this is not the corroboration it first looks like. All I will claim from the two together is the order of magnitude: **a naive DTO importer runs at a few rows per second, not a few hundred.** That much is solid, and it is the part that matters for planning.

Count them on the update path—the common one, since a nightly feed is mostly re-writes:

| Step | Round trips |
| ---------- | ---------- |
| `GetCatalogEntryDto(sku, Full \| Variations)` | 1 |
| Category resolve | 0 if cached, 1 if not |
| `GetCatalogRelationDto` (+3 more on an actual move) | 1–4 |
| Price write | 1 transaction, plus cache purge and a remote event |
| `SaveCatalogEntry`—fired unconditionally | 1 |
| Meta object load (uncached) + persist | 2 |

That is **six to ten round trips per row**—or eight to twelve if you count the price write's transaction, cache purge and remote broadcast as the three separate pieces of work they are. At cloud-hosting latency the round trips *are* the runtime. Four fixes, in the order I would apply them:

1. **Use the batch price overload.** `IPriceService.SetCatalogEntryPrices`—the single-key form, taking one `CatalogKey`—opens its own transaction, purges caches, replicates to the price-detail tables and broadcasts a price-updated event **per SKU**. The `IEnumerable<CatalogKey>` overload collapses 500 transactions and 500 broadcasts into one. On the shape of the work this is the highest-leverage change available—one transaction and one broadcast per batch instead of per row—and it is a one-line signature swap plus a buffer. I have not measured it: every run in this post had prices switched off on both sides.
2. **Do not save an unchanged entry.** `SaveCatalogEntry(existing)` fires even when zero rows are dirty. You already have `DataRow.RowState`; check it. The primary-node short-circuit in the writer above is the same idea applied one level down—extend it.
3. **Watch out for quadratic category creation.** If you generate categories from the feed, you will write something that gives each new node a unique uri segment. Mine did it by reading *every sibling under the parent, per language, per category created*—which makes creating N categories under one parent O(N²) in database work. That is my helper's design, not a Commerce behaviour, and it is the obvious way to write the check, which is exactly why it is worth naming. Resolve the sibling set once per parent and keep it.
4. **Know what `ReadMode.UnCached` costs you.** Loading the meta object uncached is a deliberate correctness trade—a cached meta object can lose a concurrent write—but it is a guaranteed database hit per row. Keep it, and know that it is part of your floor.

None of that requires going back to `IContentRepository`. It does require accepting that "I picked the fast API" is the beginning of the performance work, not the end of it.

### The Thing That Mattered More Than the API

Here is the finding I did not go looking for, and it turned out to dwarf everything above.

Partway through this work I re-ran the DTO benchmark and it came back **eight times slower** than the first time—15.50 minutes per batch of 500, against 1.96 the run before. Same code, same machine, same category, same row count. For a while I assumed I had broken something.

I had not. The only thing different was that the application had been running overnight. Restarting it and running the identical job again:

| DTO, 5,000 rows | Process uptime at start | Median batch of 500 |
| ---------- | ---------- | ---------- |
| Pair C, fresh restart | ~10 minutes | **2.10 min** |
| Pair D, fresh restart | ~10 minutes | 2.16 min |
| Pair B, fresh restart | ~10 minutes | 2.18 min |
| Pair A | ~1h 45m | 1.96 min |
| After running overnight | **~16.5 hours** | **15.50 min** |

**A long-running process imported seven times slower than a freshly started one.** Nothing else about the run changed—same code, same machine, same category, same row count.

Note that uptime is not a tidy dial: the 1h 45m run is the fastest of the five, a shade quicker than any of the three fresh starts—which themselves land within 4% of each other. At the scale of an hour or two this is all noise. The effect that matters only shows up at the scale of a working day, and it is not subtle when it does.

And here is the part that should make you cautious about the number at the top of this post: **in that overnight run, the low-level DTO API was slower than `IContentRepository`.** 15.50 minutes per batch against the content API's 5.92 on a young process—roughly 2.6× the wrong way. The 2.7–3.3× advantage I measured is real, and it is real *for a process that has not been up very long*. It is not a property of the API that survives every condition, and I would not have found that out if I had run the benchmark once and published.

Once you look for it, there is drift inside every single session I ran, on both APIs. In Pair C the content path went 6.48 → 8.23 → 9.94 min per batch across its three volumes, and the DTO path went 2.10 → 2.46 → 3.17.

I had a version of this section that put those two rows in a table, worked out that both came to about +52%, and concluded that the matching percentages proved the decay was not a property of either write path. That was wrong, and it is worth showing why, because the mistake is an easy one.

**The two arms are not comparable.** The content session ran 190 minutes and put 12,000 rows into the node; the DTO session ran 144 minutes and put in 27,000. Normalise by either axis and the symmetry evaporates—per minute of uptime it is +0.28% against +0.35%, and per thousand rows written it is +4.4% against +1.9%, a factor of more than two. Two unnormalised endpoints happening to land on similar percentages is not a finding, it is a coincidence I nearly published.

**And the per-batch data leans the other way.** In the same run, batch cost jumps at every volume boundary—+13.7% and +11.6% on the content arm—where almost no wall-clock passes but several thousand rows land under the node. A discontinuity with no elapsed time is a population signature, not an uptime one.

So: within a single session I cannot separate uptime from node population, and I am not going to pretend otherwise. What I *can* separate is the overnight case, and that is the result worth having.

Node population simply cannot produce a number that size. Walk the fresh run: it went from a freshly cleaned category to 5,200 children at **2.18 min** per batch, then on to 12,200 children at **2.51**—7,000 more rows under the node bought a 15% slowdown. The overnight run was sitting at **15.50 and 17.64** over a comparable stretch. To pin that on population you would need the curve to go near-vertical somewhere the fresh run shows it almost flat. Within-session drift and a sevenfold collapse are different phenomena, and only the second one has a clean answer.

I want to be careful about how far I push that, because I did not record how many rows were in the category when the overnight run started, and I cannot reconstruct it from the artifacts. So I cannot give you a clean same-population pair. What I can say is that the fresh run measured the node-population effect directly, over the range in question, and it is small—nowhere near a factor of seven.

I have not chased it further, so I will not tell you what it is. Managed heap growth, cache pressure, connection-pool state and accumulated `AsyncLocal`/ambient state are all plausible and I have not separated them. What I will tell you is the operational consequence, because it is concrete and it is easy to act on:

- **Benchmark on a freshly started process, or your numbers are about uptime rather than about your code.** Both of my first two pairs were run on relatively young processes, which is the only reason they agree.
- **If a nightly import is slower than the same import run by hand, look at how long the app pool has been up before you look at your import.** In production an app pool routinely stays up for days. Mine was slow after sixteen hours.
- **Recycling before a large import is a one-line change** that, on this evidence, is worth more than any single optimisation in the previous section.

That last point is the uncomfortable one. I spent a section counting round trips per row, and every improvement available there is worth less than restarting the process first.

### Measure It on Your Own Catalog

Here is the benchmark, so you can disagree with me using your own data. Two scheduled jobs, one per API, each importing 5,000 then 50,000 then 500,000 generated products into the same throwaway category—same content type, same generated rows, same batch loop, so the only difference between the two sets of numbers is what happens inside one row's write.

Two things about how I ran it, both of which you need in order to reproduce the figures above.

There is **one** category, shared by both jobs, so I ran the cleanup job between the two passes; without that step the second job starts against the first job's rows and the comparison is not a comparison. And **restart the application before each pass**—see the section above for why that turned out to matter more than anything else here.

The code below is the code that produced Pair D, unchanged—I checked the file timestamps against the run headers rather than assuming, because I got this wrong twice while writing. That is worth insisting on generally: a post that says "run this and reproduce it" while shipping a tidied-up version of what actually ran is making a promise it has not kept. Check the run header the jobs print—it records the catalog id and language each arm resolved, so you can confirm the two halves are writing the same thing before you believe the ratio.

It reports, per volume: total elapsed and rows/second, and then the per-batch distribution—average, **median**, fastest and slowest for every 500-product batch. Both matter. The average tells you what the import costs; the gap between the average and the median tells you whether one 8-second GC pause is quietly moving your mean.

First the shared half—content type, data generator, timing, the self-creating category, and a cleanup job:

{% include code-modal.html
   id="2026-09-30-CatalogImportBenchmark-Shared"
   lang="csharp"
   file="post_assets/code-snippets/2026-09-30-CatalogImportBenchmark.Shared.cs"
%}

Then the two jobs. The content-API one:

{% include code-modal.html
   id="2026-09-30-ContentApiImportBenchmarkJob"
   lang="csharp"
   file="post_assets/code-snippets/2026-09-30-ContentApiImportBenchmarkJob.cs"
%}

And the DTO one:

{% include code-modal.html
   id="2026-09-30-DtoApiImportBenchmarkJob"
   lang="csharp"
   file="post_assets/code-snippets/2026-09-30-DtoApiImportBenchmarkJob.cs"
%}

**Key Points:**

- **Warm up before starting the clock.** A couple of hundred throwaway rows per job absorbs JIT, connection-pool ramp-up and the cold metadata cache. Skip this and whichever path you measure first looks slower than it is—which is the single easiest way to produce a benchmark that confirms whatever you already believed.
- **Fresh SKU prefix per volume.** Reuse one and the second volume is silently measuring *updates*, a different and usually faster shape.
- **Everything that is not the write happens outside the measurement.** Category resolution, meta class load, market lookup: once, up front. And the generator is lazy, so materialising 500,000 rows is not accidentally part of the result.
- **There is a time budget per volume**, one hour by default. A 500,000-row content-API pass can run for most of a day; without a ceiling the job gets killed by a deploy or a recycle and you learn nothing. When the budget is hit, the volume stops and the report extrapolates from what completed—`projected full 2,857.33 min for 500,000 rows at the observed rate` is a perfectly good answer, and a much cheaper one than finding out the long way. Read that projection as a **floor**, for the reason in the section above: it assumes the rate holds, and the rate falls.
- **The results go to disk, per volume, as they complete.** A scheduled job's result string is overwritten by the next run and is miserable to diff, so each volume is also appended to a readable `.txt` and to a `volumes.csv` you can chart—plus, optionally, one row per batch in its own file. Writing after *each* volume rather than at the end of the job is the point: if the 500,000-row pass dies to a recycle, the 5,000 and 50,000 numbers are already safe. File-write failures are collected and reported, never thrown, because losing a nine-hour run to a permissions error would be a poor joke.
- **Prices are off by default.** `IPriceService` is the same call on both paths—`IContentRepository` has no price story of its own—so including it just adds the same constant to both. Turn it on separately to see what the per-SKU price write costs you, because that is its own finding.
- **Clean up with the third job.** It deletes the benchmark category and everything under it in one call. Deleting half a million entries individually would take longer than creating them did.

Run it on a development or staging copy. It writes real catalog rows, and the content-API half leaves a content version behind for every one—on purpose, because the job counts them at the end and that count is usually what ends the argument.

## Gotchas

A short list of the things that cost me the most time, none of which look like problems until they are.

**These are my bugs, not Optimizely's.** Every item below is something my own importer did, and I am listing them because they are the kind of thing you will write too, not because the platform does them to you. Where an item describes a design choice of mine, I have said so—you should not go looking for a framework fix.

**The job shows "Unresponsive", and the process turns out to have restarted:**

- Read the causality carefully, because I had it backwards for a while: **a merely slow job never shows Unresponsive.** The scheduler's liveness ping runs on its own timer, independent of your job body.
- The flag means the process stopped answering—a recycle, an OOM, or thread-pool starvation—and left `IsRunning` set behind it.
- My cause was buffering an entire 200 MB CSV before writing a single row: a `MemoryStream`, a `ToArray()` copy, a UTF-8 validation pass over the whole array, then a fully materialised list of parsed rows. On the allocation arithmetic that is comfortably over a gigabyte, most of it on the large object heap—though I sized it from the allocation chain rather than confirmed it with a profiler, so treat the OOM as the leading suspect rather than a diagnosis.
- Stream the file.

**A file that rejects every row takes longer than one that imports every row:**

- **My job base wrote each log line in its own `DbContext` with its own `SaveChanges`**—a round trip per line. Every rejection was a log line, so a fully-rejecting file became an insert-per-row storm on the job thread.
- A stock job logging through `ILogger` has no such cost.
- Cap the rejections you narrate individually either way.

**100% of rows rejected, and it looks like a hang:**

- Case-sensitive parsing of a boolean column. A spreadsheet exporting `TRUE` instead of `true` rejects the entire file, which then triggers the symptom above.
- Parse tolerantly.

**The site is slow *after* a successful import:**

- **My category resolver flushed the entire Commerce node cache globally** on invalidate, and the import called it at both ends of the run.
- Every request afterwards ran cold, at exactly the moment the database was least rested.
- Invalidate the keys you touched.

**Two imports produce a result neither would alone:**

- **I registered the writers as singletons** holding per-run caches, and nothing stopped a scheduled job and an API call running at once.
- Yours will be however you registered them—check, and serialise explicitly if they hold state.

**Category creation gets slower the more categories you create:**

- **My SEO helper checked uri-segment uniqueness by reading every sibling under the parent**, once per category per language, which is O(N²) against sibling count.
- This is not something Commerce does to you; it is something a uniqueness check written the obvious way does.
- Resolve the sibling set once per parent and keep it.

**A row silently updates nothing:**

- An unguarded field assignment nulled a column the feed did not send—or a write happened outside the event scope and nobody downstream was told.

## Summary

For bulk catalog writes in Optimizely Commerce, the low-level DTO API is the right layer: it writes the rows you asked for and nothing else, no version per row, no publish pipeline per row. Measured across four independent pairs, that is worth **2.7-3.3×**, call it 3×—a real win, and a smaller one than the folklore suggests. What you give up is real too: no rollback lever, no events unless you raise them, no validation unless you write it, and an indexing question you must answer for your own solution rather than assume.

Around that writer, the pattern that made a quarter of a million rows survivable was:

- committing record by record, with no run-wide transaction, so one bad row cannot roll back the good ones;
- returning a **report of every failed row and its reason** as the deliverable—a partial run is an outcome to describe, not an error to throw;
- three stacked layers of per-row failure handling, where an expected failure is a returned outcome and only the genuinely unexpected is an exception;
- a rejection carrying both the natural key and the physical row number, counted and reported in one place;
- upsert on the feed's own key, so replay is safe without tracking runs;
- one coalesced catalog event per batch instead of one per row;
- and the discipline to actually count the round trips afterwards, because picking the fast API bought me a floor of a few rows per second and not one row more—and the humility to notice, eventually, that restarting the application mattered more than any of it.

If you have actually timed a DTO-layer write reaching Graph—or found a push path I missed—I would genuinely like to hear it, because I got as far as a well-supported deduction and stopped. Let me know in the comments!

Thank you for reading, and I hope this saves you a night of watching a progress bar that is not moving.
