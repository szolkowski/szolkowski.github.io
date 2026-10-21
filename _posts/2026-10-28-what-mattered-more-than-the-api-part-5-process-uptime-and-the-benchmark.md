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

For a while I assumed I had broken something.

I had not. The only thing different was that the application had been running overnight.

So I restarted it and ran the identical job again:

| DTO, 5,000 rows | Process uptime at start | Median batch of 500 |
| ---------- | ---------- | ---------- |
| Pair C, fresh restart | ~10 minutes | 2.10 min |
| Pair D, fresh restart | ~10 minutes | 2.16 min |
| Pair B, fresh restart | ~10 minutes | 2.18 min |
| Pair A | ~1h 45m | 1.96 min |
| After running overnight | a full night | 15.50 min |

A long-running process imported seven times slower than a freshly started one.

Uptime is a messy dial, though.

The 1h 45m run is the fastest of the five, a little quicker than any of the three fresh starts, and those three land within 4% of each other.

At the scale of an hour or two this is all noise. The effect shows up at the scale of a working day, and then it is not subtle.

There is also a condition on the number at the top of this series. In that overnight run the low-level DTO API was slower than `IContentRepository`.

It took 15.50 minutes per batch, where the content path in Pair C managed 6.48.

The 2.7-3.3x advantage I measured holds for a process that has not been up very long. I would never have found that out if I had run the benchmark once and published.

### Is it uptime, or the growing node?

Once we look for it, there is drift inside every session I ran, on both APIs. In Pair C the content path went from 6.48 to 9.94 min per batch across its three volumes, and the DTO path went from 2.10 to 3.17.

Within one session I cannot separate uptime from the growing number of children under the node, so I will not guess.

The overnight case is different. In the fresh run the category went from empty to 5,200 children at 2.18 min per batch, then on to 12,200 children at 2.51. So 7,000 more rows under the node cost us about 15%.

The overnight run was sitting at 15.50 minutes per batch. Node population cannot produce a number that size.

I did not record how many rows were in the category when the overnight run started, so I can't give you a clean same-population pair. A 15% effect and a sevenfold collapse are still very different things.

### What to do about it

I have no answer for the cause.

Managed heap growth, cache pressure, connection-pool state, and accumulated `AsyncLocal` state are all plausible, and I have never separated them.

The operational part is easy to act on:

- Benchmark on a freshly started process. Otherwise the numbers tell you about uptime and not about your code.
- If a nightly import is slower than the same import run by hand, check how long the app pool has been up before you look at your import. In production an app pool easily stays up for days. Mine was slow the morning after.
- Recycling before a large import is a one-line change, and on this evidence it is worth more than any single fix from Part 4.

I spent a whole post counting round trips per row, and restarting the process first beats all of it.

## Measure it on your own catalog

Let's look at the benchmark, so you can check the numbers on your own data.

It is three scheduled jobs: one per API, plus a cleanup job. I ran the same DTO job three times to get a better average time. Each import job writes 5,000, then 50,000, then 500,000 generated products into the same throwaway category. Same content type, same generated rows, same batch loop, so the only difference is what happens inside one row's write.

### How to run it

There is one category, shared by both jobs, so we run the cleanup job between the two passes.

Without it the second job starts against the first job's rows.

And we restart the application before each pass. See the section above for why.

The code below is the code that produced Pair D, unchanged. Check the run header the jobs print.

It records the catalog id and language each arm resolved, so we can confirm both halves are writing the same thing.

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

**Key Points:**

- Warm up with a couple of hundred throwaway rows, or the first path we measure looks slow
- Use a fresh SKU prefix per volume, or we are measuring updates
- Everything that is not the write happens outside the measurement
- There is a one-hour budget per volume, and the report extrapolates from what completed
- Results go to disk after every volume
- Prices are off by default, since they are the same call on both paths
- Clean up with the third job

The warm-up absorbs JIT, connection-pool ramp-up, and the cold metadata cache.

The one-hour budget is why the bigger volumes stop early, and why our node stopped at about 12,000 children. A 500,000-row content-API pass can run for days.

Read the projection in the report as a floor, because it assumes the rate holds, and the rate falls.

Each volume is appended to a readable `.txt` file and to a `volumes.csv` we can chart.

So if the 500,000-row pass dies to a recycle, the 5,000 and 50,000 numbers are already safe.

Run it on a development or staging copy. It writes real catalog rows, and the content-API half leaves a content version behind for every one.

The job counts the versions on one imported entry at the end.

## Summary

For bulk catalog writes in Optimizely Commerce, the low-level DTO API is the right layer. It writes the rows we asked for and nothing else, with no version per row and no publish pipeline per row.

Measured across four pairs that is worth 2.7-3.3x, so let's call it 3x. What we give up:

- no rollback lever
- no events unless we raise them
- no validation unless we write it
- an indexing question we have to answer for our own solution

Around that writer, the pattern that made a quarter of a million rows survivable was:

- Commit record by record, with no run-wide transaction, so one bad row can't roll back the good ones.
- Return a report of every failed row and its reason. A partial run is still a successful run.
- Use three layers of per-row failure handling, where an expected failure is a returned result and an unexpected one is an exception.
- Carry both the natural key and the physical row number on every rejection, counted and reported in one place.
- Upsert on the feed's own key, so replay is safe without tracking runs.
- Raise one coalesced catalog event per batch instead of one per row.
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
