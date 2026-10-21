---
layout: post
title:  "The Low-Level API Is Not Automatically Fast. Part 4: Counting Round Trips"
description: "The low-level Commerce DTO API is not automatically fast: counting the database round trips per imported row, and four fixes I would apply first."
date:   2026-10-21 10:00:00 +0200
# Pinned so dateModified cannot precede datePublished on a scheduled post.
last_modified_at: 2026-10-21 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
redirect_from:
  - /2026/10/21/The-Low-Level-API-Is-Not-Automatically-Fast-Part-4-Counting-Round-Trips.html
  - /2026/10/21/the-low-level-api-is-not-automatically-fast-part-4-counting-round-trips.html
image:
   path: assets/img/2026-10-21-the-low-level-api-is-not-automatically-fast-part-4-counting-round-trips.png
   alt: "The Low-Level API Is Not Automatically Fast. Part 4: Counting Round Trips"
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

In [Part 3]({% post_url 2026-10-16-surviving-a-dirty-feed-part-3-error-handling-and-event-batching %}) we made our importer correct: every row committed on its own, a report of every failed row, and one catalog event per batch. Unfortunately, it was still far too slow.

In this part I will count what really happens in the database for each imported row, and show four fixes that I would apply first.

## How slow is it?

On a restored production database our import processed 500 products in 1.73 minutes. That is roughly 5 rows per second, so our 252,670-row file would run for about 14 and a half hours.

Switching to the low-level DTO API removed the content pipeline. However, the import was still too slow. Most of the time now goes to database round trips.

The benchmark in Part 5 gives 3.8 to 4.3 rows/s on the DTO side, which looks very close.

The two runs are set up differently, though (this one wrote prices against a full production catalog, the benchmark had prices off and a much smaller node). So the only thing I take from them together is the order of magnitude.

A simple DTO importer runs at a few rows per second, and that is enough for planning.

## Where does the time go?

Let's count the round trips on the update path in the first version of my importer (the one measured above). It is the most common path, since a nightly feed is mostly re-writes:

| Step | Round trips |
| ---------- | ---------- |
| `GetCatalogEntryDto(sku, Full \| Variations)` | 1 |
| Category resolve | 0 if cached, 1 if not |
| `GetCatalogRelationDto` (+3 more on an actual move) | 1–4 |
| Price write | 1 transaction, plus cache purge and a remote event |
| `SaveCatalogEntry` (called every time) | 1 |
| Meta object load (uncached) + persist | 2 |

That gives us six to ten round trips per row, or eight to twelve if we count the price write's transaction, cache purge, and remote broadcast as three separate pieces of work.

With the latency of a cloud-hosted database, these round trips are most of the runtime.

## Four fixes

Here are the fixes in the order I would apply them.

### 1. Use the batch price overload

`IPriceService.SetCatalogEntryPrices` in its single-key form (taking one `CatalogKey`) opens its own transaction, purges caches, replicates to the price-detail tables, and broadcasts a price-updated event for every SKU.

The `IEnumerable<CatalogKey>` overload does all of it once per batch, so 500 transactions and 500 broadcasts become one.

Looking at the amount of work, this should be the biggest win of the four, and it is only a signature change plus a buffer. I haven't measured this fix though. Price writing was enabled only in the production code, and every benchmark in this series (Part 1 and Part 5) ran with prices switched off.

### 2. Don't save an unchanged entry

`SaveCatalogEntry(existing)` is called even when no rows are dirty.

We already have `DataRow.RowState`, so let's check it before saving. The primary-node short-circuit in the [Part 2]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %}) writer is the same idea one level down, so it is worth extending.

### 3. Watch out for quadratic category creation

