// ---------------------------------------------------------------------------------------------
// Catalog import benchmark - shared pieces.
//
// Drop this file (plus the two job files) into any Optimizely Commerce 15 project. It has no
// dependency on anything outside the Optimizely/Mediachase assemblies: its own content type, its
// own category, its own throwaway data, and a cleanup job that removes all of it in one call.
//
// Run it on a DEVELOPMENT or STAGING database. It writes hundreds of thousands of real catalog
// rows, and the content-API job additionally leaves a content version behind for every one.
// ---------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;

using EPiServer;
using EPiServer.Authorization;
using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Commerce.Catalog.DataAnnotations;
using EPiServer.Core;
using EPiServer.DataAccess;
using EPiServer.DataAnnotations;
using EPiServer.Scheduler;
using EPiServer.Security;

using Mediachase.Commerce.Catalog;
using Mediachase.Commerce.Catalog.Dto;
using Mediachase.MetaDataPlus.Configurator;

#nullable enable

namespace CatalogImport.Benchmark;

// ---------------------------------------------------------------------------------------------
// 1. The content type both paths write.
//
// Using one type for both is what makes the comparison fair: the content API writes these
// properties through the publish pipeline, the DTO API writes the same MetaDataPlus fields
// directly. MetaClassName is set explicitly so the DTO side can MetaClass.Load(...) it by name.
//
// EVERY PROPERTY IS PREFIXED, and that is not cosmetic. Optimizely validates property attributes
// GLOBALLY BY NAME across all catalog content types: if any other type in the solution already
// has a property with the same name but, say, [CultureSpecific] on it and this one does not, the
// model sync throws at startup and the site will not boot -
//
//   "The property 'Volume' was found on multiple content types, but with different attribute
//    CultureSpecific values."
//
// A drop-in benchmark type cannot know what the host solution already declares, so it must not
// share a single property name with it. Do not "tidy" these prefixes away, and do not fix a
// collision by matching the host's attributes - that just moves the breakage to the next project
// you paste this into. (There is a config escape hatch,
// episerver.commerce:IgnorePropertyAndMetafieldMisMatch, but turning global validation off to
// accommodate throwaway code is a bad trade.)
// ---------------------------------------------------------------------------------------------

#nullable disable

[CatalogContentType(
    GUID = "8f4a7c02-3d51-4e69-9b18-2c7f5a0e64d3",
    MetaClassName = BenchmarkVariant.MetaClass,
    DisplayName = "Benchmark variant",
    Description = "Throwaway type used by the catalog import benchmark jobs.")]
public class BenchmarkVariant : VariationContent
{
    public const string MetaClass = "BenchmarkVariant";

    public virtual string BenchmarkDisplayName { get; set; }

    public virtual bool BenchmarkIsActive { get; set; }

    public virtual string BenchmarkVolume { get; set; }
}

/// <summary>
/// The category the benchmark writes into.
///
/// This exists because a catalog node must be stamped with a meta class that MetaDataPlus has
/// actually generated stored procedures for. Picking a plausible-sounding existing name does not
/// work: "CatalogNode" resolves via MetaClass.Load in most databases but is a system meta class
/// with no generated procs, so a node stamped with it is written happily and then explodes the
/// moment anything tries to read it back through the content layer -
///
///   Could not find stored procedure 'mdpsp_avto_CatalogNode_Get'.
///
/// Declaring our own node type means the content-type sync provisions both the meta class and its
/// procedures, and the fixture never has to guess. Deliberately has no properties of its own, for
/// the same collision reason documented on BenchmarkVariant.
///
/// THE [AvailableContentTypes] ATTRIBUTE IS LOAD-BEARING. IncludeOn is not merely "this type may
/// live here" - the moment ANY type in the solution declares IncludeOn for a parent, that parent
/// flips from "anything is allowed" to a whitelist of the types that opted in. Most Commerce
/// solutions have a category type declaring IncludeOn = [typeof(CatalogContent)], which silently
/// makes every other node type illegal directly under a catalog:
///
///   Content type 'BenchmarkCategory' is not allowed to be created under parent of content type
///   'CatalogContent'.
///
/// So we opt in explicitly. Include is declared for the same reason in the other direction: it
/// pins BenchmarkVariant as a legal child regardless of what the host solution restricts.
/// </summary>
[CatalogContentType(
    GUID = "1c93b6d7-52ae-4f80-9a26-df314b8e70c5",
    MetaClassName = BenchmarkCategory.MetaClass,
    DisplayName = "Benchmark category",
    Description = "Throwaway category used by the catalog import benchmark jobs.")]
