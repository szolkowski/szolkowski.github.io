[ScheduledPlugIn(
    DisplayName = "[Catalog Traversal Demo] Export Catalog Products",
    Description = "Exports all products from the Fashion catalog to external system",
    GUID = "681EA6C4-B635-4CC3-8D9B-DBE3BEC602A6")]
public class CatalogExportJob : ScheduledJobBase
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly IExternalSystemClient _externalClient;
    private readonly ILogger<CatalogExportJob> _logger;
    private bool _stopSignaled;


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

     public override void Stop() => _stopSignaled = true;

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
            foreach (var item in _catalogTraversal.GetAllProducts(options, CancellationToken.None))
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
