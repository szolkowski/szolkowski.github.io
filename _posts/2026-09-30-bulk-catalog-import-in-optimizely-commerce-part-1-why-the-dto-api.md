---
layout: post
title:  "Bulk Catalog Import in Optimizely Commerce. Part 1: Why the DTO API"
description: "Why bulk catalog writes in Optimizely Commerce belong on the DTO API rather than IContentRepository — measured 2.7-3.3x faster, and what it costs you."
date:   2026-09-30 10:00:00 +0200
# Pinned so dateModified cannot precede datePublished on a scheduled post.
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

A while back I wrote about [reading a large catalog without running out of memory]({% post_url 2026-02-18-memory-efficient-catalog-traversal-in-optimizely-commerce-part-1-building-the-service %}). This post is about the other direction, which turns out to be the harder one: **writing** a quarter of a million products into Optimizely Commerce, nightly, from a PIM feed you don't control.

The project I have in mind receives a product export that started at 252,670 rows and later grew past a million. It arrives as a CSV, or in 50-item chunks over an API, and it's exactly as clean as any feed produced by a system somebody else owns—which is to say, some rows are wrong. A missing price here, an unescaped quote there, a category that doesn't exist yet.

Two requirements fall out of that, and they were both written down before a line of code was:

1. The write path has to be **fast enough** that a full feed finishes overnight.
2. **Commit record by record, and return a report of every row that failed, with the reason it failed.** Not "the import succeeded" or "the import failed"—a per-row verdict. A run that writes 249,500 rows and hands back a list of the 500 it could not write, each with its reason and its line in the file, is a success. A run that stops at row 148,213 because one price was missing is a failure, even though it hit a genuine problem.

That second one is worth dwelling on, because it's a *product* requirement rather than an engineering preference. Somebody has to fix the feed, and that somebody doesn't have access to your logs. What they need is a list they can act on. Everything in Part 3 exists to produce that list.

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

This is correct, readable, and exactly what you want when an editor changes one product in the UI. It's also doing a great deal of work you did not ask for. Per row, `Save(…, SaveAction.Publish)`:

- writes a **new content version** (and, on an update, supersedes the previous one);
- runs the **publish pipeline**—validation, access checks, property save handlers;
- fires **`IContentEvents`**, which your own subscribers and half the platform are listening to;
- invalidates content caches, locally and across instances;
- queues the item for **indexing**.

For one editorial change that's the whole point. For 250,000 rows of machine-generated feed data it's 250,000 version rows describing a history nobody will ever open, plus 250,000 trips through a pipeline whose every stage is irrelevant to a row that came out of a spreadsheet.

Commerce has a second, older door into the same tables, and for bulk writes it's the right one.

## Two APIs, One Catalog

Underneath the content layer, catalog entries and nodes are ordinary SQL rows, reached through `Mediachase.Commerce.Catalog`—`CatalogContext.Current`, strongly typed `DataSet`s (`CatalogEntryDto`, `CatalogNodeDto`, `CatalogRelationDto`), and `MetaDataPlus` for the custom fields. This is the API Commerce Manager was built on. It predates the content model, it is still present and un-deprecated in Commerce 15—nothing in my solution suppresses an `[Obsolete]` to use it—and Commerce Manager still runs on it.

| | `IContentRepository` | `ICatalogSystem` DTO API |
| ---------- | ---------- | ---------- |
| What it writes | a content version **and** the catalog rows | the catalog rows |
| Version history | a new version per save | none—rows are updated in place |
| Publish events | `IContentEvents` fire | no `IContentEvents`, no catalog event—but see the note below |
| Content-type validation | runs | doesn't exist |
| Access checks | enforced | you're on your own |
| Search / Graph indexing | triggered by publish | **verify this yourself**—see below |
| Good for | an editor changing one product | a feed changing 250,000 |

### So How Much Faster Is It, Actually?

I got tired of seeing this asserted and never measured, so I measured it. Two scheduled jobs, the same catalog content type, the same generated rows, the same batch loop, the same throwaway category—the only difference is what happens inside one row's write. Both jobs are in Part 5; run them and you'll get a number that is true for *your* database rather than mine.

Writing 5,000 variants into an empty category, prices excluded on both sides. I ran the whole thing four times over five days, because the first result deserved a second opinion and the second one disagreed with the first:

| 5,000 rows | Content API | DTO API | Ratio (total) | Ratio (median batch) |
| ---------- | ---------- | ---------- | ---------- | ---------- |
| **Pair A**—31 Aug | 64.05 min | 19.59 min | **3.27×** | 3.15× |
| **Pair B**—1–2 Sep | 59.92 min | 21.87 min | **2.74×** | 2.72× |
| **Pair C**—3–4 Sep | 64.66 min | 21.28 min | **3.04×** | 3.08× |
| **Pair D**—4 Sep | 62.11 min | 22.03 min | **2.82×** | 2.90× |