[AvailableContentTypes(
    IncludeOn = new[] { typeof(CatalogContent) },
    Include = new[] { typeof(BenchmarkVariant) })]
public class BenchmarkCategory : NodeContent
{
    public const string MetaClass = "BenchmarkCategory";
}

#nullable enable

// ---------------------------------------------------------------------------------------------
// 2. The row being imported, and a lazy generator for it.
// ---------------------------------------------------------------------------------------------

public sealed class BenchmarkProduct
{
    public string Sku { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public decimal RetailPrice { get; init; }

    public double Weight { get; init; }

    public string Volume { get; init; } = string.Empty;
}

public static class BenchmarkData
{
    /// <summary>
    /// Yields products one at a time. Deliberately lazy - materialising 500,000 of these into a
    /// list before the run would put the allocation cost inside the measurement, which is exactly
    /// the mistake real importers make.
    /// </summary>
    public static IEnumerable<BenchmarkProduct> Generate(int count, string skuPrefix)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new BenchmarkProduct
            {
                Sku = $"{skuPrefix}-{i:D7}",
                DisplayName = $"Benchmark product {i:D7}",
                RetailPrice = 9.99m + (i % 100),
                Weight = 0.1d + (i % 50) / 100d,
                Volume = (i % 4) switch
                {
                    0 => "10 ml",
                    1 => "50 ml",
                    2 => "100 ml",
                    _ => "500 ml",
                },
            };
        }
    }
}

// ---------------------------------------------------------------------------------------------
// 3. Options.
// ---------------------------------------------------------------------------------------------

public sealed class BenchmarkOptions
{
    /// <summary>
    /// Volumes to run, in order. Each one is a fresh set of SKUs, so every volume measures
    /// inserts rather than updates.
    /// </summary>
    public int[] Volumes { get; init; } = { 5_000, 50_000, 500_000 };

    /// <summary>The batch the per-batch timings are reported against.</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>
    /// Hard ceiling per volume. A 500,000-row content-API pass can run for many hours; without a
    /// budget the job gets killed by a deploy or a recycle and you learn nothing. When the budget
    /// is hit the volume stops and the report extrapolates from what did complete.
    /// </summary>
    public int TimeBudgetMinutesPerVolume { get; init; } = 60;

    /// <summary>
    /// Rows written before the clock starts - not measured, but not removed either; only the
    /// cleanup job takes them out. They exist so neither path pays for JIT,
    /// connection-pool ramp-up or a cold metadata cache. Without this the first path measured
    /// always looks slower than it is.
    /// </summary>
    public int WarmupRows { get; init; } = 200;

    /// <summary>
    /// Prices are written through IPriceService on both paths or neither - IContentRepository
    /// has no price story of its own. Left off by default so the numbers isolate the entry
    /// write. Turn it on to see what the per-SKU price call actually costs you.
    /// </summary>
    public bool IncludePriceWrite { get; init; }

    public const string DefaultCategoryCode = "import-benchmark";

    public string CategoryCode { get; init; } = DefaultCategoryCode;

    /// <summary>
    /// Where the results are written. Leave null for the default, which prefers the writable,
    /// persistent %HOME%\site when one exists (Azure App Service and DXP put you there) and falls
    /// back to the application directory locally.
    /// </summary>
    public string? OutputDirectory { get; init; }

    /// <summary>
    /// Also write one CSV row per batch. Worth having on a long run: it is the only way to see
    /// whether throughput degraded as the table grew, or whether one GC pause skewed the average.
    /// </summary>
    public bool WritePerBatchCsv { get; init; } = true;
}

// ---------------------------------------------------------------------------------------------
// 4. The measurement.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Records total elapsed time plus one entry per batch, and reports the distribution rather than
/// just the mean - a single 8-second GC pause moves the average and leaves the median alone, and
/// knowing which of those happened is the whole point of keeping per-batch numbers.
/// </summary>
public sealed class ThroughputRecorder
{
    // Each batch keeps its own row count, not just its duration: the final batch of a volume can
    // be partial, and reporting a nominal 500 for it would quietly falsify the per-batch CSV.
    private readonly List<(int Rows, TimeSpan Elapsed)> _batchTimes = new();
    private readonly Stopwatch _total = new();

    public int Rows { get; private set; }

    public int Batches => _batchTimes.Count;

