---
layout: post
title:  "What Mattered More Than the API. Part 5: Process Uptime and the Benchmark"
description: "A long-running app imported 7x slower than a freshly restarted one, the finding that beat every API optimization, plus runnable benchmark jobs."
date:   2026-10-28 10:00:00 +0200
# Pinned so dateModified cannot precede datePublished on a scheduled post.
last_modified_at: 2026-10-28 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
redirect_from:
  - /2026/10/28/What-Mattered-More-Than-the-API-Part-5-Process-Uptime-and-the-Benchmark.html
  - /2026/10/28/what-mattered-more-than-the-api-part-5-process-uptime-and-the-benchmark.html
image:
   path: assets/img/2026-10-28-what-mattered-more-than-the-api-part-5-process-uptime-and-the-benchmark.png
   alt: "What Mattered More Than the API. Part 5: Process Uptime and the Benchmark"
   width: 1200
   height: 630
primary_tag: commerce
tags:
- episerver
- optimizely
- commerce
- catalog
- performance
- benchmark
- import
- .NET
---

[Part 4]({% post_url 2026-10-21-the-low-level-api-is-not-automatically-fast-part-4-counting-round-trips %}) counted the round trips behind each imported row and listed four fixes worth making. In this part I will show the finding that turned out to matter more than all of them, and give you the benchmark jobs so you can check it on your own catalog.

## What mattered more than the API?

Partway through this work I re-ran the DTO benchmark and it came back eight times slower than the first time. 15.50 minutes per batch of 500, against 1.96 the run before.

Same code, same machine, same category, same row count.

For a while I assumed I had broken something, but the only thing different was that the application had been running overnight.

So I restarted it and ran the identical job again. Here is every DTO run of the 5,000-row volume, with the pairs from [Part 1]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %}), sorted by how long the application had been up:

| DTO, 5,000 rows | Process uptime at start | Median batch of 500 |
| ---------- | ---------- | ---------- |
| Pair C, fresh restart | ~10 minutes | 2.10 min |
| Pair D, fresh restart | ~10 minutes | 2.16 min |
| Pair B, fresh restart | ~10 minutes | 2.18 min |
| Pair A | ~1h 45m | 1.96 min |
| After running overnight | a full night | 15.50 min |

A long-running process imported seven times slower than a freshly started one.

The effect is uneven, though.

The 1h 45m run is the fastest of the five, a little quicker than any of the three fresh starts, and those three land within 4% of each other.

At the scale of an hour or two this is all noise, but after a full night it is obvious.

There is also a condition on the number from Part 1. In that overnight run the low-level DTO API was slower than `IContentRepository`.

It took 15.50 minutes per batch. The content path in Pair A managed 6.15.

The 2.7-3.3x advantage I measured holds for a process that has not been up very long. I would never have found that out if I had run the benchmark once and published.

### Is it uptime, or the growing node?

Once we look for it, there is drift inside every session I ran, on both APIs. In Pair C the content path went from 6.48 to 9.94 min per batch across its three volumes, and the DTO path went from 2.10 to 3.17.

Within one session I cannot separate uptime from the growing number of children under the node, so I will not guess.

The overnight case gives us a clean comparison.

Both the overnight run and the fresh run worked a category holding about 12,000 children. There the overnight run took 17.64 minutes per batch, and the fresh run took 2.51.

Same node size, seven times apart. The growing node is not what did this.

Population does cost something. In the fresh run, going from 5,200 children at 2.18 min per batch to 12,200 at 2.51 cost us about 15%, which is nowhere near a factor of seven.

### What to do about it

I have no answer for the cause.

Managed heap growth, cache pressure, connection-pool state, and accumulated ambient state are all plausible, and I have not separated them.

The operational part is easy to act on:

- Benchmark on a freshly started process. Otherwise the numbers tell you how long the application has been up.
- If a nightly import is slower than the same import run by hand, check how long the app pool has been up before you look at your import. In production an app pool easily stays up for days. Mine was slow the morning after.
- Recycling before a large import is a one-line change, and on this evidence it is worth more than any single fix from Part 4.

I spent a whole post counting round trips per row, and restarting the process first beats all of it.

## Measure it on your own catalog

Let's look at the benchmark, so you can check the numbers on your own data.

It is three scheduled jobs: one per API, plus a cleanup job.

Each import job writes 5,000, then 50,000, then 500,000 generated products into the same throwaway category.

Same content type, same generated rows, same batch loop, so the only difference is what happens inside one row's write.

### How to run it

