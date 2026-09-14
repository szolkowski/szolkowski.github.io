---
layout: post
title:  "The Low-Level API Is Not Automatically Fast. Part 4: Counting Round Trips"
description: "The low-level Commerce DTO API is not automatically fast: counting the database round trips per imported row, and the four fixes that actually help."
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
primary_tag: performance
tags:
- episerver
- optimizely
- commerce
- catalog
- performance
- import
- .NET
---

By the end of [Part 3]({% post_url 2026-10-14-surviving-a-dirty-feed-part-3-error-handling-and-event-batching %}) the importer was correct: one writer per row, a per-row failure report, one coalesced catalog event per batch. Correct, and still far too slow.

This is the part I would most want to read in somebody else's post, so I will not skip it in mine.

## The Low-Level API Is Not Automatically Fast

Here's the part I would most want to read in somebody else's post, so I won't skip it in mine.

On a restored production database, this import measured **500 products in 1.73 minutes—roughly 5 rows per second.** At that rate a 252,670-row file runs for many hours. The low-level API removed the content pipeline, and the result was *still* far too slow, because the cost had moved somewhere else entirely: into the number of database round trips per row.

The benchmark in Part 5 lands at **3.9 to 4.3 rows/s** across its three DTO arms, which is close enough to be worth mentioning—but I want to be careful about how much weight that carries, because the two are not like-for-like and they are not independent. Both ran against a local database on my own machine. Both are the same DTO API, just two implementations of it. And they differ in two ways that should have pushed them *apart*: the project measurement wrote prices and ran against a full production catalog, while the benchmark had prices off and a far smaller node. On my own argument, those two costs should have made the production figure the slower of the pair, and it was marginally the faster.

So this is not the corroboration it first looks like. All I'll claim from the two together is the order of magnitude: **a naive DTO importer runs at a few rows per second, not a few hundred.** That much is solid, and it is the part that matters for planning.

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

1. **Use the batch price overload.** `IPriceService.SetCatalogEntryPrices`—the single-key form, taking one `CatalogKey`—opens its own transaction, purges caches, replicates to the price-detail tables and broadcasts a price-updated event **per SKU**. The `IEnumerable<CatalogKey>` overload collapses 500 transactions and 500 broadcasts into one. On the shape of the work this is the highest-leverage change available—one transaction and one broadcast per batch instead of per row—and it is a one-line signature swap plus a buffer. I have not measured it: every run behind this series had prices switched off on both sides.
2. **Don't save an unchanged entry.** `SaveCatalogEntry(existing)` fires even when zero rows are dirty. You already have `DataRow.RowState`; check it. The primary-node short-circuit in the Part 2 writer is the same idea applied one level down—extend it.
3. **Watch out for quadratic category creation.** If you generate categories from the feed, you'll write something that gives each new node a unique uri segment. Mine did it by reading *every sibling under the parent, per language, per category created*—which makes creating N categories under one parent O(N²) in database work. That is my helper's design, not a Commerce behaviour, and it is the obvious way to write the check, which is exactly why it is worth naming. Resolve the sibling set once per parent and keep it.
4. **Know what `ReadMode.UnCached` costs you.** Loading the meta object uncached is a deliberate correctness trade—a cached meta object can lose a concurrent write—but it is a guaranteed database hit per row. Keep it, and know that it is part of your floor.

None of that requires going back to `IContentRepository`. It does require accepting that "I picked the fast API" is the beginning of the performance work, not the end of it.

## Gotchas

A short list of the things that cost me the most time, none of which look like problems until they are.

**These are my bugs, not Optimizely's.** Every item below is something my own importer did, and I'm listing them because they are the kind of thing you'll write too, not because the platform does them to you. Where an item describes a design choice of mine, I have said so—you should not go looking for a framework fix.

**The job shows "Unresponsive", and the process turns out to have restarted:**

- Read the causality carefully, because I had it backwards for a while: **a merely slow job never shows Unresponsive.** The scheduler's liveness ping runs on its own timer, independent of your job body.
- The flag means the process stopped answering—a recycle, an OOM, or thread-pool starvation—and left `IsRunning` set behind it.
- My cause was buffering an entire 200 MB CSV before writing a single row: a `MemoryStream`, a `ToArray()` copy, a UTF-8 validation pass over the whole array, then a fully materialised list of parsed rows. On the allocation arithmetic that's comfortably over a gigabyte, most of it on the large object heap—though I sized it from the allocation chain rather than confirmed it with a profiler, so treat the OOM as the leading suspect rather than a diagnosis.
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
- This is not something Commerce does to you; it's something a uniqueness check written the obvious way does.
- Resolve the sibling set once per parent and keep it.

**A row silently updates nothing:**

- An unguarded field assignment nulled a column the feed did not send—or a write happened outside the event scope and nobody downstream was told.

## What's Next?

Everything above is worth doing, and none of it is the biggest lever. While measuring these fixes I ran the same benchmark twice on the same machine and got results eight times apart, with nothing changed but how long the application had been running. Part 5 is that finding, and the benchmark jobs you can run on your own catalog to check it.

Have you counted the round trips on your own importer? Let me know in the comments!

Thank you for reading, and stay tuned for Part 5.

## This Post is Part of a Series

- [Part 1: Why the DTO API]({% post_url 2026-09-30-bulk-catalog-import-in-optimizely-commerce-part-1-why-the-dto-api %})
- [Part 2: The Writer]({% post_url 2026-10-07-writing-a-catalog-entry-the-low-level-way-part-2-the-writer %})
- [Part 3: Error Handling and Event Batching]({% post_url 2026-10-14-surviving-a-dirty-feed-part-3-error-handling-and-event-batching %})
- Part 4: Counting Round Trips - (this post)
- Part 5: Process Uptime and the Benchmark - coming soon
