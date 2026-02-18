/// <summary>
/// Automated database index maintenance job that runs on a schedule to optimize SQL Server performance.
/// This job analyzes index fragmentation and performs maintenance operations to keep queries running efficiently.
/// </summary>
[ScheduledPlugIn(
    DisplayName = "Database Index Maintenance Scheduled Job",
    SortIndex = 20000)]
public sealed class DatabaseIndexMaintenanceScheduledJob : ScheduledJobBase
{
    private bool _stopRequested;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Constructor injecting configuration for database connection access.
    /// Sets IsStoppable to allow manual termination of long-running maintenance operations.
    /// </summary>
    public DatabaseIndexMaintenanceScheduledJob(IConfiguration configuration)
    {
        _configuration = configuration;

        // Allow administrators to stop the job if it's running too long
        IsStoppable = true;
    }

    /// <summary>
    /// Handles stop requests by setting a flag that's checked during execution loops.
    /// This allows graceful cancellation between maintenance operations.
    /// </summary>
    public override void Stop()
    {
        _stopRequested = true;
    }

    /// <summary>
    /// Main entry point for the scheduled job execution.
    /// Retrieves the database connection string and delegates to ExecuteInternal.
    /// </summary>
    public override string Execute()
    {
        // Get the connection string from configuration
        var connectionString = _configuration.GetConnectionString("EPiServerDB");

        // Validate connection string exists before proceeding
        var result = !string.IsNullOrEmpty(connectionString)
        ? ExecuteInternal(connectionString)
        : "Connection string is empty";

        return result;
    }

    /// <summary>
    /// Core maintenance logic that analyzes and optimizes database indexes.
    /// Uses a three-phase approach:
    /// 1. Query all indexes and measure their fragmentation levels
    /// 2. Rebuild or reorganize indexes based on fragmentation thresholds
    /// 3. Update statistics for tables with fragmented indexes
    /// </summary>
    private string ExecuteInternal(string connectionString)
    {
        // StringBuilder accumulates log messages for the job execution report
        var log = new StringBuilder();
        try
        {
            // Establish database connection using 'using' for automatic disposal
            using var conn = new SqlConnection(connectionString);
            conn.Open();

            log.AppendLine("Starting index maintenance...");

            // Query SQL Server's Dynamic Management Views (DMVs) to analyze index fragmentation
            // sys.dm_db_index_physical_stats provides fragmentation metrics for each index
            var indexQuery = @"
                SELECT OBJECT_SCHEMA_NAME(s.[object_id]) AS SchemaName,
                        OBJECT_NAME(s.[object_id]) AS TableName,
                        i.name AS IndexName,
                        s.avg_fragmentation_in_percent AS Frag
                FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') s
                JOIN sys.indexes i
                    ON s.[object_id] = i.[object_id]
                    AND s.index_id = i.index_id
                WHERE i.type_desc IN ('CLUSTERED', 'NONCLUSTERED')
                    AND s.page_count > 100;"; // Only analyze indexes with more than 100 pages (800KB+)

            using var cmd = new SqlCommand(indexQuery, conn);
            using var reader = cmd.ExecuteReader();

            // Phase 1: Collect all indexes and their fragmentation metrics
            // Store in a list to avoid maintaining an open reader during maintenance operations
            var indexList = new List<(string Schema, string Table, string Index, double Fragmentation)>();
            while (reader.Read() && !_stopRequested)
            {
                var schema = reader.GetString(0);      // Schema name (e.g., "dbo")
                var table = reader.GetString(1);       // Table name
                var index = reader.GetString(2);       // Index name
                var frag = reader.GetDouble(3);        // Fragmentation percentage (0-100)

                indexList.Add((schema, table, index, frag));
            }

            // Close the reader before executing maintenance commands
            reader.Close();

            // Phase 2: Perform index maintenance based on fragmentation thresholds
            // Industry best practices: REBUILD > 30%, REORGANIZE 5-30%, do nothing < 5%
            foreach (var (schema, table, index, frag) in indexList)
            {
                // Check for stop request between each index operation
                if (_stopRequested) break;

                // Use pattern matching to determine the appropriate maintenance action
                var sql = frag switch
                {
                    // Severe fragmentation (>30%): REBUILD creates a new index from scratch
                    // Add optionally "WITH ONLINE = ON" which allows concurrent queries during rebuild (Enterprise Edition only)
                    > 30 => $"ALTER INDEX [{index}] ON [{schema}].[{table}] REBUILD;",

                    // Moderate fragmentation (5-30%): REORGANIZE defragments the leaf level
                    // This is always an online operation and requires less resources than rebuild
                    > 5 => $"ALTER INDEX [{index}] ON [{schema}].[{table}] REORGANIZE;",

                    // Low fragmentation (<5%): No action needed
                    _ => null
                };

                // Execute the maintenance command if an action was determined
                if (sql != null)
                {
                    log.AppendLine($"Maintaining index [{index}] on [{schema}].[{table}] - Fragmentation: {frag:F2}%");
                    using var alterCmd = new SqlCommand(sql, conn);
                    // Set a generous timeout for long-running queries
                    alterCmd.CommandTimeout = 180;
                    alterCmd.ExecuteNonQuery();
                }
            }

            // Phase 3: Update statistics for tables that had fragmented indexes
            // Statistics help the query optimizer make better execution plan decisions
            foreach (var (schema, table, _, frag) in indexList)
            {
                // Check for stop request between each statistics operation
                if (_stopRequested) break;

                // Only update statistics for tables with fragmentation > 5%
                // SAMPLE 50 PERCENT balances accuracy with execution time
                var sql = frag switch
                {
                    > 5 => $"UPDATE STATISTICS [{schema}].[{table}] WITH SAMPLE 50 PERCENT;",
                    _ => null
                };

                if (sql != null)
                {
                    log.AppendLine($"Updating statistics for [{schema}].[{table}]...");
                    using var statsCmd = new SqlCommand(sql, conn);
                    // Set a generous timeout for long-running queries
                    statsCmd.CommandTimeout = 180;
                    statsCmd.ExecuteNonQuery();
                    log.AppendLine("Statistics updated.");
                }
            }

            conn.Close();
        }
        catch (Exception ex)
        {
            // Log any errors that occur during maintenance
            log.AppendLine($"Error: {ex.Message}");
        }

        // Return the accumulated log as the job execution result
        return log.ToString();
    }
}