---
layout: post
title:  "Catalog Traversal in Action. Part 2: Real-World Scheduled Job Patterns"
date:   2026-01-24 10:00:00 +0200
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

```csharp
[ScheduledPlugIn(
    DisplayName = "Export Catalog Products",
    Description = "Exports all products from the Fashion catalog to external system",
    GUID = "681EA6C4-B635-4CC3-8D9B-DBE3BEC602A6")]
public class CatalogExportJob : ScheduledJobBase
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly IExternalSystemClient _externalClient;
    private readonly ILogger<CatalogExportJob> _logger;

    public CatalogExportJob(
        ICatalogTraversalService catalogTraversal,
        IExternalSystemClient externalClient,
        ILogger<CatalogExportJob> logger)
    {
        _catalogTraversal = catalogTraversal;
        _externalClient = externalClient;
        _logger = logger;
        IsStoppable = true;
    }

    public override string Execute()
    {
        var processedCount = 0;
        var errorCount = 0;
        var startTime = DateTime.UtcNow;

        try
        {
            var options = new CatalogTraversalOptions
            {
                CatalogName = "Fashion"
            };

            _logger.LogInformation("Starting catalog export for '{CatalogName}'", options.CatalogName);

            // The magic happens here - items are streamed one at a time
            foreach (var item in _catalogTraversal.GetAllProducts(options, CancellationToken))
            {
                try
                {
                    // Process each item - only one in memory at a time
                    switch (item)
                    {
                        case ProductContent product:
                            _externalClient.ExportProduct(product);
                            break;
                        case VariationContent variant:
                            _externalClient.ExportVariant(variant);
                            break;
                    }

                    processedCount++;

                    // Report progress every 100 items
                    if (processedCount % 100 == 0)
                    {
                        OnStatusChanged($"Processed {processedCount} items...");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error exporting item");
                    errorCount++;
                    
                    // Continue processing despite errors
                    // Alternatively, you could fail fast by re-throwing
                }

                // Check if job was stopped by user
                if (_stopSignaled)
                {
                    _logger.LogWarning("Job stopped by user at {ProcessedCount} items", processedCount);
                    return $"Job stopped by user. Processed {processedCount} items.";
                }
            }

            var duration = DateTime.UtcNow - startTime;
            var result = $"Successfully processed {processedCount} items in {duration.TotalMinutes:F1} minutes. Errors: {errorCount}";
            
            _logger.LogInformation("Catalog export completed: {Result}", result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in catalog export job");
            return $"Job failed after processing {processedCount} items: {ex.Message}";
        }
    }
}
```

**Key Points:**

- Progress reporting every 100 items keeps administrators informed
- Individual item errors are logged but don't stop the entire job
- Respects the stop signal for manual cancellation
- Tracks both success and error counts for visibility

## Pattern 2: Incremental Sync with State Management

For ongoing synchronization, you only want to process items that changed since the last successful run. This pattern dramatically reduces processing time and external API calls.

```csharp
[ScheduledPlugIn(
    DisplayName = "Incremental Catalog Sync",
    Description = "Syncs only changed products since last run",
    GUID = "A3BED4B6-FF3F-409E-895F-05567C8D3225")]
public class IncrementalCatalogSyncJob : ScheduledJobBase
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly ILastSyncRepository _lastSyncRepository;
    private readonly IExternalSystemClient _externalClient;
    private readonly ILogger<IncrementalCatalogSyncJob> _logger;

    private const string SyncStateKey = "CatalogSync_Fashion";

    public IncrementalCatalogSyncJob(
        ICatalogTraversalService catalogTraversal,
        ILastSyncRepository lastSyncRepository,
        IExternalSystemClient externalClient,
        ILogger<IncrementalCatalogSyncJob> logger)
    {
        _catalogTraversal = catalogTraversal;
        _lastSyncRepository = lastSyncRepository;
        _externalClient = externalClient;
        _logger = logger;
    }

    public override string Execute()
    {
        // Get the last successful sync timestamp
        var lastSyncDate = _lastSyncRepository.GetLastSyncDate(SyncStateKey);
        var currentSyncDate = DateTime.UtcNow;

        var updatedCount = 0;
        var errorCount = 0;

        try
        {
            var options = new CatalogTraversalOptions
            {
                CatalogName = "Fashion",
                // Only get items updated since last sync
                LastUpdated = lastSyncDate
            };

            var syncType = lastSyncDate.HasValue ? "Incremental" : "Full";
            _logger.LogInformation(
                "{SyncType} sync started. Last sync: {LastSyncDate}",
                syncType,
                lastSyncDate?.ToString("g") ?? "Never");

            foreach (var item in _catalogTraversal.GetAllProducts(options, CancellationToken))
            {
                try
                {
                    switch (item)
                    {
                        case ProductContent product:
                            _externalClient.SyncProduct(product);
                            break;
                        case VariationContent variant:
                            _externalClient.SyncVariant(variant);
                            break;
                    }

                    updatedCount++;

                    if (updatedCount % 50 == 0)
                    {
                        OnStatusChanged($"Synced {updatedCount} changed items...");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing item");
                    errorCount++;
                }
            }

            // Only update the last sync date if job completed successfully
            _lastSyncRepository.SaveLastSyncDate(SyncStateKey, currentSyncDate);

            var result = lastSyncDate.HasValue
                ? $"Incremental sync complete: {updatedCount} items changed since {lastSyncDate:g}. Errors: {errorCount}"
                : $"Full sync complete: {updatedCount} items processed. Errors: {errorCount}";

            _logger.LogInformation("Sync completed: {Result}", result);
            return result;
        }
        catch (Exception ex)
        {
            // Don't update last sync date on failure - we'll retry from the same point next time
            _logger.LogError(ex, "Fatal error in catalog sync job");
            return $"Job failed after processing {updatedCount} items: {ex.Message}";
        }
    }
}
```

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

