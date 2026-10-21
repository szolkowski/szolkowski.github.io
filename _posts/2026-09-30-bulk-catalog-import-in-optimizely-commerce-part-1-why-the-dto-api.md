---
layout: post
title:  "Bulk Catalog Import in Optimizely Commerce. Part 1: Why the DTO API"
description: "Why I use the DTO API instead of IContentRepository for bulk catalog writes in Optimizely Commerce: measured 2.7-3.3x faster, and what it costs you."
date:   2026-09-30 10:00:00 +0200
last_modified_at: 2026-09-30 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
redirect_from:
  - /2026/09/30/Bulk-Catalog-Import-in-Optimizely-Commerce-Part-1-Why-the-DTO-API.html
  - /2026/09/30/bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api.html
image:
   path: assets/img/2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api.png
   alt: "Bulk Catalog Import in Optimizely Commerce. Part 1: Why the DTO API"
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

A while back I wrote about [reading a large catalog without running out of memory]({% post_url 2026-02-18-memory-efficient-catalog-traversal-in-optimizely-commerce-part-1-building-the-service %}). This series is about the other direction, which turns out to be the harder one: writing a quarter of a million products into Optimizely Commerce, nightly, from a PIM feed we don't control.

In this part I will explain why I use the low-level DTO API for it, how much faster it is, and what it costs.

## What is the problem?

My project receives a product export that started at 252,670 rows and later grew past a million. It arrives as a CSV, or in 50-item chunks over an API.

Like any feed from a system somebody else owns, some rows are wrong. A missing price here, an unescaped quote there, a category that doesn't exist yet.

Both requirements were written down before the first line of code:

1. The write path has to be fast enough that a full feed finishes overnight.
2. Commit record by record, and return a report of every row that failed, with the reason it failed.

The second one means a verdict per row. A run that writes 249,500 rows and gives back the 500 it couldn't write, each with its reason and its line in the file, is a success.

A run that stops at row 148,213 because one price was missing is a failure, even though the price really was missing.

This is a product requirement, and not just my preference as a developer. Somebody has to fix the feed, and that somebody doesn't have access to our logs.

They need a list they can act on, and producing it is the main topic of Part 3.

The answer to the first requirement is to stop using `IContentRepository`. Let's see why, and what it costs (most write-ups skip that part).

## What is wrong with the content API?

The obvious way to create a variant in Optimizely Commerce is the content way:

```csharp
// MyVariant is your own [CatalogContentType] VariationContent subclass
var variant = _contentRepository.GetDefault<MyVariant>(categoryLink);
variant.Code = "SKU-1234";
variant.Name = "Widget, 10 ml";
_contentRepository.Save(variant, SaveAction.Publish, AccessLevel.NoAccess);
```

This is correct, readable, and exactly what we want when an editor changes one product in the UI.

However, it does a lot of work we didn't ask for. For each row, `Save(…, SaveAction.Publish)`:

- writes a new content version (and on an update, supersedes the previous one)
- runs the publish pipeline: validation, access checks, and property save handlers
- fires `IContentEvents`, which our own subscribers and half of the platform are listening to
- invalidates content caches, locally and across instances
- queues the item for indexing

For one editorial change that is the whole point. For 250,000 rows of machine-generated feed data, it is 250,000 version rows of history nobody will ever open.

It is also 250,000 trips through a pipeline where no stage matters for a row that came out of a spreadsheet.

Commerce has a second, older door into the same tables, and for bulk writes it is the right one.

## Two APIs, one catalog

Under the content layer, catalog entries and nodes are ordinary SQL rows.

We reach them through `Mediachase.Commerce.Catalog`: `CatalogContext.Current`, strongly typed `DataSet`s (`CatalogEntryDto`, `CatalogNodeDto`, `CatalogRelationDto`), and `MetaDataPlus` for the custom fields.

Commerce Manager was built on this API and still runs on it.

It is older than the content model, and it is still present and not deprecated in Commerce 15 (nothing in my solution suppresses an `[Obsolete]` to use it).

| | `IContentRepository` | `ICatalogSystem` DTO API |
| ---------- | ---------- | ---------- |
| What it writes | a content version and the catalog rows | the catalog rows |
| Version history | a new version per save | none, rows are updated in place |
| Publish events | `IContentEvents` fire | no `IContentEvents`, no catalog event (see the note below) |
| Content-type validation | runs | doesn't exist |
| Access checks | enforced | you're on your own |
| Search / Graph indexing | triggered by publish | verify this yourself (see below) |
| Good for | an editor changing one product | a feed changing 250,000 |

## How much faster is it?

I kept seeing the speed-up claimed without numbers, so I measured it.

I used two scheduled jobs, one per API (the DTO-side job runs in three variants, details in Part 5). Both use the same catalog content type, generated rows, batch loop, and throwaway category. The only difference is what happens inside each row's write.

We will get to both jobs in Part 5, so you can run them and get a number that is true for your database.

Each run writes 5,000 variants into an empty category, with prices excluded on both sides. The DTO API column below shows one of the three DTO variants (details in Part 5).

I ran it four times over five days, because the first result deserved a second opinion, and the second one disagreed with the first:

| 5,000 rows | Content API | DTO API | Ratio (total) | Ratio (median batch) |
| ---------- | ---------- | ---------- | ---------- | ---------- |
| Pair A (31 Aug) | 64.05 min | 19.59 min | 3.27× | 3.15× |
| Pair B (1–2 Sep) | 59.92 min | 21.87 min | 2.74× | 2.72× |
| Pair C (3–4 Sep) | 64.66 min | 21.28 min | 3.04× | 3.08× |
| Pair D (4 Sep) | 62.11 min | 22.03 min | 2.82× | 2.90× |

