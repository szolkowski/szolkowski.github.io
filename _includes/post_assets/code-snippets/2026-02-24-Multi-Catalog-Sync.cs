[ScheduledPlugIn(
    DisplayName = "[Catalog Traversal Demo] Multi-Catalog Sync",
    Description = "Syncs all catalogs to external system",
    GUID = "3FD79D80-47D3-4E17-85C1-5D97E196D691")]
public class MultiCatalogSyncJob : ScheduledJobBase
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly IContentLoader _contentLoader;
    private readonly ReferenceConverter _referenceConverter;
    private readonly IExternalSystemClient _externalClient;
    private readonly ILogger<MultiCatalogSyncJob> _logger;
    private bool _stopSignaled;

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
    
    public override void Stop() => _stopSignaled = true;

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

        foreach (var item in _catalogTraversal.GetAllProducts(options, CancellationToken.None))
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