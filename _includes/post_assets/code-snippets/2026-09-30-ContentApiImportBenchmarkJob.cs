// ---------------------------------------------------------------------------------------------
// Catalog import benchmark - the IContentRepository path.
//
// Requires 2026-09-05-CatalogImportBenchmark.Shared.cs.
//
// This is the "obvious" way to create a variant, and per row it writes a content version, runs
// the publish pipeline, fires IContentEvents, purges caches and queues indexing. All of that is
// inside the measurement, because all of it is what you actually pay when you import this way.
// ---------------------------------------------------------------------------------------------

using System;
using System.Linq;

using EPiServer;
using EPiServer.Core;
using EPiServer.DataAccess;
using EPiServer.Scheduler;
using EPiServer.Security;

using Mediachase.Commerce;
using Mediachase.Commerce.Catalog;
using Mediachase.Commerce.Markets;
using Mediachase.Commerce.Pricing;

#nullable enable

namespace CatalogImport.Benchmark;

[ScheduledJob(
    DisplayName = "[Benchmark] 1. Import via IContentRepository",
    Description = "Creates 5k / 50k / 500k variants through the content API and reports throughput.",
    GUID = "c3a91e58-47b6-4d02-8f7a-6b90d2e15c84")]
public class ContentApiImportBenchmarkJob : BenchmarkJobBase
{
    private readonly IContentRepository _contentRepository;
    private readonly IContentLoader _contentLoader;
    private readonly IContentVersionRepository _versionRepository;
    private readonly ReferenceConverter _referenceConverter;
    private readonly IPrincipalAccessor _principalAccessor;
    private readonly IPriceService _priceService;
    private readonly IMarketService _marketService;

    private BenchmarkFixture _fixture = default!;
    private IMarket _market = default!;
    private string _lastSku = string.Empty;

    public ContentApiImportBenchmarkJob(
        IContentRepository contentRepository,
        IContentLoader contentLoader,
        IContentVersionRepository versionRepository,
        ReferenceConverter referenceConverter,
        IPrincipalAccessor principalAccessor,
        IPriceService priceService,
        IMarketService marketService)
    {
        _contentRepository = contentRepository;
        _contentLoader = contentLoader;
        _versionRepository = versionRepository;
        _referenceConverter = referenceConverter;
        _principalAccessor = principalAccessor;
        _priceService = priceService;
        _marketService = marketService;
    }

    public override string Execute()
    {
        ElevatePrincipal(_principalAccessor);

        // Everything expensive that is not the write itself happens here, before any clock starts:
        // category resolution, meta class load, market lookup.
        _fixture = new BenchmarkFixture(_contentRepository, _contentLoader, _referenceConverter);
        _fixture.Prepare(Options.CategoryCode);
        _market = _marketService.GetMarket(MarketId.Default);

        var report = RunVolumes(
            label: "IContentRepository",
            skuPrefix: "CAPI",
            writeRow: WriteRow,
            warmup: Warmup);

        // The version count is a result too, so it goes into the same file as the timings.
        var versionCost = DescribeVersionCost();
        ResultWriter?.AppendNote(versionCost);

        return report + Environment.NewLine + versionCost;
    }

    // Stamped on the run header so the two arms can be checked for agreement at a glance -
    // they must write the same catalog and the same language or they are not comparing writes.
    protected override string RunContext =>
        $"  |  catalog {_fixture.CatalogId}  |  language {_fixture.Language}";

    private void Warmup()
    {
        foreach (var product in BenchmarkData.Generate(Options.WarmupRows, $"CAPI-WARMUP-{RunStamp}"))
        {
            WriteRow(product);
        }
    }

    private void WriteRow(BenchmarkProduct product)
    {
        // GetDefault + Save(Publish) is the whole content-API story. Note that nothing here is
        // batched or batchable - the unit of work is one content item, by design.
        var variant = _contentRepository.GetDefault<BenchmarkVariant>(_fixture.CategoryLink);

        variant.Code = product.Sku;
        variant.Name = product.DisplayName;
        variant.BenchmarkDisplayName = product.DisplayName;
        variant.BenchmarkIsActive = true;
        variant.BenchmarkVolume = product.Volume;
        variant.Weight = product.Weight;

        _contentRepository.Save(variant, SaveAction.Publish, AccessLevel.NoAccess);

        if (Options.IncludePriceWrite)
        {
            WritePrice(product);
        }

        _lastSku = product.Sku;
    }

    /// <summary>
    /// Prices never go through IContentRepository - they are IPriceService on both paths.
    /// Included only so the two jobs can be compared with it on as well as off.
    /// </summary>
    private void WritePrice(BenchmarkProduct product)
    {
        var key = new CatalogKey(product.Sku);

        _priceService.SetCatalogEntryPrices(key, new IPriceValue[]
        {
            new PriceDetailValue
            {
                CatalogKey = key,
                MarketId = _market.MarketId,
                CustomerPricing = CustomerPricing.AllCustomers,
                ValidFrom = DateTime.UtcNow,
                MinQuantity = 0,
                UnitPrice = new Money(product.RetailPrice, _market.DefaultCurrency),
            },
        });
    }

    /// <summary>
    /// One content version per row, per import run, for data with no editorial history worth
    /// keeping.
    ///
    /// It will always report 1, and that is a limitation of the benchmark rather than a result:
    /// every volume uses a fresh SKU prefix, so no row here is ever an update. The interesting
    /// number - versions accumulating across nightly re-imports of the SAME SKUs - is the one
    /// this design cannot measure. Re-run a volume with the previous run's prefix to see it.
    /// </summary>
    private string DescribeVersionCost()
    {
        if (string.IsNullOrEmpty(_lastSku))
        {
            return "No rows were written, so there is nothing to say about versions.";
        }

        var link = _referenceConverter.GetContentLink(_lastSku);
        if (ContentReference.IsNullOrEmpty(link))
        {
            return $"Could not resolve '{_lastSku}' to count its versions.";
        }

        var versions = _versionRepository.List(link).Count();

        return $"Content versions on one imported entry ('{_lastSku}') after this run: {versions}. " +
               "Multiply by the row count, then by the number of nightly runs, and that is the " +
               "table the DTO path does not write to at all.";
    }
}
