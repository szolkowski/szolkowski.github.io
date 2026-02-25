public class HangfireBatchCatalogExportJob
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly IExternalBatchClient _batchClient;
    private readonly ILogger<HangfireBatchCatalogExportJob> _logger;

    private const int _batchSize = 50;

    public HangfireBatchCatalogExportJob(
        ICatalogTraversalService catalogTraversal,
        IExternalBatchClient batchClient,
        ILogger<HangfireBatchCatalogExportJob> logger)
    {
        _catalogTraversal = catalogTraversal;
        _batchClient = batchClient;
        _logger = logger;
    }

    public string Execute(PerformContext context, CancellationToken cancellationToken)
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

            context.WriteLine(ConsoleTextColor.Cyan, "Starting batch export with batch size: {0}", _batchSize);

            var batch = new List<ICatalogTraversalItem>(_batchSize);

            foreach (var item in _catalogTraversal.GetAllProducts(options, cancellationToken))
            {
                batch.Add(item);

                if (batch.Count >= _batchSize)
                {
                    var result = ProcessBatch(context, batch, ++batchCount);
                    totalProcessed += result.Processed;
                    errorCount += result.Errors;
                    
                    batch.Clear();

                    context.WriteLine("Processed {0} items in {1} batches", totalProcessed, batchCount);
                }
            }

            // Process remaining items
            if (batch.Count > 0)
            {
                var result = ProcessBatch(context, batch, ++batchCount);
                totalProcessed += result.Processed;
                errorCount += result.Errors;
            }

            var summary = $"Processed {totalProcessed} items in {batchCount} batches. Errors: {errorCount}";
            context.WriteLine(ConsoleTextColor.Green, summary);
            
            return summary;
        }
        catch (Exception ex)
        {
            context.WriteLine(ConsoleTextColor.Red, "Fatal error: {0}", ex.Message);
            _logger.LogError(ex, "Fatal error in batch export job");
            throw;
        }
    }

    private (int Processed, int Errors) ProcessBatch(
        PerformContext context, 
        List<ICatalogTraversalItem> batch, 
        int batchNumber)
    {
        try
        {
            context.WriteLine("Processing batch {0} with {1} items", batchNumber, batch.Count);
            
            _batchClient.ExportBatch(batch);
            
            return (batch.Count, 0);
        }
        catch (Exception ex)
        {
            context.WriteLine(ConsoleTextColor.Yellow, "Batch {0} failed, attempting individual processing", batchNumber);
            _logger.LogError(ex, "Error processing batch {BatchNumber}", batchNumber);
            
            return ProcessBatchIndividually(context, batch, batchNumber);
        }
    }

    private (int Processed, int Errors) ProcessBatchIndividually(
        PerformContext context,
        List<ICatalogTraversalItem> batch, 
        int batchNumber)
    {
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
                context.WriteLine(ConsoleTextColor.Red, "Error processing item from batch {0}", batchNumber);
                _logger.LogError(ex, "Error processing individual item");
                errors++;
            }
        }

        return (processed, errors);
    }
}

public class HangfireCleanupJob
{
    public void Execute(PerformContext context, string exportSummary)
    {
        context.WriteLine(ConsoleTextColor.Cyan, "Running post-export cleanup...");
        context.WriteLine("Export summary: {0}", exportSummary);
        
        // Perform cleanup tasks
        // ...
        
        context.WriteLine(ConsoleTextColor.Green, "Cleanup completed");
    }
}