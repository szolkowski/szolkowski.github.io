[ScheduledPlugIn(
    DisplayName = "[Catalog Traversal Demo] Incremental Catalog Sync",
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
