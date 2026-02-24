[ScheduledPlugIn(
    DisplayName = "[Catalog Traversal Demo] Catalog Export with Detailed Progress",
    Description = "Exports catalog with detailed progress tracking and metrics",
    GUID = "20DB2FB3-0944-4B19-BE73-AD2E17E9FED0")]
public class DetailedProgressExportJob : ScheduledJobBase
{
    private readonly ICatalogTraversalService _catalogTraversal;
    private readonly IExternalSystemClient _externalClient;
    private readonly ILogger<DetailedProgressExportJob> _logger;
    private bool _stopSignaled;


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

    public override void Stop() => _stopSignaled = true;

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

            foreach (var item in _catalogTraversal.GetAllProducts(options, CancellationToken.None))
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
        private readonly DetailedProgressExportJob _job;
        private readonly ILogger _logger;
        private DateTime _lastReport = DateTime.MinValue;
        private int _lastReportedCount = 0;
        private const int ReportIntervalSeconds = 10;

        public ProgressReporter(DetailedProgressExportJob job, ILogger logger)
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
