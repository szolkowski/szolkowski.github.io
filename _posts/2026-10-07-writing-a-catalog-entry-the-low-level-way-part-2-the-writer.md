---
layout: post
title:  "Writing a Catalog Entry the Low-Level Way. Part 2: The Writer"
description: "Upserting a catalog entry through CatalogContext and MetaDataPlus: ContentGuid, the meta class seam, response groups, and a create path that rolls back."
date:   2026-10-07 10:00:00 +0200
# Pinned so dateModified cannot precede datePublished on a scheduled post.
last_modified_at: 2026-10-07 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
redirect_from:
  - /2026/10/07/Writing-a-Catalog-Entry-the-Low-Level-Way-Part-2-The-Writer.html
  - /2026/10/07/writing-a-catalog-entry-the-low-level-way-part-2-the-writer.html
image:
   path: assets/img/2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer.png
   alt: "Writing a Catalog Entry the Low-Level Way. Part 2: The Writer"
   width: 1200
   height: 630
primary_tag: commerce
tags:
- episerver
- optimizely
- commerce
- catalog
- import
- .NET
---

[Part 1]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %}) made the case for writing bulk catalog changes through `Mediachase.Commerce.Catalog` instead of `IContentRepository`: roughly three times faster, at the cost of version history, publish events, validation and a search-indexing question you have to answer yourself.

This part is the code that decision leads to. It's longer than the content-API version, and that's fair—most of the extra lines are things the content layer was doing for you.

## Writing an Entry the Low-Level Way

Here's the full upsert. It's longer than the content-API version, and that's fair: most of the extra lines are things the content layer was doing for you.