So it is about three times faster, which is a lot less than the folklore says.

The four pairs landed between 2.74× and 3.27×, averaging just under 3×. I recommend treating that spread as the result.

No single figure here is a constant. If I had run it once, I would have published whichever number I happened to get.

### Which pairs can we trust most?

Pairs C and D, since they are the only ones where both arms started on a freshly restarted application.

In Pair A neither arm did, and in Pair B only the DTO arm did.

As Part 5 explains at some length, that matters more than anything else I measured.

Still, all four pairs ran on fairly young processes, which is why they agree so closely.

### Total vs. median

Within a pair, total and median are one result, two ways. Throughput is the total over the row count, and the batch times almost sum to the total.

In Pair A that is 3,842.6 s of batches against a recorded 3,843.2 s. The 0.6 s gap is the per-batch progress write, which is inside the run clock but outside the batch clock.

So the median is a second statistic on the same dataset. It only tells us that no single outlier batch is dragging the mean. The four pairs are what counts as replication here.

### What can we take from it?

This was a Debug build on a developer machine against a local SQL Server, which slows down both paths.

The ratio is the part worth comparing with your setup. The rows per second aren't.

Prices were excluded from both sides on purpose, because `IPriceService` is the same call either way. Turning it on adds the same constant to both and pushes the ratio down, closer to 1.

If most of your per-row cost is the price write, the layer you write entries through matters proportionally less.

So the DTO path is about 3× faster, and it stops writing a version row for every product we never intend to version. The price for that is the next section.

## What do we give up?

This belongs right next to the speed argument, because I have watched people discover it the expensive way.

### Rollback

Content-version rollback is the obvious recovery when "the feed was wrong last night", but it doesn't apply to rows that never became a content version.

Catalog export/import is a bulk mechanism, and it can't restore a point in time.

If we need a way back, we have to build one: a snapshot, a reversible feed, or something similar.

### Publish events

If your front end revalidates its cache off `IContentEvents.PublishedContent`, an import writes 250,000 rows and purges nothing.

Anything hooked to publishing has to be hooked to whatever we raise instead (Part 3 raises one catalog event per batch).

There is one exception: `IPriceService` still broadcasts, once per SKU. Setting prices purges caches, replicates to the price-detail tables, and raises a price-updated event, which on a multi-instance setup is a message on the bus.

So the entry write is silent, and the price write isn't. That is exactly why the price call ends up at the top of the fix list in Part 4.

### Search indexing

Search almost certainly goes stale, and we should confirm exactly how stale.

Let's follow the chain. The Graph CMS integration indexes off content events, and the DTO path raises none, so nothing pushes these writes into the index as they happen.

I scanned the Graph assemblies in my own solution and found nothing subscribing to the Commerce catalog events either.

What remains is the scheduled indexing job. That would mean the catalog is stale in search for up to one job interval after every import.

This is a conclusion from evidence, and I haven't measured it. I haven't timed a write end to end, and I won't present a deduction as a stopwatch result.

I recommend doing that timing on a test environment before you rely on it. If it holds, "search is up to an interval behind the catalog" is something your business stakeholders need to know before go-live.

### Validation

There is no `[Required]`, no `IValidate<T>`, and no content-type rules. A missing mandatory field becomes a `NULL` column instead of a rejected save.

Every check we care about is now ours to write. The writer's checks are in Part 2, and the failure report is in Part 3.

## What's next?

That's the case for the DTO API, and the price that comes with it. The code to write a row through it is longer than the content API version, for good reasons.

In [Part 2]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %}) I will walk through the full upsert: `ContentGuid`, the strongly typed `DataSet` traps, the meta class that connects the two layers, and a create path that cleans up after itself.

## Summary

For bulk catalog writes in Optimizely Commerce, the low-level DTO API is the right layer. It writes only the rows we asked for, with no version and no publish pipeline per row. Measured across four independent pairs, it is about 3× faster, and this is what we pay for it:

- 2.7–3.3× faster on wall-clock time, so about 3×. A nice win, but smaller than the folklore suggests.
- No version history, so no content-version rollback when last night's feed was wrong.
- No publish events, so nothing downstream hears the import unless we raise something ourselves.
- No content-type validation, so every check we care about is ours to write.
- An indexing question that you have to answer for your own solution.

Let's be clear about which of those four costs we are taking on. This series replaces two of them: validation (Parts 2 and 3) and the publish events (Part 3 raises catalog events per batch). Rollback and search indexing stay on your list.

That's it for the why! Do you have measurements from your own catalog that disagree with mine? Let me know in the comments!

Thank you for reading, and stay tuned for Part 2.

## This Post is Part of a Series

- Part 1: Why the DTO API - (this post)
- [Part 2: The Writer]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %})
- [Part 3: Error Handling and Event Batching]({% post_url 2026-10-16-surviving-a-dirty-feed-part-3-error-handling-and-event-batching %})
- [Part 4: Counting Round Trips]({% post_url 2026-10-21-the-low-level-api-is-not-automatically-fast-part-4-counting-round-trips %})
- [Part 5: Process Uptime and the Benchmark]({{ '/2026/10/28/what-mattered-more-than-the-api-part-5-process-uptime-and-the-benchmark/' | relative_url }})