    /// <summary>One entry per completed batch, in the order they ran.</summary>
    public IReadOnlyList<(int Rows, TimeSpan Elapsed)> BatchSamples => _batchTimes;

    public TimeSpan Total => _total.Elapsed;

    /// <summary>Set by Stop(); a measurement should not be mutable from the outside.</summary>
    public bool StoppedEarly { get; private set; }

    public double RowsPerSecond =>
        _total.Elapsed.TotalSeconds > 0 ? Rows / _total.Elapsed.TotalSeconds : 0d;

    public TimeSpan AverageBatch =>
        _batchTimes.Count == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long) _batchTimes.Average(b => b.Elapsed.Ticks));

    public TimeSpan MedianBatch
    {
        get
        {
            if (_batchTimes.Count == 0)
            {
                return TimeSpan.Zero;
            }

            var ordered = _batchTimes.Select(b => b.Elapsed).OrderBy(t => t).ToArray();
            var mid = ordered.Length / 2;

            return ordered.Length % 2 == 1
                ? ordered[mid]
                : TimeSpan.FromTicks((ordered[mid - 1].Ticks + ordered[mid].Ticks) / 2);
        }
    }

    public TimeSpan FastestBatch => _batchTimes.Count == 0 ? TimeSpan.Zero : _batchTimes.Min(b => b.Elapsed);

    public TimeSpan SlowestBatch => _batchTimes.Count == 0 ? TimeSpan.Zero : _batchTimes.Max(b => b.Elapsed);

    public void Start() => _total.Start();

    public void Stop(bool stoppedEarly)
    {
        _total.Stop();
        StoppedEarly = stoppedEarly;
    }

    public void RecordBatch(int rows, TimeSpan elapsed)
    {
        Rows += rows;
        _batchTimes.Add((rows, elapsed));
    }

    /// <summary>
    /// One block per volume, ready to paste into a ticket.
    /// </summary>
    public string Format(string label, int requestedRows)
    {
        var report = new StringBuilder();
        var completion = StoppedEarly
            ? $"{Rows:N0} of {requestedRows:N0} rows (STOPPED EARLY - time budget or stop signal)"
            : $"{Rows:N0} rows";

        report.AppendLine(CultureInfo.InvariantCulture, $"{label}: {completion}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  total          {Format(Total)}  ({RowsPerSecond:N1} rows/s)");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  batches        {Batches:N0}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  batch avg      {Format(AverageBatch)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  batch median   {Format(MedianBatch)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  batch fastest  {Format(FastestBatch)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  batch slowest  {Format(SlowestBatch)}");

        if (StoppedEarly && RowsPerSecond > 0)
        {
            // Label it, because a bare extrapolation invites exactly the mistake this benchmark
            // exists to expose: throughput degrades over a long run, so a rate observed early is
            // the best case and the projection built from it is a floor, not an estimate.
            var projected = TimeSpan.FromSeconds(requestedRows / RowsPerSecond);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  projected full {Format(projected)} for {requestedRows:N0} rows at the observed rate");
            report.AppendLine(
                "                 ^ a FLOOR, not an estimate - the rate falls as the run goes on");
        }

        return report.ToString();
    }

    private static string Format(TimeSpan value) =>
        value.TotalSeconds < 1
            ? $"{value.TotalMilliseconds:N0} ms"
            : value.TotalSeconds < 90
                ? $"{value.TotalSeconds:N2} s"
                : $"{value.TotalMinutes:N2} min";
}

// ---------------------------------------------------------------------------------------------
// 5. Persisting the results.
//
// A scheduled job's result string lives in tblScheduledItem, gets overwritten by the next run and
// is a pain to diff. These numbers are the whole point of the exercise, so they also go to disk:
//
//   catalog-import-benchmark.txt           human-readable, appended, every run in one place
//   catalog-import-benchmark-volumes.csv   one row per volume, appended, for diffing and charting
//   batches-<api>-<volume>-<utc>.csv       one row per batch (optional), one file per volume
//
// Written after EVERY volume rather than at the end of the job, so a 500,000-row pass that gets
// killed by a recycle still leaves the 5,000 and 50,000 results behind.
// ---------------------------------------------------------------------------------------------

public sealed class BenchmarkResultWriter
{
    private const string SummaryFileName = "catalog-import-benchmark.txt";
    private const string VolumeCsvFileName = "catalog-import-benchmark-volumes.csv";

    private const string VolumeCsvHeader =
        "utc,machine,api,volume,rows_written,stopped_early,batch_size,price_write," +
        "total_seconds,rows_per_second,batches,avg_batch_ms,median_batch_ms," +
        "fastest_batch_ms,slowest_batch_ms";

    private readonly List<string> _errors = new();
    private readonly string _runStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    public BenchmarkResultWriter(string? configuredDirectory) =>
        OutputDirectory = configuredDirectory ?? ResolveDefaultDirectory();

    public string OutputDirectory { get; }

    public string SummaryPath => Path.Combine(OutputDirectory, SummaryFileName);

    public string VolumeCsvPath => Path.Combine(OutputDirectory, VolumeCsvFileName);

    /// <summary>
    /// Anything that went wrong while writing. Never thrown - a failed file write must not lose
    /// you a run that took nine hours. The job result string still carries the full report.
    /// </summary>
    public IReadOnlyList<string> Errors => _errors;

    public void AppendRunHeader(string api, BenchmarkOptions options, string context = "")
    {
        var header = new StringBuilder();
        header.AppendLine();
        header.AppendLine("================================================================");
        header.AppendLine(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z  {api}");
        header.AppendLine(CultureInfo.InvariantCulture,
            $"machine {Environment.MachineName}  |  batch size {options.BatchSize}  |  " +
            $"price write {(options.IncludePriceWrite ? "on" : "off")}  |  " +
            $"budget {options.TimeBudgetMinutesPerVolume} min/volume  |  " +
            $"warmup {options.WarmupRows} rows{context}");
        header.AppendLine("================================================================");

        Append(SummaryPath, header.ToString());
    }

    /// <summary>
    /// Free text appended to the readable report - used by the jobs for their closing findings,
    /// such as the content-version count, which is a result and belongs in the file with the rest.
    /// </summary>
    public void AppendNote(string note) =>
        Append(SummaryPath, note.TrimEnd() + Environment.NewLine + Environment.NewLine);

    public void AppendVolume(
        string api,
        BenchmarkOptions options,
        int requestedRows,
        ThroughputRecorder recorder)
    {
        Append(SummaryPath, recorder.Format($"{api} / {requestedRows:N0}", requestedRows) + Environment.NewLine);

        var row = string.Join(",",
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Csv(Environment.MachineName),
            Csv(api),
            requestedRows.ToString(CultureInfo.InvariantCulture),
            recorder.Rows.ToString(CultureInfo.InvariantCulture),
            recorder.StoppedEarly ? "true" : "false",
            options.BatchSize.ToString(CultureInfo.InvariantCulture),
            options.IncludePriceWrite ? "true" : "false",
            recorder.Total.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
            recorder.RowsPerSecond.ToString("F2", CultureInfo.InvariantCulture),
            recorder.Batches.ToString(CultureInfo.InvariantCulture),
            Ms(recorder.AverageBatch),
            Ms(recorder.MedianBatch),
            Ms(recorder.FastestBatch),
            Ms(recorder.SlowestBatch));

        AppendCsvRow(VolumeCsvPath, VolumeCsvHeader, row);

        if (options.WritePerBatchCsv && recorder.Batches > 0)
        {
            WriteBatchCsv(api, requestedRows, recorder);
        }
    }

    private void WriteBatchCsv(string api, int requestedRows, ThroughputRecorder recorder)
    {
        var name = $"batches-{Slug(api)}-{requestedRows}-{_runStamp}.csv";
        var content = new StringBuilder("batch_number,rows,elapsed_ms").AppendLine();

        for (var i = 0; i < recorder.BatchSamples.Count; i++)
        {
            var (rows, elapsed) = recorder.BatchSamples[i];
            content.AppendLine(CultureInfo.InvariantCulture, $"{i + 1},{rows},{Ms(elapsed)}");
        }

        Append(Path.Combine(OutputDirectory, name), content.ToString());
    }

    private void AppendCsvRow(string path, string header, string row)
    {
        // Write the header only when the file is new, so the CSV stays appendable across runs
        // and opens straight into a spreadsheet.
        var needsHeader = !File.Exists(path);
        Append(path, (needsHeader ? header + Environment.NewLine : string.Empty) + row + Environment.NewLine);
    }

    private void Append(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            File.AppendAllText(path, content, Encoding.UTF8);
        }
        // Deliberately unfiltered. The contract above says this never throws, and a narrow filter
        // guarding a broad promise is worse than either - an ArgumentException from a bad
        // OutputDirectory (the most likely misconfiguration) or a SecurityException would sail
        // straight through a typed filter and kill the nine-hour run this catch exists to protect.
        catch (Exception ex)
        {
            var message = $"Could not write '{path}': {ex.Message}";
            if (!_errors.Contains(message))
            {
                _errors.Add(message);
            }
        }
    }

    /// <summary>
    /// %HOME% is set and writable on Azure App Service and DXP, and survives a restart, which the
    /// application directory does not. Locally it is unset and we fall back to the bin folder.
    /// Override BenchmarkOptions.OutputDirectory if neither suits you.
    /// </summary>
    private static string ResolveDefaultDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");

        return !string.IsNullOrWhiteSpace(home) && Directory.Exists(home)
            ? Path.Combine(home, "site", "benchmark-results")
            : Path.Combine(AppContext.BaseDirectory, "benchmark-results");
    }

    private static string Ms(TimeSpan value) =>
        value.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string Slug(string value) =>
        new string(value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray())
            .Trim('-');
}

// ---------------------------------------------------------------------------------------------
// 6. Fixture: the benchmark category, created on demand through the DTO API.
//
// Doing this once, outside the measured region, means neither job needs a manual setup step and
// neither pays for category resolution inside the clock.
// ---------------------------------------------------------------------------------------------

public sealed class BenchmarkFixture
{
    private readonly IContentRepository _contentRepository;
    private readonly IContentLoader _contentLoader;
    private readonly ReferenceConverter _referenceConverter;

    public BenchmarkFixture(
        IContentRepository contentRepository,
        IContentLoader contentLoader,
        ReferenceConverter referenceConverter)
    {
        _contentRepository = contentRepository;
        _contentLoader = contentLoader;
        _referenceConverter = referenceConverter;
    }

    public int CatalogId { get; private set; }

    public int NodeId { get; private set; }

    public ContentReference CategoryLink { get; private set; } = ContentReference.EmptyReference;

    public MetaClass MetaClass { get; private set; } = default!;

    /// <summary>
    /// The catalog default language. The content-API path writes meta fields in whatever language
    /// IContentRepository resolves; the DTO path has to be told. Hardcoding "en" here would make
    /// the two halves write DIFFERENT rows on any solution whose catalog is not English, and the
    /// head-to-head would silently stop comparing like with like.
    /// </summary>
    public string Language { get; private set; } = "en";

    public void Prepare(string categoryCode)
    {
        MetaClass = LoadMetaClass(BenchmarkVariant.MetaClass);
        var categoryMetaClass = LoadMetaClass(BenchmarkCategory.MetaClass);

        var catalog = CatalogContext.Current;
        var existing = catalog.GetCatalogNodeDto(categoryCode);

        // Same guard the cleanup job has. Without it the benchmark happily writes into node [0] in
        // whichever catalog it lands in, and cleanup then refuses to remove it - you would be left
        // with half a million rows and no supported way to delete them.
        if (existing.CatalogNode.Count > 1)
        {
            throw new InvalidOperationException(
                $"{existing.CatalogNode.Count} categories are coded '{categoryCode}'. Pick a code that " +
                "is unique, or remove the duplicates, before benchmarking.");
        }

        if (existing.CatalogNode.Count == 0)
        {
            CreateCategory(categoryCode);
            existing = catalog.GetCatalogNodeDto(categoryCode);

            if (existing.CatalogNode.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Created category '{categoryCode}' but could not read it back from the catalog tables.");
            }
        }
        else if (existing.CatalogNode[0].MetaClassId != categoryMetaClass.Id)
        {
            throw new InvalidOperationException(
                $"A category with code '{categoryCode}' already exists but is not a " +
                $"{BenchmarkCategory.MetaClass}. If an earlier run created it with a system meta class, the " +
                "node has no generated stored procedures and cannot be loaded through the content layer " +
                "(you would see \"Could not find stored procedure 'mdpsp_avto_CatalogNode_Get'\"). Run " +
                "'[Benchmark] 3. Delete benchmark catalog data' to remove it, then run this job again.");
        }

        var row = existing.CatalogNode[0];
        CatalogId = row.CatalogId;
        Language = ResolveDefaultLanguage(CatalogId);
        NodeId = row.CatalogNodeId;

        // The code overload resolves both nodes and entries. Note the three-argument overload
        // takes an int content id, not a code - it is not the one you want here.
        CategoryLink = _referenceConverter.GetContentLink(categoryCode);

        if (ContentReference.IsNullOrEmpty(CategoryLink))
        {
            throw new InvalidOperationException(
                $"Category '{categoryCode}' exists in the catalog tables but has no content link. " +
                "Restart the site so the content layer picks it up.");
        }
    }

    /// <summary>
    /// Created through IContentRepository rather than by hand-building a CatalogNodeDto, because
    /// the content API knows which meta class to stamp on the node and we demonstrably do not.
    ///
    /// This does NOT compromise the benchmark: it is one row, created once, before either clock
    /// starts, and it is setup rather than the thing being measured. The DTO job takes the same
    /// dependency for the same reason.
    /// </summary>
    private void CreateCategory(string categoryCode)
    {
        var catalogLink = _contentLoader
            .GetChildren<CatalogContent>(_referenceConverter.GetRootLink())
            .Select(c => c.ContentLink)
            .FirstOrDefault();

        if (ContentReference.IsNullOrEmpty(catalogLink))
        {
            throw new InvalidOperationException("No catalog exists. Create one before benchmarking.");
        }

        var category = _contentRepository.GetDefault<BenchmarkCategory>(catalogLink);
        category.Code = categoryCode;
        category.Name = "Import benchmark";

        _contentRepository.Save(category, SaveAction.Publish, AccessLevel.NoAccess);
    }

    /// <summary>
    /// Reads the catalog's default language from the CatalogDto. Falls back to the row's first
    /// language, then to "en", because a benchmark that refuses to start over a language lookup
    /// is worse than one that makes a documented assumption.
    /// </summary>
    private static string ResolveDefaultLanguage(int catalogId)
    {
        var catalogs = CatalogContext.Current.GetCatalogDto(catalogId);
        if (catalogs.Catalog.Count == 0)
        {
            return "en";
        }

        // IsDefaultLanguageNull() first, and this is not defensive padding: the column is
        // nullable, so the generated accessor THROWS StrongTypingException on NULL rather than
        // returning null - IsNullOrWhiteSpace would never get the chance to run. Exactly the trap
        // this post warns about a few sections up, which I then walked straight into.
        var row = catalogs.Catalog[0];
        if (!row.IsDefaultLanguageNull() && !string.IsNullOrWhiteSpace(row.DefaultLanguage))
        {
            return row.DefaultLanguage;
        }

        return catalogs.CatalogLanguage.Count > 0
            ? catalogs.CatalogLanguage[0].LanguageCode
            : "en";
    }

    private static MetaClass LoadMetaClass(string name) =>
        MetaClass.Load(CatalogContext.MetaDataContext, name)
        ?? throw new InvalidOperationException(
            $"Meta class '{name}' does not exist. Start the site once so the content-type sync " +
            "provisions the benchmark types, then run the job again.");
}

// ---------------------------------------------------------------------------------------------
// 7. Cleanup.
//
// One call removes the benchmark category and every entry under it. Deleting half a million
// entries individually would itself take hours; this is the cheap way. Restoring a database
// snapshot is cheaper still if you have one.
// ---------------------------------------------------------------------------------------------

[ScheduledJob(
    DisplayName = "[Benchmark] 3. Delete benchmark catalog data",
    Description = "Removes the benchmark category and every entry under it.",
    GUID = "b6e1d0c4-9a72-4f38-8d55-1e0a4c7b93f6")]
public class BenchmarkCleanupJob : ScheduledJobBase
{
    // Single-sourced from BenchmarkOptions so the two cannot drift apart as literals. Be clear
    // about what this does NOT do though: if you override CategoryCode on a derived job, this
    // job still cleans the DEFAULT category. Override it here too, or clean up by hand.
    private const string CategoryCode = BenchmarkOptions.DefaultCategoryCode;

    /// <summary>
    /// Set to true only to remove a category left behind by an older build of this benchmark,
    /// which stamped nodes with a meta class that has no generated stored procedures.
    /// </summary>
    private const bool ForceDeleteForeignCategory = false;

    public override string Execute()
    {
        var catalog = CatalogContext.Current;
        var node = catalog.GetCatalogNodeDto(CategoryCode);

        if (node.CatalogNode.Count == 0)
        {
            return $"Nothing to clean up - no category coded '{CategoryCode}'.";
        }

        // This is the most destructive line in the whole set, so it gets the most checking.
        // More than one match means the code is not the unique handle we assumed and we cannot
        // tell which one is ours; deleting the wrong catalog's tree is not recoverable.
        if (node.CatalogNode.Count > 1)
        {
            return $"Refusing to delete: {node.CatalogNode.Count} categories are coded " +
                   $"'{CategoryCode}'. Remove the benchmark one by hand.";
        }

        var row = node.CatalogNode[0];

        var metaClass = MetaClass.Load(CatalogContext.MetaDataContext, BenchmarkCategory.MetaClass);
        var isBenchmarkCategory = metaClass is not null && row.MetaClassId == metaClass.Id;
        var description =
            $"category '{CategoryCode}' (node {row.CatalogNodeId}, catalog {row.CatalogId}, " +
            $"name '{row.Name}')";

        // Report BEFORE deleting, not after. An earlier version appended a "this might not have
        // been yours" note to the return string of a call that had already run - which reads like
        // a safety check and is not one. If this node is not ours, refuse and make the reader
        // opt in; DeleteCatalogNodeAndEntries is the only irreversible line in this whole set.
        //
        // We cannot simply REQUIRE BenchmarkCategory: the documented recovery path is a node
        // stamped with the wrong meta class by an older build, and removing that is exactly what
        // this job is for. Hence an explicit flag rather than a hard rule.
        if (!isBenchmarkCategory && !ForceDeleteForeignCategory)
        {
            return $"Refusing to delete {description}: it is not a {BenchmarkCategory.MetaClass}. " +
                   "If you created it with an earlier build of this benchmark, set " +
                   $"{nameof(ForceDeleteForeignCategory)} and run again. If you did not, this is " +
                   "somebody else's data.";
        }

        catalog.DeleteCatalogNodeAndEntries(row.CatalogNodeId, row.CatalogId);

        return $"Deleted {description} and all entries under it. " +
               "Content versions left by the content-API job are removed with their entries, " +
               "but run the CMS 'Automatic Emptying of Trash' / version-trim jobs if you want the " +
               "version tables compacted too. On a 500,000-row volume this is one very large " +
               "delete and may exceed the SQL command timeout - restoring a database snapshot is " +
               "the reliable option at that size.";
    }
}

// ---------------------------------------------------------------------------------------------
// 8. Shared job plumbing.
// ---------------------------------------------------------------------------------------------

public abstract class BenchmarkJobBase : ScheduledJobBase
{
    // volatile, and not an auto-property. Stop() is called from the scheduler thread while the
    // job thread reads this in a tight loop with no other synchronisation - in a Release build the
    // JIT is entitled to hoist that read and the Stop button does nothing. Most Optimizely job
    // samples get this wrong, including the one this was adapted from.
    private volatile bool _stopSignaled;

    protected BenchmarkJobBase() => IsStoppable = true;

    public override void Stop() => _stopSignaled = true;

    protected bool StopSignaled => _stopSignaled;

    private static readonly string[] _administratorRole = { Roles.Administrators };

    /// <summary>
    /// Stamps every SKU this run writes, warm-up included. Without it a second run collides with
    /// the first on duplicate Code and dies in the warm-up, before a single measurement - and a
    /// benchmark nobody can run twice is not a benchmark.
    /// </summary>
    protected string RunStamp { get; } = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Override in a derived job to run different volumes, or just edit the defaults on
    /// <see cref="BenchmarkOptions"/>. The jobs are constructed by DI, so there is nowhere
    /// to pass this in from the outside.
    /// </summary>
    protected virtual BenchmarkOptions Options { get; } = new();

    /// <summary>
    /// Set by <see cref="RunVolumes"/>. Jobs use it to append their own closing findings to the
    /// same file the per-volume numbers went to.
    /// </summary>
    protected BenchmarkResultWriter? ResultWriter { get; private set; }

    /// <summary>
    /// Extra facts to stamp on the run header. The resolved catalog and language belong here:
    /// the two arms MUST agree on both, and an assumption you can read in the output is much
    /// cheaper to catch than one buried in a fixture.
    /// </summary>
    protected virtual string RunContext => string.Empty;

    /// <summary>
    /// Runs every configured volume through <paramref name="writeRow"/>, timing each batch.
    /// Both jobs share this loop, so the only difference between the two sets of numbers is
    /// what happens inside writeRow.
    /// </summary>
    protected string RunVolumes(
        string label,
        string skuPrefix,
        Action<BenchmarkProduct> writeRow,
        Action warmup)
    {
        // Outside the clock on purpose.
        warmup();

        var writer = new BenchmarkResultWriter(Options.OutputDirectory);
        ResultWriter = writer;
        writer.AppendRunHeader(label, Options, RunContext);

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"{label} - batch size {Options.BatchSize}, price write {(Options.IncludePriceWrite ? "on" : "off")}");
        report.AppendLine();

        foreach (var volume in Options.Volumes)
        {
            if (StopSignaled)
            {
                report.AppendLine("Stopped before remaining volumes.");
                break;
            }

            var recorder = RunVolume(volume, skuPrefix, writeRow);

            // Persist immediately. If the next volume is the 500,000 one and the process is
            // recycled halfway through it, this volume's numbers are already safely on disk.
            writer.AppendVolume(label, Options, volume, recorder);

            report.Append(recorder.Format($"{label} / {volume:N0}", volume));
            report.AppendLine();
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"Results written to {writer.OutputDirectory}");
        report.AppendLine(CultureInfo.InvariantCulture, $"  {Path.GetFileName(writer.SummaryPath)}   (readable, appended)");
        report.AppendLine(CultureInfo.InvariantCulture, $"  {Path.GetFileName(writer.VolumeCsvPath)}   (one row per volume, appended)");

        foreach (var error in writer.Errors)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  WARNING: {error}");
        }

        return report.ToString();
    }

    private ThroughputRecorder RunVolume(int volume, string skuPrefix, Action<BenchmarkProduct> writeRow)
    {
        // A prefix per volume keeps every pass an insert. Reuse one and the second volume would
        // silently be measuring updates, which are a different - and usually faster - shape.
        var prefix = $"{skuPrefix}-{volume}-{RunStamp}";
        var budget = TimeSpan.FromMinutes(Options.TimeBudgetMinutesPerVolume);

        var recorder = new ThroughputRecorder();
        var batchStopwatch = new Stopwatch();
        var batchRows = 0;
        var stoppedEarly = false;

        recorder.Start();

        try
        {
            foreach (var product in BenchmarkData.Generate(volume, prefix))
            {
                if (batchRows == 0)
                {
                    batchStopwatch.Restart();
                }

                writeRow(product);
                batchRows++;

                if (batchRows < Options.BatchSize)
                {
                    continue;
                }

                batchStopwatch.Stop();
                recorder.RecordBatch(batchRows, batchStopwatch.Elapsed);
                batchRows = 0;

                OnStatusChanged(
                    $"{prefix}: {recorder.Rows:N0}/{volume:N0} rows, {recorder.RowsPerSecond:N1} rows/s");

                if (StopSignaled || recorder.Total >= budget)
                {
                    // Only "stopped early" if there was actually something left to write. Crossing the
                    // budget on the very last batch is a completed volume, not a truncated one, and
                    // reporting it as truncated produces the nonsense "5,000 of 5,000 rows (STOPPED
                    // EARLY)" plus a projection for a run that already finished.
                    stoppedEarly = recorder.Rows < volume;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Without this, a throw on row 499,999 of a nine-hour pass unwinds past AppendVolume
            // and the whole volume is lost - defeating the "persist after every volume" design
            // precisely when it matters. Record what we got and let the caller move on.
            stoppedEarly = true;
            OnStatusChanged($"{prefix}: aborted after {recorder.Rows:N0} rows - {ex.Message}");
        }
        finally
        {
            // A trailing partial batch is timed too, but it is not comparable to a full one, so
            // it is recorded with its real row count rather than padded.
            if (batchRows > 0)
            {
                batchStopwatch.Stop();
                recorder.RecordBatch(batchRows, batchStopwatch.Elapsed);
            }

            recorder.Stop(stoppedEarly);
        }

        return recorder;
    }

    /// <summary>
    /// The DTO API reads the ambient principal and a scheduled job has not got one. Set once,
    /// before the clock starts, so it costs neither path anything.
    /// </summary>
    /// <summary>
    /// Deliberately does NOT restore the previous principal, unlike the importer earlier in this
    /// post which carefully saves and restores it in a finally. A scheduled job owns its thread
    /// for its whole life and nothing else runs on it, so there is nothing to restore for. Do not
    /// lift this into request-scoped code - use the save/restore shape instead.
    /// </summary>
    protected static void ElevatePrincipal(IPrincipalAccessor principalAccessor) =>
        principalAccessor.Principal = new GenericPrincipal(
            new GenericIdentity("CatalogImportBenchmark"),
            _administratorRole);

}