{% include code-modal.html
   id="2026-10-07-CatalogEntryWriter"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-07-CatalogEntryWriter.cs"
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

- `ContentGuid` is yours to set, or the content model cannot see the row
- Write nulls with `SetXxxNull()`, read them behind `IsXxxNull()`
- Re-read the new entry id; the save often leaves it at 0
- The meta class is the seam between the DTO and the content layer
- The DTO is its own change tracker, so guard every assignment
- Moving an entry between categories costs four calls, not one
- The create path is the only place all-or-nothing belongs

**`ContentGuid` is not decoration.** `entryRow.ContentGuid = Guid.NewGuid()` sets the field the content layer would normally own, and it's what makes the row addressable as `IContent` afterwards. Skip it and you get an entry the catalog is perfectly happy with and the content model cannot see.

**Nulls go both ways, and the read direction is worse.** Strongly typed `DataSet` columns reject a null assignment, so you call the generated `SetXxxNull()`. But *reading* a NULL column throws `StrongTypingException` rather than returning null, which means `?? string.Empty` on the property does nothing—the exception fires before the `??` is ever reached. Guard with `IsXxxNull()` first.

**The new id is not reliably handed back.** `entryRow.CatalogEntryId` is often still 0 after `SaveCatalogEntry`. Re-read it with `CatalogEntryInfo`, the cheapest response group there is, rather than the full one you used to look it up.

**The meta class is the seam, and it's named more subtly than it looks.** `MetaClass.Load(metaDataContext, nameof(Product))` is what keeps the DTO layer and the content layer describing the same thing: your `[CatalogContentType]` class defines the properties, the content-type sync provisions them as MetaDataPlus fields at startup, and the writer then sets them by `nameof`. The usual warning is "rename the class and the import stops finding its own fields"—but that only holds while `MetaClassName` on the attribute is left unset. Set it, as the benchmark jobs in Part 5 do, and the meta class is named by the attribute and renaming the type is harmless. Know which of the two you're relying on.

**Resolve the meta class once per run.** Not because it cannot change while the process is up—Commerce Manager and the MetaDataPlus admin pages can add or drop fields on a live context while your import is running—but because caching *pins* the definition for the run. A schema change landing at row 120,000 then cannot silently split your feed into rows written against two different shapes.

**The DTO *is* the change tracker.** `SaveCatalogEntry(existing)` inspects `DataRow.RowState` and issues statements only for dirty rows. That's why hydrating the DTO, mutating it, and saving it's the update pattern, and why guarding every assignment with `if (item.X is not null)` turns a partial payload into a partial update instead of nulling out every column the feed happened not to send that night. One distinction matters later: skipping *clean rows* is not the same as skipping the *call*. Invoke it on an entirely unchanged DTO and you still pay a round trip, a transaction scope and the cache work—which is why "don't save an unchanged entry" is on the fix list in Part 4.

**Response groups are the cost knob, and the cheapest setting is not always right.** `CatalogEntryFull | Variations` is what makes an update expensive, but the update path genuinely needs the variation row, and a nightly feed is mostly updates. Probing with a cheap group and re-reading on a hit would add a round trip to the common case to save one on the rare case. Size the read for the path you actually take most often, and be able to say why.

**Inject both statics, not just one.** `CatalogContext.Current` and `CatalogContext.MetaDataContext` are what you'll see everywhere. Injecting only the first moves the wall a few lines down and fixes nothing; both belong in the constructor. The payoff is bounded but real—the validation branches become genuinely unit-testable, where before nothing was. No more than that, though: `MetaClass.Load` is still a static that does I/O, `MetaClass` and `MetaObject` are concrete types with no interface behind them, and the change notifier is reached statically too. Getting the write paths under test needs your own `IMetaClassProvider` and a sink interface, which is a bigger change than this post makes and worth knowing before you start.

**Injecting `MetaDataContext` is not the no-op refactor it looks like, so take the `ServiceAccessor`.** `CatalogContext.MetaDataContext` is a memoised, connection-string-keyed instance shared by everything in the process. `MetaDataContext` itself is registered **transient**, so `ctor(MetaDataContext)` hands your writer a *private* one, with its own MetaDataPlus cache and, more to the point, its own `Language`. That's the property deciding which language row `SetMetaField` writes to. Capture one in a writer you registered as a singleton and you've quietly changed behaviour while believing you tidied a static away. `ServiceAccessor<MetaDataContext>` resolves per call, which is what Optimizely's own `CatalogMetaObjectRepository` takes, and for the same reason.

**Moving an entry between categories is a four-step dance.** Delete the primary relation, save, **re-read the DTO**, add the new one. The `DataSet` cannot hold a deleted primary row and a replacement at the same time. Note the short-circuit when the entry is already in the right place—over a quarter of a million rows that's most of them, and it saves three round trips each time: the delete-and-save, the re-read, and the add-and-save. A move that actually happens costs four calls; one that doesn't costs one.

**The create path is the one place all-or-nothing is right, and one hole stays open.** The entry row commits before the node relation, meta fields and prices do. If any of those throw, a half-created entry with no category and no price is worse than no entry, so it's deleted. A failed rollback becomes an `AggregateException` naming both causes, because "the cleanup also failed" is a different incident from "the write failed".

The hole: rolling back needs the entry id, and occasionally the id is not handed back after the save and the re-read cannot identify the row. When that happens the writer **refuses to guess**—it won't hand an unverified id to a recursive delete—so it reports a failure and leaves the row alone. That's a genuine orphan: an entry with no category, no meta fields and no price, and no event raised for it, so nothing downstream will ever hear about it. Two things make it tolerable rather than alarming. It's rare, and it's usually *self-healing*: because the writer upserts on SKU, the next run finds that row, takes the update path, and fills in what was missing. "Usually" because there's one gap—if the SKU next arrives with IsActive false, the writer takes the deactivate path, which only flips a meta flag, and the entry stays uncategorised and unpriced until it comes back active. Worth knowing about; not worth building a second recovery mechanism for.

**One retry, and it's a correctness fix.** `SqlException { Number: 547 }` naming the node-entry FK means a cached category id outlived its category—someone deleted it, or the database was restored under a running app. Invalidate and try once. This is *not* a transient-failure retry, and Part 3 comes back to why that gap matters.

### The Supporting Types

The writer above leans on the small types introduced in prose along the way. Here they are in one file, so the snippet compiles rather than merely reading well:

{% include code-modal.html
   id="2026-10-07-WriterContracts"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-07-WriterContracts.cs"
%}

Drop that in alongside `CatalogEntryWriter` and both build against a clean Commerce 15 project. One deliberate difference from the listing above: `Product` is a **plain class** in the contracts file, not a `[CatalogContentType] VariationContent` subclass. The writer only ever reaches it through `nameof`, and shipping a live catalog content type in a set of article snippets would provision a meta class in your Commerce database the moment you pressed F5. In your own solution it's the real content type—the one thing that won't work against the stand-in is the `GetDefault<...>()` call from Part 1, which needs a genuine one.

Part 3 needs this file too: the bulk importer reaches into it for `ProductImportItem`, `WriteOutcome`, `ExceptionTree` and `ICatalogEntryWriter`.


## What's Next?

The writer handles one row. It does not decide what happens when row 148,213 has a missing price and 249,999 other rows are waiting behind it—and on this project, answering that was a stated product requirement rather than an engineering nicety. Part 3 covers the per-row failure report, why there is deliberately no run-wide transaction, and how to raise one catalog event per batch instead of a quarter of a million of them.

Have you hit a DTO-layer trap I have not listed here? Let me know in the comments!

Thank you for reading, and stay tuned for Part 3.

## This Post is Part of a Series

- [Part 1: Why the DTO API]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %})
- Part 2: The Writer - (this post)
- Part 3: Error Handling and Event Batching - coming soon
- Part 4: Counting Round Trips - coming soon
- Part 5: Process Uptime and the Benchmark - coming soon
