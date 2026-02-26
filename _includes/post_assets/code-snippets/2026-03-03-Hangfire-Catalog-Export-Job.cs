public class HangfireCatalogExportJob
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly IExternalSystemClient _externalClient;
    private readonly ILogger<HangfireCatalogExportJob> _logger;

    public HangfireCatalogExportJob(
        ICatalogTraversalService catalogTraversal,
        IExternalSystemClient externalClient,
        ILogger<HangfireCatalogExportJob> logger)
    {
        _catalogTraversal = catalogTraversal;
        _externalClient = externalClient;
        _logger = logger;
    }

    public void Execute(PerformContext context, CancellationToken cancellationToken)
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

            context.WriteLine(ConsoleTextColor.Green, "Starting catalog export for '{0}'", options.CatalogName);
            _logger.LogInformation("Starting catalog export for '{CatalogName}'", options.CatalogName);

            foreach (var item in _catalogTraversal.GetAllProducts(options, cancellationToken))
            {
                try
                {
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

                    // Update progress bar every 100 items
                    if (processedCount % 100 == 0)
                    {
                        context.WriteLine("Processed {0} items...", processedCount);
                    }
                }
                catch (Exception ex)
                {
                    context.WriteLine(ConsoleTextColor.Red, "Error processing item: {0}", ex.Message);
                    _logger.LogError(ex, "Error exporting item");
                    errorCount++;
                }
            }

            var duration = DateTime.UtcNow - startTime;
            var result = $"Successfully processed {processedCount} items in {duration.TotalMinutes:F1} minutes. Errors: {errorCount}";
            
            context.WriteLine(ConsoleTextColor.Green, result);
            _logger.LogInformation("Catalog export completed: {Result}", result);
        }
        catch (Exception ex)
        {
            context.WriteLine(ConsoleTextColor.Red, "Fatal error: {0}", ex.Message);
            _logger.LogError(ex, "Fatal error in catalog export job");
            throw; // Hangfire will handle retry logic
        }
    }
}