```csharp
[ScheduledPlugIn(
    DisplayName = "Batch Catalog Export",
    Description = "Exports products in batches to external API",
    GUID = "C004BA30-C72F-445A-9EF6-EA3FCCF191B7")]
public class BatchCatalogExportJob : ScheduledJobBase
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly IExternalBatchClient _batchClient;
    private readonly ILogger<BatchCatalogExportJob> _logger;

    private const int BatchSize = 50;

    public BatchCatalogExportJob(
        ICatalogTraversalService catalogTraversal,
        IExternalBatchClient batchClient,
        ILogger<BatchCatalogExportJob> logger)
    {
        _catalogTraversal = catalogTraversal;
        _batchClient = batchClient;
        _logger = logger;
        IsStoppable = true;
    }

    public override string Execute()
    {
        var totalProcessed = 0;
        var batchCount = 0;
        var errorCount = 0;

        try
        {
            var options = new CatalogTraversalOptions
            {
                CatalogName = "Fashion"
            };

            var batch = new List<ICatalogTraversalItem>(BatchSize);

            foreach (var item in _catalogTraversal.GetAllProducts(options, CancellationToken))
            {
                batch.Add(item);

                // When batch is full, send it
                if (batch.Count >= BatchSize)
                {
                    var result = ProcessBatch(batch, ++batchCount);
                    totalProcessed += result.Processed;
                    errorCount += result.Errors;
                    
                    batch.Clear();

                    OnStatusChanged($"Processed {totalProcessed} items in {batchCount} batches...");
                }

                if (_stopSignaled)
                {
                    // Process remaining items before stopping
                    if (batch.Count > 0)
                    {
                        var result = ProcessBatch(batch, ++batchCount);
                        totalProcessed += result.Processed;
                        errorCount += result.Errors;
                    }

                    return $"Job stopped. Processed {totalProcessed} items in {batchCount} batches.";
                }
            }

            // Process any remaining items in the last batch
            if (batch.Count > 0)
            {
                var result = ProcessBatch(batch, ++batchCount);
                totalProcessed += result.Processed;
                errorCount += result.Errors;
            }

            return $"Successfully processed {totalProcessed} items in {batchCount} batches. Errors: {errorCount}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in batch export job");
            return $"Job failed after processing {totalProcessed} items: {ex.Message}";
        }
    }

    private (int Processed, int Errors) ProcessBatch(List<ICatalogTraversalItem> batch, int batchNumber)
    {
        try
        {
            _logger.LogInformation("Processing batch {BatchNumber} with {ItemCount} items", batchNumber, batch.Count);
            
            _batchClient.ExportBatch(batch);
            
            return (batch.Count, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing batch {BatchNumber}", batchNumber);
            
            // Fallback: try to process items individually
            return ProcessBatchIndividually(batch, batchNumber);
        }
    }

    private (int Processed, int Errors) ProcessBatchIndividually(List<ICatalogTraversalItem> batch, int batchNumber)
    {
        _logger.LogWarning("Batch {BatchNumber} failed, attempting individual processing", batchNumber);
        
        var processed = 0;
        var errors = 0;

        foreach (var item in batch)
        {
            try
            {
                _batchClient.ExportSingle(item);
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing individual item from batch {BatchNumber}", batchNumber);
                errors++;
            }
        }

        return (processed, errors);
    }
}
```

**Key Points:**

- Items are collected into batches of 50 (or your preferred size)
- If a batch fails, fall back to individual processing
- Remaining items are processed even when stopped
- Clear separation between batch and individual processing logic

## Pattern 4: Multi-Catalog Processing

When you have multiple catalogs and need to process all of them, this pattern ensures clean separation and progress visibility.

