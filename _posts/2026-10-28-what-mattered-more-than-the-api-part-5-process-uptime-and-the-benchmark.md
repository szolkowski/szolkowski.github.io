---
layout: post
title:  "What Mattered More Than the API. Part 5: Process Uptime and the Benchmark"
description: "A long-running app imported 7x slower than a freshly restarted one — the finding that beat every API optimisation, plus runnable benchmark jobs."
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
primary_tag: performance
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

[Part 4]({% post_url 2026-10-21-the-low-level-api-is-not-automatically-fast-part-4-counting-round-trips %}) counted the round trips behind each imported row and listed four fixes worth making. This part is about the thing that turned out to matter more than all of them put together, and it was not on anybody's list.

## The Thing That Mattered More Than the API

Here's the finding I did not go looking for, and it turned out to dwarf everything in Part 4.

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

And here's the part that should make you cautious about the number at the top of this post: **in that overnight run, the low-level DTO API was slower than `IContentRepository`.** 15.50 minutes per batch against the content API's 5.92 on a young process—roughly 2.6× the wrong way. The 2.7–3.3× advantage I measured is real, and it is real *for a process that has not been up very long*. It is not a property of the API that survives every condition, and I would not have found that out if I had run the benchmark once and published.

Once you look for it, there is drift inside every single session I ran, on both APIs. In Pair C the content path went 6.48 → 8.23 → 9.94 min per batch across its three volumes, and the DTO path went 2.10 → 2.46 → 3.17.

It is tempting to put those two rows side by side, notice that both come to about +52%, and conclude that the matching percentages prove the decay is not a property of either write path. That conclusion doesn't hold, and it is worth showing why, because the mistake is an easy one.

**The two arms are not comparable.** The content session ran 190 minutes and put 12,000 rows into the node; the DTO session ran 144 minutes and put in 27,000. Normalise by either axis and the symmetry evaporates—per minute of uptime it is +0.28% against +0.35%, and per thousand rows written it is +4.4% against +1.9%, a factor of more than two. Two unnormalised endpoints happening to land on similar percentages is not a finding, it is a coincidence.

**And the per-batch data leans the other way.** In the same run, batch cost jumps at every volume boundary—+13.7% and +11.6% on the content arm—where almost no wall-clock passes but several thousand rows land under the node. A discontinuity with no elapsed time is a population signature, not an uptime one.

So: within a single session I cannot separate uptime from node population, and I'm not going to pretend otherwise. What I *can* separate is the overnight case, and that is the result worth having.

Node population simply cannot produce a number that size. Walk the fresh run: it went from a freshly cleaned category to 5,200 children at **2.18 min** per batch, then on to 12,200 children at **2.51**—7,000 more rows under the node bought a 15% slowdown. The overnight run was sitting at **15.50 and 17.64** over a comparable stretch. To pin that on population you would need the curve to go near-vertical somewhere the fresh run shows it almost flat. Within-session drift and a sevenfold collapse are different phenomena, and only the second one has a clean answer.

I want to be careful about how far I push that, because I did not record how many rows were in the category when the overnight run started, and I cannot reconstruct it from the artifacts. So I cannot give you a clean same-population pair. What I can say is that the fresh run measured the node-population effect directly, over the range in question, and it is small—nowhere near a factor of seven.

I have not chased it further, so I won't tell you what it is. Managed heap growth, cache pressure, connection-pool state and accumulated `AsyncLocal`/ambient state are all plausible and I have not separated them. What I'll tell you is the operational consequence, because it is concrete and it is easy to act on:

- **Benchmark on a freshly started process, or your numbers are about uptime rather than about your code.** Both of my first two pairs were run on relatively young processes, which is the only reason they agree.
- **If a nightly import is slower than the same import run by hand, look at how long the app pool has been up before you look at your import.** In production an app pool routinely stays up for days. Mine was slow after sixteen hours.
- **Recycling before a large import is a one-line change** that, on this evidence, is worth more than any single optimisation in the previous section.

That last point is the uncomfortable one. I spent a section counting round trips per row, and every improvement available there is worth less than restarting the process first.

## Measure It on Your Own Catalog

Here's the benchmark, so you can disagree with me using your own data. Two scheduled jobs, one per API, each importing 5,000 then 50,000 then 500,000 generated products into the same throwaway category—same content type, same generated rows, same batch loop, so the only difference between the two sets of numbers is what happens inside one row's write.

Two things about how I ran it, both of which you need in order to reproduce the figures from Part 1.

There's **one** category, shared by both jobs, so I ran the cleanup job between the two passes; without that step the second job starts against the first job's rows and the comparison is not a comparison. And **restart the application before each pass**—see the section above for why that turned out to matter more than anything else here.

The code below is the code that produced Pair D, unchanged; I checked the file timestamps against the run headers rather than assuming. That's worth insisting on generally, because a post that says "run this and reproduce it" while shipping a tidied-up version of what actually ran is making a promise it has not kept. Check the run header the jobs print—it records the catalog id and language each arm resolved, so you can confirm the two halves are writing the same thing before you believe the ratio.

It reports, per volume: total elapsed and rows/second, and then the per-batch distribution—average, **median**, fastest and slowest for every 500-product batch. Both matter. The average tells you what the import costs; the gap between the average and the median tells you whether one 8-second GC pause is quietly moving your mean.