There is one category, shared by both import jobs, so we run the cleanup job between the two passes.

Without it the second job starts against the first job's rows.

And we restart the application before each pass. See the section above for why.

The code below is the code that produced Pair D, unchanged.

Check the run header the jobs print. It records the catalog id and language each arm resolved, so we can confirm both halves are writing the same thing.

Per volume, the report gives us the total elapsed time, rows per second, and the per-batch distribution: average, median, fastest, and slowest.

The average tells us what the import costs. The gap between the average and the median tells us whether one 8-second GC pause is moving our mean.

First the shared half: the content type, the data generator, the timing, the self-creating category, and a cleanup job.

{% include code-modal.html
   id="2026-10-28-CatalogImportBenchmark-Shared"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-28-CatalogImportBenchmark.Shared.cs"
%}

Now the two jobs. First the content-API one:

{% include code-modal.html
   id="2026-10-28-ContentApiImportBenchmarkJob"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-28-ContentApiImportBenchmarkJob.cs"
%}

And now the DTO one:

{% include code-modal.html
   id="2026-10-28-DtoApiImportBenchmarkJob"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-28-DtoApiImportBenchmarkJob.cs"
%}

These three files stand alone, so we don't need the writer or the importer from Parts 2 and 3. There is one line in the DTO job to check before you build. It injects `CatalogMetaObjectRepository`, which lives in `Mediachase.MetaDataPlus.Internal`.

That is the supported way to do an uncached per-language meta read, and it is what my own project uses. However, `Internal` carries no compatibility guarantee between minor versions, so if your Commerce version has moved it, that is the line to adjust.

Key points:

- Warm up with a couple of hundred throwaway rows, or the first path we measure looks slow
- Use a fresh SKU prefix per volume, or we are measuring updates
- Everything that is not the write happens outside the measurement
- There is a one-hour budget per volume, and the report extrapolates from what completed
- Results go to disk after every volume
- Prices are off by default, since they are the same call on both paths
- Clean up with the third job

The warm-up absorbs JIT, connection-pool ramp-up, and the cold metadata cache.

A 500,000-row content-API pass can run for many hours, which is why there is a budget per volume.

One of my arms is weaker than it looks, though. I stopped the content-API 50,000 volume in the first run by hand after four batches, at 33 minutes instead of the full hour.

Read the projection in the report as a floor, because it assumes the rate holds, and the rate falls.

Each volume is appended to a readable `.txt` file and to a `volumes.csv` we can chart.

So if the 500,000-row pass dies to a deploy/recycle, the 5,000 and 50,000 numbers are already safe.

Deleting half a million entries one by one is its own slow job.

Run it on a development or staging copy. It writes real catalog rows, and the content-API half leaves a content version behind for every one.

The job counts the versions on one imported entry at the end. It will always say 1, because every volume writes fresh SKUs, so nothing here is ever an update. The number that matters is what accumulates across nightly re-imports of the same SKUs, and we can see that by re-running a volume with the previous prefix.

## Summary

Part 1 made the case for the DTO layer and priced it, and in Parts 2 and 3 we filled in two of those costs, the validation and the events.

Around that writer, the pattern that made a quarter of a million rows survivable was:

- We commit record by record, with no run-wide transaction, so one bad row can't roll back the good ones.
- We return a report of every failed row and its reason. A partial run is still a successful run.
- Use three layers of per-row failure handling, where an expected failure is a returned result and an unexpected one is an exception.
- We carry both the natural key and the physical row number on every rejection, counted and reported in one place.
- We upsert on the feed's own key, so our replay is safe without tracking runs.
- We raise one coalesced catalog event per batch instead of one per row.
- Count the round trips afterwards. Picking the fast API only bought me a floor of a few rows per second.
- Restart the application before a big import. That mattered more than any of the fixes above.

That's it for the series!

If you have timed a DTO-layer write reaching Graph, or found a push path I missed, I would like to hear it. I got as far as a well-supported deduction and stopped. Let me know in the comments!

Thank you for reading, and I hope this series saves you a night of watching a progress bar that is not moving.

## This Post is Part of a Series

- [Part 1: Why the DTO API]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %})
- [Part 2: The Writer]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %})
- [Part 3: Error Handling and Event Batching]({% post_url 2026-10-16-surviving-a-dirty-feed-part-3-error-handling-and-event-batching %})
- [Part 4: Counting Round Trips]({% post_url 2026-10-21-the-low-level-api-is-not-automatically-fast-part-4-counting-round-trips %})
- Part 5: Process Uptime and the Benchmark - (this post)
