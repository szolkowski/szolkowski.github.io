---
layout: post
title:  "Catalog Traversal in Action. Part 2: Real-World Scheduled Job Patterns"
date:   2026-02-24 10:00:00 +0200
author: Stanisław Szołkowski
comments: true
published: true
image:
   path: assets/img/2026-02-24-Catalog-Traversal-in-Action-Part-2-Real-World-Scheduled-Job-Patterns.png
   alt: "Catalog Traversal in Action. Part 2: Real-World Scheduled Job Patterns"
tags:
- episerver
- optimizely
- commerce
- catalog
- patterns
- scheduled jobs
- .NET
---

In my [previous post]({% post_url 2026-02-18-Memory-Efficient-Catalog-Traversal-in-Optimizely-Commerce-Part-1-Building-the-Service %}), I showed how to build a memory-efficient catalog traversal service for Optimizely Commerce. The service uses streaming to process large catalogs without loading everything into memory at once.

But having a well-designed service is only half the battle. The real value comes from knowing how to use it effectively in production scenarios. In this post, I'll walk through practical patterns for scheduled jobs that process catalog data—complete with error handling, progress reporting, and resilience strategies.

## Quick Recap: The Service

As a reminder, here's the interface we're working with:

```csharp
public interface ICatalogTraversalService
{
    IEnumerable<ICatalogTraversalItem> GetAllProducts(
        CatalogTraversalOptions options,
        CancellationToken cancellationToken = default);
}

public class CatalogTraversalOptions
{
    public string? CatalogName { get; set; }
    public ContentReference? CatalogLink { get; set; }
    public DateTime? LastUpdated { get; set; }
}
```

The service yields products and variants one at a time as it traverses the catalog hierarchy. Now let's see how to use it effectively.

## Pattern 1: Full Catalog Export

The most straightforward use case: export all products from a catalog to an external system. This pattern is useful for initial data loads or complete refreshes.

{% include code-modal.html
   id="2026-02-24-Export-Catalog-Products"
   lang="csharp"
   file="post_assets/code-snippets/2026-02-24-Export-Catalog-Products.cs"
%}

**Key Points:**

- Progress reporting every 100 items keeps administrators informed
- Individual item errors are logged but don't stop the entire job
- Respects the stop signal for manual cancellation
- Tracks both success and error counts for visibility

## Pattern 2: Incremental Sync with State Management

For ongoing synchronization, you only want to process items that changed since the last successful run. This pattern dramatically reduces processing time and external API calls.

{% include code-modal.html
   id="2026-02-24-Incremental-Catalog-Sync"
   lang="csharp"
   file="post_assets/code-snippets/2026-02-24-Incremental-Catalog-Sync.cs"
%}

**Key Points:**

- State is saved ONLY after successful completion
- First run performs full sync (no last sync date)
- Subsequent runs are incremental and much faster
- Failed runs don't update the state, ensuring no data is missed

### Simple State Repository Implementation

Here's a basic implementation using DDS for storing sync state:

```csharp
public interface ILastSyncRepository
{
    DateTime? GetLastSyncDate(string key);
    void SaveLastSyncDate(string key, DateTime date);
}

[EPiServerDataStore(AutomaticallyCreateStore = true, AutomaticallyRemapStore = true)]
public class SyncStateRecord : IDynamicData
{
    public Identity Id { get; set; }
    public string Key { get; set; }
    public DateTime LastSyncDate { get; set; }
}

public class LastSyncRepository : ILastSyncRepository
{
    private readonly DynamicDataStoreFactory _dataStoreFactory;

    public LastSyncRepository(DynamicDataStoreFactory dataStoreFactory)
    {
        _dataStoreFactory = dataStoreFactory;
    }

    public DateTime? GetLastSyncDate(string key)
    {
        var store = _dataStoreFactory.GetStore(typeof(SyncStateRecord));
        var record = store.Find<SyncStateRecord>("Key", key).FirstOrDefault();
        return record?.LastSyncDate;
    }

    public void SaveLastSyncDate(string key, DateTime date)
    {
        var store = _dataStoreFactory.GetStore(typeof(SyncStateRecord));
        var record = store.Find<SyncStateRecord>("Key", key).FirstOrDefault();

        if (record == null)
        {
            record = new SyncStateRecord { Key = key, LastSyncDate = date };
            store.Save(record);
        }
        else
        {
            record.LastSyncDate = date;
            store.Save(record);
        }
    }
}
```