```csharp
[ScheduledPlugIn(
    DisplayName = "Multi-Catalog Sync",
    Description = "Syncs all catalogs to external system",
    GUID = "3FD79D80-47D3-4E17-85C1-5D97E196D691")]
public class MultiCatalogSyncJob : ScheduledJobBase
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly IContentLoader _contentLoader;
    private readonly ReferenceConverter _referenceConverter;
    private readonly IExternalSystemClient _externalClient;
    private readonly ILogger<MultiCatalogSyncJob> _logger;

    public MultiCatalogSyncJob(
        ICatalogTraversalService catalogTraversal,
        IContentLoader contentLoader,
        ReferenceConverter referenceConverter,
        IExternalSystemClient externalClient,
        ILogger<MultiCatalogSyncJob> logger)
    {
        _catalogTraversal = catalogTraversal;
        _contentLoader = contentLoader;
        _referenceConverter = referenceConverter;
        _externalClient = externalClient;
        _logger = logger;
        IsStoppable = true;
    }

    public override string Execute()
    {
        var catalogResults = new Dictionary<string, (int Processed, int Errors)>();
        var totalProcessed = 0;
        var totalErrors = 0;

        try
        {
            // Get all catalogs
            var catalogs = _contentLoader
                .GetChildren<CatalogContentBase>(_referenceConverter.GetRootLink())
                .ToList();

            _logger.LogInformation("Found {CatalogCount} catalogs to process", catalogs.Count);

            foreach (var catalog in catalogs)
            {
                if (_stopSignaled)
                {
                    _logger.LogWarning("Job stopped while processing catalog '{CatalogName}'", catalog.Name);
                    break;
                }

                OnStatusChanged($"Processing catalog: {catalog.Name}");
                
                var result = ProcessCatalog(catalog);
                catalogResults[catalog.Name] = result;
                
                totalProcessed += result.Processed;
                totalErrors += result.Errors;

                _logger.LogInformation(
                    "Completed catalog '{CatalogName}': {Processed} processed, {Errors} errors",
                    catalog.Name,
                    result.Processed,
                    result.Errors);
            }

            // Build detailed summary
            var summary = new StringBuilder();
            summary.AppendLine($"Multi-catalog sync completed:");
            summary.AppendLine($"Total: {totalProcessed} items processed, {totalErrors} errors");
            summary.AppendLine();
            
            foreach (var (catalogName, result) in catalogResults)
            {
                summary.AppendLine($"  {catalogName}: {result.Processed} items, {result.Errors} errors");
            }

            return summary.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in multi-catalog sync job");
            return $"Job failed. Processed {totalProcessed} items across {catalogResults.Count} catalogs.";
        }
    }

    private (int Processed, int Errors) ProcessCatalog(CatalogContentBase catalog)
    {
        var processed = 0;
        var errors = 0;

        var options = new CatalogTraversalOptions
        {
            CatalogLink = catalog.ContentLink
        };

        foreach (var item in _catalogTraversal.GetAllProducts(options, CancellationToken))
        {
            try
            {
                switch (item)
                {
                    case ProductContent product:
                        _externalClient.SyncProduct(product);
                        break;
                    case VariationContent variant:
                        _externalClient.SyncVariant(variant);
                        break;
                }

                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing item in catalog '{CatalogName}'", catalog.Name);
                errors++;
            }
        }

        return (processed, errors);
    }
}
```

**Key Points:**

- Each catalog is processed independently
- Results are tracked per catalog for detailed reporting
- Job can be stopped between catalogs
- Summary shows results for each catalog individually

## Pattern 5: Progress Tracking and Monitoring

For long-running jobs, detailed progress tracking helps operations teams monitor performance and identify issues early.

