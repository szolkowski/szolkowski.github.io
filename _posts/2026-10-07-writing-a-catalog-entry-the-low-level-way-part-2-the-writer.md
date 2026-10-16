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

In [Part 1]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %}) I explained why we write bulk catalog changes through `Mediachase.Commerce.Catalog` instead of `IContentRepository`. It is roughly three times faster, but we lose version history, publish events, and validation, and we have to answer the search-indexing question ourselves.

In this part I will show the code for it. It is longer than the content-API version, because most of the extra lines do work that the content layer was doing for us.

After the code I will go through the traps I wish I had known about before writing it.

## Writing an entry the low-level way

Here is the full upsert:

{% include code-modal.html
   id="2026-10-07-CatalogEntryWriter"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-07-CatalogEntryWriter.cs"
%}

It works against a small and boring input model:

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

`Product` is our own catalog content type. It plays the role `MyVariant` had in the Part 1 example, and it is named after what the feed carries:

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

The writer never creates a `Product` instance. It only uses the type's name: `nameof(Product)` to load the meta class and `nameof(Product.Volume)` to set a field. That is the seam described in the key points below.

There are also three small collaborators, which aren't interesting enough to spend time on here:

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

// Wraps IPriceService. See Part 4 of this series for why this one deserves
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

Key points:

- `ContentGuid` is yours to set, or the content model can't see the row
- Write nulls with `SetXxxNull()`, read them behind `IsXxxNull()`
- Re-read the new entry id, because the save often leaves it at 0
- The meta class is the seam between the DTO and the content layer
- The DTO is its own change tracker, so guard every assignment
- Moving an entry to another category costs four calls, or one when it is already there
- The create path is the only place where all-or-nothing belongs

## How does it work?

Let's go through the parts of the writer that are easy to get wrong.

### ContentGuid

`entryRow.ContentGuid = Guid.NewGuid()` sets a field that the content layer normally owns. It is what makes the row addressable as `IContent` afterward.

If we skip it, the catalog is perfectly happy with the entry, but the content model can't see it.

### Nulls in both directions

Strongly typed `DataSet` columns reject a null assignment, so we need to call the generated `SetXxxNull()` instead.

Reading is worse. Reading a NULL column throws `StrongTypingException` instead of returning null, so `?? string.Empty` on the property does nothing: the exception fires before the `??` is ever reached.

That's why I guard reads with `IsXxxNull()`.

### The new entry id

`entryRow.CatalogEntryId` is often still 0 after `SaveCatalogEntry`, so we need to re-read it.

For that I use `CatalogEntryInfo`, the cheapest response group there is. The full one we used for the lookup isn't needed here.

### The meta class

`MetaClass.Load(metaDataContext, nameof(Product))` keeps the DTO layer and the content layer describing the same thing. Our `[CatalogContentType]` class defines the properties, the content-type sync provisions them as MetaDataPlus fields at startup, and then the writer sets them by `nameof`.

The usual warning is "rename the class and the import stops finding its own fields". In my opinion it needs a small correction: it only holds while `MetaClassName` on the attribute is left unset.

If we set it, as the benchmark jobs in Part 5 do, the meta class is named by the attribute and renaming the type is harmless. It is good to know which of the two we rely on.

### Caching the meta class

The meta class is resolved once per writer instance. That is once per run only if the writer lives for one run. If the writer is a singleton, the definition stays pinned for the whole process lifetime (and the known issues in Part 4 show another problem with singleton writers).

My reason for caching it isn't the obvious one. You might expect meta classes to stay fixed while the process is up, but they don't: Commerce Manager and the MetaDataPlus admin pages can add or drop fields on a live context while the import is running.

Caching pins the definition for the writer's lifetime, so a schema change landing at row 120,000 can't silently split our feed into rows written against two different shapes.

### The DTO tracks its own changes

`SaveCatalogEntry(existing)` inspects `DataRow.RowState` and issues statements only for dirty rows.

That's why the update pattern is: load the DTO, change it, and save it. It is also why every assignment is guarded with `if (item.X is not null)`. A partial payload becomes a partial update, and we don't null out every column the feed didn't send that night.

One thing matters later: skipping clean rows doesn't mean skipping the call. Calling it on a completely unchanged DTO still costs a round trip, a transaction scope, and the cache work. That's why "don't save an unchanged entry" is on the fix list in Part 4.

### Response groups

`CatalogEntryFull | Variations` is what makes an update expensive. However, the update path needs the variation row (it writes `Weight`), and a nightly feed is mostly updates.

Probing with a cheap group and re-reading on a hit would add a round trip to the common case to save one on the rare case.

So the read should be sized for the path we take most often, and we should be able to say why. I sized mine for updates.

### Inject both statics

`CatalogContext.Current` and `CatalogContext.MetaDataContext` are the statics we see everywhere. Injecting only the first one moves the wall a few lines down and fixes nothing, so both belong in the constructor.

The gain is limited: the validation branches become unit-testable, where before nothing was.

However, `MetaClass.Load` is still a static that does I/O, `MetaClass` and `MetaObject` are concrete types with no interface behind them, and the change notifier is reached statically too.

Getting the write paths under test needs our own `IMetaClassProvider` and a sink interface. That is a bigger change than this post makes, and it is better to know about it before starting than halfway in.

### Why a `ServiceAccessor` for `MetaDataContext`?

Injecting `MetaDataContext` looks like a no-op refactor, but it isn't one.

