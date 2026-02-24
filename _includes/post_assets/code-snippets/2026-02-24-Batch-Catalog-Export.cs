[ScheduledPlugIn(
    DisplayName = "[Catalog Traversal Demo] Batch Catalog Export",
    Description = "Exports products in batches to external API",
    GUID = "C004BA30-C72F-445A-9EF6-EA3FCCF191B7")]
public class BatchCatalogExportJob : ScheduledJobBase
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly IExternalBatchClient _batchClient;
    private readonly ILogger<BatchCatalogExportJob> _logger;
    private bool _stopSignaled;
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

     public override void Stop() => _stopSignaled = true;

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

            foreach (var item in _catalogTraversal.GetAllProducts(options, CancellationToken.None))
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