```csharp
[ScheduledPlugIn(
    DisplayName = "Catalog Export with Detailed Progress",
    Description = "Exports catalog with detailed progress tracking and metrics",
    GUID = "20DB2FB3-0944-4B19-BE73-AD2E17E9FED0")]
public class DetailedProgressExportJob : ScheduledJobBase
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly IExternalSystemClient _externalClient;
    private readonly ILogger<DetailedProgressExportJob> _logger;

    public DetailedProgressExportJob(
        ICatalogTraversalService catalogTraversal,
        IExternalSystemClient externalClient,
        ILogger<DetailedProgressExportJob> logger)
    {
        _catalogTraversal = catalogTraversal;
        _externalClient = externalClient;
        _logger = logger;
        IsStoppable = true;
    }

    public override string Execute()
    {
        var metrics = new ProcessingMetrics();
        var progressReporter = new ProgressReporter(this, _logger);

        try
        {
            var options = new CatalogTraversalOptions
            {
                CatalogName = "Fashion"
            };

            foreach (var item in _catalogTraversal.GetAllProducts(options, CancellationToken))
            {
                try
                {
                    var processingStarted = DateTime.UtcNow;

                    switch (item)
                    {
                        case ProductContent product:
                            _externalClient.ExportProduct(product);
                            metrics.ProductsProcessed++;
                            break;
                        case VariationContent variant:
                            _externalClient.ExportVariant(variant);
                            metrics.VariantsProcessed++;
                            break;
                    }

                    metrics.RecordProcessingTime(DateTime.UtcNow - processingStarted);
                    metrics.TotalProcessed++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing item");
                    metrics.Errors++;
                }

                // Report progress with detailed metrics
                progressReporter.ReportProgress(metrics);

                if (_stopSignaled)
                {
                    return metrics.GetStoppedSummary();
                }
            }

            return metrics.GetCompletedSummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in export job");
            return metrics.GetFailedSummary(ex.Message);
        }
    }

    private class ProcessingMetrics
    {
        public int ProductsProcessed { get; set; }
        public int VariantsProcessed { get; set; }
        public int Errors { get; set; }
        public int TotalProcessed => ProductsProcessed + VariantsProcessed;
        public DateTime StartTime { get; } = DateTime.UtcNow;
        
        private readonly List<TimeSpan> _processingTimes = new();

        public void RecordProcessingTime(TimeSpan time)
        {
            _processingTimes.Add(time);
            
            // Keep only last 100 samples to calculate average
            if (_processingTimes.Count > 100)
            {
                _processingTimes.RemoveAt(0);
            }
        }

        public TimeSpan AverageProcessingTime =>
            _processingTimes.Any()
                ? TimeSpan.FromTicks((long)_processingTimes.Average(t => t.Ticks))
                : TimeSpan.Zero;

        public TimeSpan ElapsedTime => DateTime.UtcNow - StartTime;

        public double ItemsPerSecond =>
            ElapsedTime.TotalSeconds > 0
                ? TotalProcessed / ElapsedTime.TotalSeconds
                : 0;

        public string GetCompletedSummary()
        {
            return $@"Export completed successfully:
  Products: {ProductsProcessed}
  Variants: {VariantsProcessed}
  Total: {TotalProcessed}
  Errors: {Errors}
  Duration: {ElapsedTime.TotalMinutes:F1} minutes
  Average: {ItemsPerSecond:F1} items/second
  Avg processing time: {AverageProcessingTime.TotalMilliseconds:F0}ms";
        }

        public string GetStoppedSummary()
        {
            return $"Job stopped. Processed {TotalProcessed} items ({ProductsProcessed} products, {VariantsProcessed} variants). Errors: {Errors}";
        }

        public string GetFailedSummary(string error)
        {
            return $"Job failed after processing {TotalProcessed} items: {error}";
        }
    }

    private class ProgressReporter
    {
        private readonly ScheduledJobBase _job;
        private readonly ILogger _logger;
        private DateTime _lastReport = DateTime.MinValue;
        private int _lastReportedCount = 0;
        private const int ReportIntervalSeconds = 10;

        public ProgressReporter(ScheduledJobBase job, ILogger logger)
        {
            _job = job;
            _logger = logger;
        }

        public void ReportProgress(ProcessingMetrics metrics)
        {
            var now = DateTime.UtcNow;
            
            // Report every 10 seconds
            if ((now - _lastReport).TotalSeconds < ReportIntervalSeconds)
            {
                return;
            }

            var itemsSinceLastReport = metrics.TotalProcessed - _lastReportedCount;
            var timeSinceLastReport = now - _lastReport;
            var recentRate = timeSinceLastReport.TotalSeconds > 0
                ? itemsSinceLastReport / timeSinceLastReport.TotalSeconds
                : 0;

            var status = $@"Progress: {metrics.TotalProcessed} items ({metrics.ProductsProcessed}p/{metrics.VariantsProcessed}v)
  Rate: {metrics.ItemsPerSecond:F1} items/s (recent: {recentRate:F1} items/s)
  Errors: {metrics.Errors}
  Elapsed: {metrics.ElapsedTime.TotalMinutes:F1}m";

            _job.OnStatusChanged(status);
            _logger.LogInformation("Job progress: {Status}", status.Replace("\n", " | "));

            _lastReport = now;
            _lastReportedCount = metrics.TotalProcessed;
        }
    }
}
```

**Key Points:**

- Tracks detailed metrics: products vs variants, processing times, throughput
- Reports progress every 10 seconds with current and recent rates
- Calculates average processing time for performance monitoring
- Provides detailed summaries for completion, stopping, or failure

## When to Use Each Pattern

Choose the right pattern based on your needs:

| Pattern | Best For | Key Benefit |
|---------|----------|-------------|
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
- Part 3: Hangfire Integration - wait for the future release!