`CatalogContext.MetaDataContext` is a memoized instance, keyed by connection string and shared by everything in the process. `MetaDataContext` itself is registered as transient, so `ctor(MetaDataContext)` gives our writer a private one.

That private one has its own MetaDataPlus cache and, more importantly, its own `Language`. That property decides which language row `SetMetaField` writes to.

If we capture one in a writer registered as a singleton, we have changed behavior while thinking we only tidied away a static. Of all the traps in this post, this is the one I'd most expect to get through code review unnoticed.

`ServiceAccessor<MetaDataContext>` resolves per call. Optimizely's own `CatalogMetaObjectRepository` takes it too, for the same reason.

### Moving an entry between categories

It takes four steps: delete the primary relation, save, re-read the DTO, and add the new one. The `DataSet` can't hold a deleted primary row and its replacement at the same time.

I added a short-circuit for when the entry is already in the right place. Over a quarter of a million rows that's most of them, and each time it saves three round trips: the delete-and-save, the re-read, and the add-and-save.

A move that really happens costs four calls, and one that doesn't costs one.

### Cleanup on the create path

The create path is the one place where all-or-nothing is right.

The entry row commits before the node relation, meta fields, and prices do. If any of those throw, a half-created entry with no category and no price is worse than no entry, so I delete it.

A failed rollback becomes an `AggregateException` naming both causes, because "the cleanup also failed" is a different incident from "the write failed".

### The orphan hole

One hole stays open. Rolling back needs the entry id, and occasionally the id isn't handed back after the save and the re-read can't identify the row.

In that case the writer refuses to guess. It won't pass an unverified id to a recursive delete, so it reports a failure and leaves the row alone.

That leaves an orphan: an entry with no category, no meta fields, and no price, and no event raised for it, so nothing downstream will ever hear about it.

Why is it acceptable? It is rare, and it usually fixes itself. The writer upserts on SKU, so the next run finds that row, takes the update path, and fills in what was missing.

I say "usually" because there is one gap. If the SKU next arrives with IsActive false, the writer takes the deactivate path, which only flips a meta flag. The entry stays uncategorized and unpriced until it comes back active.

In my opinion it isn't worth building a second recovery mechanism for it.

### One retry

`SqlException { Number: 547 }` naming the node-entry FK means that a cached category id outlived its category. Someone deleted it, or the database was restored under a running app.

In that case we invalidate it and try once more. This is a correctness fix for one exact FK. It doesn't retry transient failures, and Part 3 comes back to why that gap matters.

### The supporting types

The writer uses the small types introduced along the way. Here they are in one file, so the snippet compiles:

{% include code-modal.html
   id="2026-10-07-WriterContracts"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-07-WriterContracts.cs"
%}

Let's put it next to `CatalogEntryWriter`. Both build against a clean Commerce 15 project (I checked them before publishing).

I made one difference from the listing above on purpose: in the contracts file `Product` is a plain class instead of a `[CatalogContentType] VariationContent` subclass. Because of that it also declares `DisplayName` itself, since there is no `EntryContentBase` to inherit it from. The writer only reaches it through `nameof`, and a live catalog content type in a set of article snippets would provision a meta class in your Commerce database the moment you pressed F5.

In your own solution it is the real content type. The only thing that won't work against the stand-in is the `GetDefault<...>()` call from Part 1, which needs a real one.

Part 3 needs this file too: the bulk importer uses `ProductImportItem`, `WriteOutcome`, `ExceptionTree`, and `ICatalogEntryWriter` from it.

## What's next?

The writer handles one row. It doesn't decide what happens when row 148,213 has a missing price and 249,999 other rows are waiting behind it. On this project, answering that was a stated product requirement.

[Part 3]({% post_url 2026-10-16-surviving-a-dirty-feed-part-3-error-handling-and-event-batching %}) covers the per-row failure report, why I chose to commit each row on its own with no run-wide transaction, and how to raise one catalog event per batch instead of a quarter of a million of them.

## Summary

The upsert is long, and almost all of the extra length is work the content layer used to do for us. What do we need to take care of when writing an entry through the DTO API?

- `ContentGuid`, or the row lands in the catalog and the content model can't see it
- Nulls in both directions: `SetXxxNull()` to write one, `IsXxxNull()` to read one without a `StrongTypingException`
- The new entry id, because `SaveCatalogEntry` often leaves it at 0 and we have to re-read it
- The meta class as the seam between our `[CatalogContentType]` and MetaDataPlus, named by `nameof` unless `MetaClassName` says otherwise
- Every assignment and every save, since the DTO tracks its own changes but still costs a round trip for a clean one
- Cleanup on the create path, the one place in the writer where all-or-nothing is the right answer

Get those six right and one row goes in correctly. How fast it goes in is the subject of Part 4.

One hole stays open by design: when the id can't be verified, the writer leaves an orphan behind instead of deleting the wrong row, and the next run usually fixes it.

That's it! Have you hit a DTO-layer trap I haven't listed here? Let me know in the comments!

Thank you for reading, and stay tuned for Part 3.

## This Post is Part of a Series

- [Part 1: Why the DTO API]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %})
- Part 2: The Writer - (this post)
- [Part 3: Error Handling and Event Batching]({% post_url 2026-10-16-surviving-a-dirty-feed-part-3-error-handling-and-event-batching %})
- [Part 4: Counting Round Trips]({{ '/2026/10/21/the-low-level-api-is-not-automatically-fast-part-4-counting-round-trips/' | relative_url }})
- Part 5: Process Uptime and the Benchmark - coming soon