First the shared half—content type, data generator, timing, the self-creating category, and a cleanup job:

{% include code-modal.html
   id="2026-10-28-CatalogImportBenchmark-Shared"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-28-CatalogImportBenchmark.Shared.cs"
%}

Then the two jobs. The content-API one:

{% include code-modal.html
   id="2026-10-28-ContentApiImportBenchmarkJob"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-28-ContentApiImportBenchmarkJob.cs"
%}

And the DTO one:

{% include code-modal.html
   id="2026-10-28-DtoApiImportBenchmarkJob"
   lang="csharp"
   file="post_assets/code-snippets/2026-10-28-DtoApiImportBenchmarkJob.cs"
%}

**Key Points:**

- Warm up before starting the clock, or the first path measured looks slow
- Use a fresh SKU prefix per volume, or you're measuring updates
- Everything that is not the write happens outside the measurement
- There's a one-hour budget per volume, and the report extrapolates
- Results go to disk per volume, not at the end of the job
- Prices are off by default; they're the same call on both paths
- Clean up with the third job, not row by row

**Warm up before starting the clock.** A couple of hundred throwaway rows per job absorbs JIT, connection-pool ramp-up and the cold metadata cache. Skip it and whichever path you measure first looks slower than it is, which is the single easiest way to produce a benchmark that confirms whatever you already believed.

**Fresh SKU prefix per volume.** Reuse one and the second volume is silently measuring *updates*, a different and usually faster shape. Everything that is not the write stays outside the measurement too: category resolution, meta class load and market lookup happen once, up front, and the generator is lazy, so materialising 500,000 rows never becomes part of the result.

**There's a time budget per volume**, one hour by default. A 500,000-row content-API pass can run for most of a day; without a ceiling the job gets killed by a deploy or a recycle and you learn nothing. When the budget is hit, the volume stops and the report extrapolates from what completed—`projected full 2,857.33 min for 500,000 rows at the observed rate` is a perfectly good answer, and a much cheaper one than finding out the long way. Read that projection as a **floor**, for the reason in the section above: it assumes the rate holds, and the rate falls.

**The results go to disk, per volume, as they complete.** A scheduled job's result string is overwritten by the next run and is miserable to diff, so each volume is also appended to a readable `.txt` and to a `volumes.csv` you can chart, plus optionally one row per batch in its own file. Writing after *each* volume rather than at the end of the job is the point: if the 500,000-row pass dies to a recycle, the 5,000 and 50,000 numbers are already safe. File-write failures are collected and reported, never thrown, because losing a nine-hour run to a permissions error would be a poor joke.

**Prices are off by default.** `IPriceService` is the same call on both paths—`IContentRepository` has no price story of its own—so including it just adds the same constant to both. Turn it on separately to see what the per-SKU price write costs you, because that's its own finding.

**Clean up with the third job.** It deletes the benchmark category and everything under it in one call. Deleting half a million entries individually would take longer than creating them did.

Run it on a development or staging copy. It writes real catalog rows, and the content-API half leaves a content version behind for every one—on purpose, because the job counts them at the end and that count is usually what ends the argument.

## Summary

For bulk catalog writes in Optimizely Commerce, the low-level DTO API is the right layer: it writes the rows you asked for and nothing else, no version per row, no publish pipeline per row. Measured across four independent pairs, that's worth **2.7-3.3×**, call it 3×—a real win, and a smaller one than the folklore suggests. What you give up is real too: no rollback lever, no events unless you raise them, no validation unless you write it, and an indexing question you must answer for your own solution rather than assume.

Around that writer, the pattern that made a quarter of a million rows survivable was:

- committing record by record, with no run-wide transaction, so one bad row cannot roll back the good ones;
- returning a **report of every failed row and its reason** as the deliverable—a partial run is an outcome to describe, not an error to throw;
- three stacked layers of per-row failure handling, where an expected failure is a returned outcome and only the genuinely unexpected is an exception;
- a rejection carrying both the natural key and the physical row number, counted and reported in one place;
- upsert on the feed's own key, so replay is safe without tracking runs;
- one coalesced catalog event per batch instead of one per row;
- and the discipline to actually count the round trips afterwards, because picking the fast API bought me a floor of a few rows per second and not one row more—and the humility to notice, eventually, that restarting the application mattered more than any of it.

If you have actually timed a DTO-layer write reaching Graph—or found a push path I missed—I would genuinely like to hear it, because I got as far as a well-supported deduction and stopped. Let me know in the comments!

Thank you for reading, and I hope this saves you a night of watching a progress bar that's not moving.

If you have actually timed a DTO-layer write reaching Graph—or found a push path I missed—I would genuinely like to hear it, because I got as far as a well-supported deduction and stopped. Let me know in the comments!

Thank you for reading, and I hope this series saves you a night of watching a progress bar that is not moving.

## This Post is Part of a Series

- [Part 1: Why the DTO API]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %})
- [Part 2: The Writer]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %})
- [Part 3: Error Handling and Event Batching]({% post_url 2026-10-14-surviving-a-dirty-feed-part-3-error-handling-and-event-batching %})
- [Part 4: Counting Round Trips]({% post_url 2026-10-21-the-low-level-api-is-not-automatically-fast-part-4-counting-round-trips %})
- Part 5: Process Uptime and the Benchmark - (this post)