If you create categories from the feed (my importer has a category writer that this series doesn't show), you need to give each new node a unique URI segment. My helper did it by reading every sibling under the parent, per language, for each created category. That makes creating N categories under one parent O(N²) in database work.

Commerce doesn't do this - y helper did. However, it is the most obvious way to write such a check, so I'm sure I'm not the only one who wrote it.

The fix is simple: resolve the sibling set once per parent and keep it.

### 4. Know what `ReadMode.UnCached` costs

My importer loads the meta object with `ReadMode.UnCached`. It is a correctness trade-off: a cached meta object can lose a concurrent write.

The price is one database hit per row, every time. I keep it, but it is good to know that it is part of our minimum cost per row.

None of these fixes require going back to `IContentRepository`. Choosing the low-level API was only the first step of the performance work.

## Known issues

Let's go through the things that cost me the most time. None of them looked like a problem at first.

All of them are bugs in my own importer, and Optimizely itself wasn't the problem. I list them because they are easy to write, so you might write them too.

### The job shows "Unresponsive", but the process restarted

- Read the cause carefully, because I had it backwards for a while: a job that is only slow never shows "Unresponsive". The scheduler's liveness ping runs on its own timer, independent of the job itself.
- The flag means that the process stopped responding (a recycle, an out-of-memory crash, or thread-pool starvation) and left `IsRunning` set.
- In my case the importer buffered the whole 200 MB CSV before writing a single row: a `MemoryStream`, a `ToArray()` copy, a UTF-8 validation pass over the whole array, and then a full list of parsed rows.
- By my calculation that is well over a gigabyte, most of it on the large object heap. I estimated it from the allocations and didn't confirm it with a profiler, so treat out-of-memory as the main suspect only.
- The fix: we need to stream the file.

### A file that rejects every row takes longer than one that imports every row

- My job base class wrote each log line in its own `DbContext` with its own `SaveChanges`, so every log line was a round trip.
- Every rejected row was a log line, so a fully rejected file became a storm of inserts, one per row, on the job thread.
- A standard job logging through `ILogger` doesn't have this cost.
- Either way, we should limit how many rejections we log one by one (the importer from Part 3 caps it at 200).

### All rows rejected, and it looks like a hang

- Case-sensitive parsing of a boolean column. A spreadsheet exporting `TRUE` instead of `true` rejects the whole file, which then causes the problem above.
- We should parse such columns tolerantly.

### The site is slow after a successful import

- In the first version of my importer, the category resolver flushed the whole Commerce node cache globally on invalidate, and the import called it at both the start and the end of the run.
- Every request afterward ran on a cold cache, exactly when the database could use a break the most.
- We should invalidate only the keys we touched.

### Two imports give a result that neither would give alone

- I registered the writers as singletons holding per-run caches, and nothing stopped a scheduled job and an API call from running at the same time.
- Check how you registered yours, and if they hold state, make sure only one import runs at a time.

### A column is nulled by a partial feed

- An unguarded field assignment set a column to null when the feed didn't send it.
- We should guard every field assignment, so only the fields the feed sends are written.

### Nobody downstream hears about a write

- A write happened outside the event scope, so no catalog event was raised for it.
- We should make sure every write happens inside the event scope.

## What's next?

All of the above is worth doing, but there is a bigger problem.

While measuring the other fixes I ran the same benchmark twice on the same machine and got results that differed by a factor of eight. The only thing that changed was how long the application had been running. [Part 5]({{ '/2026/10/28/what-mattered-more-than-the-api-part-5-process-uptime-and-the-benchmark/' | relative_url }}) is about that finding, and it comes with benchmark jobs that you can run on your own catalog.

## Summary

Switching to the low-level API removed the content pipeline, but we still have six to ten round trips per row on the common update path. What can we do about it?

- Expect a few rows per second from a simple DTO importer. That is the order of magnitude both of my measurements support.
- Start with the price write. The per-SKU overload opens a transaction, purges caches, and broadcasts for every row, and the `IEnumerable<CatalogKey>` overload does it once per batch.
- Skip unchanged entries, since `SaveCatalogEntry` costs a round trip even when nothing is dirty.
- Resolve the sibling set once per parent, otherwise a uniqueness check written the obvious way becomes quadratic.
- Keep `ReadMode.UnCached` for correctness, and count its one database hit per row as part of your minimum cost.

That's it for the round trips! Have you counted them on your own importer? Let me know in the comments!

Thank you for reading, and stay tuned for Part 5.

## This Post is Part of a Series

- [Part 1: Why the DTO API]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %})
- [Part 2: The Writer]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %})
- [Part 3: Error Handling and Event Batching]({% post_url 2026-10-16-surviving-a-dirty-feed-part-3-error-handling-and-event-batching %})
- Part 4: Counting Round Trips - (this post)
- [Part 5: Process Uptime and the Benchmark]({{ '/2026/10/28/what-mattered-more-than-the-api-part-5-process-uptime-and-the-benchmark/' | relative_url }})