**Call it three times faster. Not a hundred.** I'm quoting it plainly because the folklore around this API is wilder than the measurement, and a 3× claim you can reproduce is worth more than a 100× claim you cannot. Four independent pairs landed between **2.74× and 3.27×**, averaging just under 3×. Treat that spread as the result: no single figure here's a constant, and if I had run it once I would have published whichever number I happened to get.

Pairs C and D are the ones I would trust most, and it is worth saying why: they are the only pairs where **both** arms were started on a freshly restarted application. In Pair A neither was, and in Pair B only the DTO arm was—which, as Part 5 explains at some length, turns out to matter more than anything else I measured. All four pairs were nonetheless run on comparatively young processes, which is why they agree as closely as they do.

One caution about reading the table: within a pair, total and median are not two results. Throughput is the total over the row count, and the batch times all but sum to the total—3,842.6 s of batches against a recorded 3,843.2 s in Pair A, the 0.6 s gap being the per-batch progress write, which sits inside the run clock but outside the batch clock—so the median is a second *statistic* on one dataset. It tells you no single outlier batch is dragging the mean, and nothing more. The replication that counts is having four pairs, not having two numbers per pair.

Read the absolute figures with care. This was a Debug build on a developer machine against a local SQL Server, which inflates both paths; the **ratio** is the transferable part, not the rows per second. And prices were deliberately excluded from both sides, because `IPriceService` is the same call either way—turning it on adds the same constant to both and pushes the ratio *down*, closer to 1. If most of your per-row cost is the price write, the layer you write entries through matters proportionally less.

**One asymmetry I could not remove, and it probably favours the DTO side.** My DTO job writes no SEO row for the entries it creates—that much I can state flatly, it is my code. The other half is unverified: whether `Save(…, Publish)` writes one for you when you never populate `SeoInformation`. Treat it as an open question with a direction. If the content path does write a URL segment per entry, then it is doing work the DTO path skips, the two paths are not producing equivalent entries, and the ratio above is a **ceiling** rather than a midpoint for anyone whose catalog needs URLs. Worth ten minutes with a row counter before you lean on the number.

So the case for the DTO path is not that it is dramatically faster. It is that it is meaningfully faster *and* it stops writing a version row per product you never intend to version—with everything in the next section as the price.

### What You Give Up

This is the part that belongs in the same breath as the speed argument, because it is a real trade and I have watched people discover it the expensive way.

- **No version history means no rollback.** Content-version rollback is the obvious recovery mechanism for "the feed was wrong last night", and it simply doesn't apply to rows that never became a content version. Catalog export/import is a bulk mechanism, not a point-in-time restore. If you need a lever, you have to build one—a snapshot, a reversible feed, something.
- **No publish events means nothing downstream hears you.** If your front end revalidates its cache off `IContentEvents.PublishedContent`, an import writes 250,000 rows and purges nothing. Anything hooked to publishing has to be re-hooked to whatever you raise instead. One qualification, because "nothing fires" is not quite true of the whole write: **`IPriceService` still broadcasts, once per SKU.** Setting prices purges caches, replicates to the price-detail tables and raises a price-updated event which, on a multi-instance setup, is a message on the bus. So the entry write is silent and the price write is not—and that asymmetry is exactly why the price call turns up as the top item on the fix list in Part 4.
- **Search almost certainly goes stale, and you should confirm exactly how stale.** Follow the chain: the Graph CMS integration indexes off content events, the DTO path raises none, so nothing pushes these writes into the index as they happen. Scanning the Graph assemblies in my own solution, I found nothing subscribing to the Commerce catalog events either. The remaining mechanism is the scheduled indexing job—which would mean your catalog is stale in search for up to one job interval after every import. That is a conclusion from evidence rather than a measurement: I have not timed a write end-to-end and I'm not going to present a deduction as a stopwatch. Do that timing on a test environment before you rely on it, because if it holds, "search is up to an interval behind the catalog" is a fact your business stakeholders need before go-live, not after.
- **No validation.** No `[Required]`, no `IValidate<T>`, no content-type rules. A missing mandatory field becomes a `NULL` column, not a rejected save. Every check you care about is now yours to write—which is the subject of Part 3.

## What's Next?

That's the case for the DTO API and the bill that comes with it. It doesn't tell you how to actually write a row through it, and the code is longer than the content-API version for good reasons. In Part 2 I'll walk the full upsert: `ContentGuid`, the strongly typed `DataSet` traps, the meta class that bridges the two layers, and a create path that cleans up after itself.

Do you have measurements from your own catalog that disagree with mine? Let me know in the comments!

Thank you for reading, and stay tuned for Part 2.

## This Post is Part of a Series

- Part 1: Why the DTO API - (this post)
- Part 2: The Writer - coming soon
- Part 3: Error Handling and Event Batching - coming soon
- Part 4: Counting Round Trips - coming soon
- Part 5: Process Uptime and the Benchmark - coming soon
