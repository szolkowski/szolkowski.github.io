public class HangfireParallelMultiCatalogSyncJob
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly IContentLoader _contentLoader;
    private readonly ReferenceConverter _referenceConverter;
    private readonly IExternalSystemClient _externalClient;
    private readonly ILogger<HangfireParallelMultiCatalogSyncJob> _logger;

    public HangfireParallelMultiCatalogSyncJob(
        ICatalogTraversalService catalogTraversal,
        IContentLoader contentLoader,
        ReferenceConverter referenceConverter,
        IExternalSystemClient externalClient,
        ILogger<HangfireParallelMultiCatalogSyncJob> logger)
    {
        _catalogTraversal = catalogTraversal;
        _contentLoader = contentLoader;
        _referenceConverter = referenceConverter;
        _externalClient = externalClient;
        _logger = logger;
    }

    public void ExecuteOrchestrator(PerformContext context)
    {
        var catalogs = _contentLoader
            .GetChildren<CatalogContentBase>(_referenceConverter.GetRootLink())
            .ToList();

        context.WriteLine(ConsoleTextColor.Cyan, "Found {0} catalogs to process", catalogs.Count);

        var jobIds = new List<string>();

        // Create a background job for each catalog
        foreach (var catalog in catalogs)
        {
            var jobId = BackgroundJob.Enqueue<HangfireParallelMultiCatalogSyncJob>(job =>
                job.ExecuteSingleCatalog(null, catalog.ContentLink, catalog.Name));
            
            jobIds.Add(jobId);
            context.WriteLine("Queued job for catalog: {0} (Job ID: {1})", catalog.Name, jobId);
        }

        context.WriteLine(ConsoleTextColor.Green, "Queued {0} catalog processing jobs", jobIds.Count);
    }

    public void ExecuteSingleCatalog(
        PerformContext context, 
        ContentReference catalogLink,
        string catalogName)
    {
        context.WriteLine(ConsoleTextColor.Cyan, "Processing catalog: {0}", catalogName);
        
        var processed = 0;
        var errors = 0;
        var startTime = DateTime.UtcNow;

        try
        {
            var options = new CatalogTraversalOptions
            {
                CatalogLink = catalogLink
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

                    if (processed % 100 == 0)
                    {
                        context.WriteLine("  {0}: Processed {1} items", catalogName, processed);
                    }
                }
                catch (Exception ex)
                {
                    context.WriteLine(ConsoleTextColor.Yellow, "  {0}: Error processing item", catalogName);
                    _logger.LogError(ex, "Error processing item in catalog {CatalogName}", catalogName);
                    errors++;
                }
            }

            var duration = DateTime.UtcNow - startTime;
            context.WriteLine(
                ConsoleTextColor.Green,
                "Completed {0}: {1} items in {2:F1} minutes. Errors: {3}",
                catalogName,
                processed,
                duration.TotalMinutes,
                errors);
        }
        catch (Exception ex)
        {
            context.WriteLine(ConsoleTextColor.Red, "Fatal error processing catalog {0}: {1}", catalogName, ex.Message);
            _logger.LogError(ex, "Fatal error in catalog {CatalogName}", catalogName);
            throw;
        }
    }
}