## Pattern 3: Batch Processing with Error Recovery

When calling external APIs, you often need to batch requests for efficiency. This pattern shows how to batch items while maintaining error resilience.

{% include code-modal.html
   id="2026-02-24-Batch-Catalog-Export"
   lang="csharp"
   file="post_assets/code-snippets/2026-02-24-Batch-Catalog-Export.cs"
%}

**Key Points:**

- Items are collected into batches of 50 (or your preferred size)
- If a batch fails, fall back to individual processing
- Remaining items are processed even when stopped
- Clear separation between batch and individual processing logic

## Pattern 4: Multi-Catalog Processing

When you have multiple catalogs and need to process all of them, this pattern ensures clean separation and progress visibility.

{% include code-modal.html
   id="2026-02-24-Multi-Catalog-Sync"
   lang="csharp"
   file="post_assets/code-snippets/2026-02-24-Multi-Catalog-Sync.cs"
%}

**Key Points:**

- Each catalog is processed independently
- Results are tracked per catalog for detailed reporting
- Job can be stopped between catalogs
- Summary shows results for each catalog individually

## Pattern 5: Progress Tracking and Monitoring

For long-running jobs, detailed progress tracking helps operations teams monitor performance and identify issues early.

{% include code-modal.html
   id="2026-02-24-Catalog-Export-with-Detailed-Progress"
   lang="csharp"
   file="post_assets/code-snippets/2026-02-24-Catalog-Export-with-Detailed-Progress.cs"
%}

**Key Points:**

- Tracks detailed metrics: products vs variants, processing times, throughput
- Reports progress every 10 seconds with current and recent rates
- Calculates average processing time for performance monitoring
- Provides detailed summaries for completion, stopping, or failure

## When to Use Each Pattern

Choose the right pattern based on your needs:

| Pattern | Best For | Key Benefit |
| --------- | ---------- | ------------- |
| **Full Export** | Initial loads, complete refreshes | Simple, straightforward |
| **Incremental Sync** | Ongoing synchronization | Dramatically faster subsequent runs |
| **Batch Processing** | API rate limits, efficiency | Reduces API calls, handles failures gracefully |
| **Multi-Catalog** | Multiple catalogs, complex setups | Clean separation, detailed reporting |
| **Detailed Progress** | Long-running jobs, monitoring | Operations visibility, performance insights |

You can also combine patterns. For example, use incremental sync with batch processing for the most efficient ongoing synchronization.

## Best Practices

Based on the patterns above, here are some key recommendations:

**Error Handling:**

- Log individual item errors but continue processing
- Implement fallback strategies (batch → individual)
- Only fail fast for truly fatal errors

**State Management:**

- Store sync state only after successful completion
- Use unique keys for different sync jobs
- Consider storing additional metadata (item count, duration)

**Progress Reporting:**

- Report progress regularly (every 10 seconds or 100 items)
- Include meaningful metrics (items/second, errors, elapsed time)
- Show both overall and recent performance

**Cancellation:**

- Always respect the stop signal
- Process remaining items/batches before stopping
- Provide clear status on what was completed

**Logging:**

- Use structured logging with meaningful context
- Log at appropriate levels (Information for progress, Warning for issues)
- Include identifiers (catalog name, item codes) for troubleshooting

## Summary

The catalog traversal service provides a solid foundation, but the real value comes from using it effectively in scheduled jobs. The patterns in this post cover the most common scenarios:

- Full exports for complete data loads
- Incremental syncs for efficient ongoing updates
- Batch processing for API efficiency and resilience
- Multi-catalog processing for complex setups
- Detailed progress tracking for operational visibility

Choose the pattern that fits your needs, and don't hesitate to combine them for more sophisticated scenarios. The streaming approach ensures your jobs will scale gracefully as your catalogs grow.

Do you have other patterns or use cases you'd like to see covered? Let me know in the comments!

Thank you for reading, and I hope these patterns help you build robust catalog processing jobs in your Optimizely Commerce solutions.

## This Post is Part of a Series

- [Part 1: Building the Service]({% post_url 2026-02-18-Memory-Efficient-Catalog-Traversal-in-Optimizely-Commerce-Part-1-Building-the-Service %})
- Part 2: Real-World Scheduled Job Patterns - (this post)
- [Part 3: Hangfire Integration]({% post_url 2026-03-03-Catalog-Traversal-with-Hangfire-Part-3-Advanced-Job-Management %})
