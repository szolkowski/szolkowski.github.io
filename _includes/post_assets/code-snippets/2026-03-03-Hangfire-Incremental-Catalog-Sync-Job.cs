public class HangfireIncrementalCatalogSyncJob
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly ILastSyncRepository _lastSyncRepository;
    private readonly IExternalSystemClient _externalClient;
    private readonly ILogger<HangfireIncrementalCatalogSyncJob> _logger;

    private const string _syncStateKey = "CatalogSync_Fashion";

    public HangfireIncrementalCatalogSyncJob(
        ICatalogTraversalService catalogTraversal,
        ILastSyncRepository lastSyncRepository,
        IExternalSystemClient externalClient,
        ILogger<HangfireIncrementalCatalogSyncJob> logger)
    {
        _catalogTraversal = catalogTraversal;
        _lastSyncRepository = lastSyncRepository;
        _externalClient = externalClient;
        _logger = logger;
    }

    public void Execute(PerformContext context, CancellationToken cancellationToken)
    {
        var lastSyncDate = _lastSyncRepository.GetLastSyncDate(_syncStateKey);
        var currentSyncDate = DateTime.UtcNow;

        var updatedCount = 0;
        var errorCount = 0;

        try
        {
            var options = new CatalogTraversalOptions
            {
                CatalogName = "Fashion",
                LastUpdated = lastSyncDate
            };

            var syncType = lastSyncDate.HasValue ? "Incremental" : "Full";
            context.WriteLine(
                ConsoleTextColor.Cyan,
                "{0} sync started. Last sync: {1}",
                syncType,
                lastSyncDate?.ToString("g") ?? "Never");

            // Create a progress bar
            var progressBar = context.WriteProgressBar();
            var items = _catalogTraversal.GetAllProducts(options, cancellationToken).ToList();
            var totalItems = items.Count;
            context.WriteLine("Found {0} items to sync", totalItems);
            for (int i = 0; i < items.Count; i++)
            {
                try
                {
                    var item = items[i];
                        
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
                        
                    // Update progress bar
                    progressBar.SetValue((i + 1) * 100 / totalItems);
                }
                catch (Exception ex)
                {
                    context.WriteLine(ConsoleTextColor.Yellow, "Error syncing item: {0}", ex.Message);
                    _logger.LogError(ex, "Error syncing item");
                    errorCount++;
                }
            }

            // Only update the last sync date if job completed successfully
            _lastSyncRepository.SaveLastSyncDate(_syncStateKey, currentSyncDate);

            var result = lastSyncDate.HasValue
                ? $"Incremental sync complete: {updatedCount} items changed since {lastSyncDate:g}. Errors: {errorCount}"
                : $"Full sync complete: {updatedCount} items processed. Errors: {errorCount}";

            context.WriteLine(ConsoleTextColor.Green, result);
            _logger.LogInformation("Sync completed: {Result}", result);
        }
        catch (Exception ex)
        {
            context.WriteLine(ConsoleTextColor.Red, "Fatal error: {0}", ex.Message);
            _logger.LogError(ex, "Fatal error in catalog sync job");
            throw;
        }
    }
}