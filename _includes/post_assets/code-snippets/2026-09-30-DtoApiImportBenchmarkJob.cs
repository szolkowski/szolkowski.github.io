// ---------------------------------------------------------------------------------------------
// Catalog import benchmark - the low-level Commerce DTO path.
//
// Requires 2026-09-05-CatalogImportBenchmark.Shared.cs.
//
// Same content type, same category, same generated rows, same batch loop as the content-API job.
// The only difference is what happens per row: a CatalogEntryDto insert, a node relation, and the
// MetaDataPlus fields - and nothing else. No version, no publish pipeline, no events.
// ---------------------------------------------------------------------------------------------

using System;

using EPiServer;
using EPiServer.Core;
using EPiServer.Scheduler;
using EPiServer.Security;

using Mediachase.Commerce;
using Mediachase.Commerce.Catalog;
using Mediachase.Commerce.Catalog.Dto;
using Mediachase.Commerce.Catalog.Managers;
using Mediachase.Commerce.Catalog.Objects;
using Mediachase.Commerce.Markets;
using Mediachase.Commerce.Pricing;
using Mediachase.Data.Provider;
using Mediachase.MetaDataPlus;
using Mediachase.MetaDataPlus.Configurator;
using Mediachase.MetaDataPlus.Internal;

#nullable enable

namespace CatalogImport.Benchmark;

[ScheduledJob(
    DisplayName = "[Benchmark] 2. Import via the low-level DTO API",
    Description = "Creates 5k / 50k / 500k variants through CatalogContext and MetaDataPlus.",
    GUID = "5d78b2fa-01c9-4e63-b4a7-9e3f8c07d152")]
public class DtoApiImportBenchmarkJob : BenchmarkJobBase
{
    private static readonly CatalogEntryResponseGroup _infoResponseGroup =
        new(CatalogEntryResponseGroup.ResponseGroup.CatalogEntryInfo);

    private static readonly DateTime _maxEndDate = new(9999, 12, 31);

    // The content repository and loader are used ONLY by the fixture, to create the benchmark
    // category once before anything is timed. No row measured by this job goes through them.
    private readonly IContentRepository _contentRepository;
    private readonly IContentLoader _contentLoader;
    private readonly ReferenceConverter _referenceConverter;
    private readonly IPrincipalAccessor _principalAccessor;
    private readonly IPriceService _priceService;
    private readonly IMarketService _marketService;
    private readonly CatalogMetaObjectRepository _metaObjectRepository;

    private BenchmarkFixture _fixture = default!;
    private IMarket _market = default!;

    // Resolved once in Execute, not per row. CatalogContext.Current is a static resolution and
    // whatever it costs, it is not part of what this job claims to measure - and the measured
    // region should contain the write and nothing else.
    private ICatalogSystem _catalog = default!;

    public DtoApiImportBenchmarkJob(
        IContentRepository contentRepository,
        IContentLoader contentLoader,
        ReferenceConverter referenceConverter,
        IPrincipalAccessor principalAccessor,
        IPriceService priceService,
        IMarketService marketService,
        CatalogMetaObjectRepository metaObjectRepository)
    {
        _contentRepository = contentRepository;
        _contentLoader = contentLoader;
        _referenceConverter = referenceConverter;
        _principalAccessor = principalAccessor;
        _priceService = priceService;
        _marketService = marketService;
        _metaObjectRepository = metaObjectRepository;
    }

    public override string Execute()
    {
        ElevatePrincipal(_principalAccessor);

        _fixture = new BenchmarkFixture(_contentRepository, _contentLoader, _referenceConverter);
        _fixture.Prepare(Options.CategoryCode);
        _market = _marketService.GetMarket(MarketId.Default);
        _catalog = CatalogContext.Current;

        var report = RunVolumes(
            label: "Low-level DTO API",
            skuPrefix: "DTO",
            writeRow: WriteRow,
            warmup: Warmup);

        const string note =
            "No content versions were written by this job, so there is no version count to " +
            "report - which is the point. Note also that this path raises no catalog events: " +
            "in a real importer you would open one coalesced event scope per batch, which adds " +
            "one broadcast per 500 rows rather than one per row.";

        ResultWriter?.AppendNote(note);

        return report + Environment.NewLine + note;
    }

    // Stamped on the run header so the two arms can be checked for agreement at a glance -
    // they must write the same catalog and the same language or they are not comparing writes.
    protected override string RunContext =>
        $"  |  catalog {_fixture.CatalogId}  |  language {_fixture.Language}";

    private void Warmup()
    {
        foreach (var product in BenchmarkData.Generate(Options.WarmupRows, $"DTO-WARMUP-{RunStamp}"))
        {
            WriteRow(product);
        }
    }

    private void WriteRow(BenchmarkProduct product)
    {


        var dto = new CatalogEntryDto();
        var entryRow = dto.CatalogEntry.NewCatalogEntryRow();
        entryRow.CatalogId = _fixture.CatalogId;
        entryRow.Code = product.Sku;
        entryRow.Name = product.DisplayName;
        entryRow.ClassTypeId = EntryType.Variation;
        entryRow.MetaClassId = _fixture.MetaClass.Id;
        entryRow.IsActive = true;
        entryRow.IsPublished = true;
        entryRow.StartDate = DateTime.UtcNow;
        entryRow.EndDate = _maxEndDate;

        // Set by hand - this is what makes the row addressable as IContent afterwards.
        entryRow.ContentGuid = Guid.NewGuid();

        // Strongly typed DataSets reject a null assignment; you call the generated setter.
        entryRow.SetContentAssetsIDNull();
        entryRow.SetTemplateNameNull();

        dto.CatalogEntry.AddCatalogEntryRow(entryRow);

        dto.Variation.AddVariationRow(
            parentCatalogEntryRowByFK_Variation_CatalogEntry: entryRow,
            ListPrice: 0m,
            TaxCategoryId: 0,
            TrackInventory: false,
            WarehouseId: 0,
            Weight: product.Weight,
            PackageId: 0,
            MinQuantity: 0m,
            MaxQuantity: 100000m,
            Length: 0d,
            Height: 0d,
            Width: 0d);

        _catalog.SaveCatalogEntry(dto);

        // The identity value is not reliably written back onto the row. When it is not, re-read
        // with the cheapest response group there is - and note that this costs a round trip, so
        // it is one of the things worth checking if your numbers look worse than expected.
        var entryId = entryRow.CatalogEntryId;
        if (entryId <= 0)
        {
            // [0] with no meta-class filter - the thing CatalogEntryWriter.ResolveEntryId exists to
            // avoid. Fine HERE and only here: this job writes freshly stamped SKUs into a category
            // it owns, so a miss means the benchmark is broken and crashing loudly is correct.
            // Do not lift this shape into an importer.
            entryId = _catalog.GetCatalogEntryDto(product.Sku, _infoResponseGroup)
                .CatalogEntry[0].CatalogEntryId;
        }

        AddPrimaryNode(entryId);
        WriteMetaFields(entryId, product);

        if (Options.IncludePriceWrite)
        {
            WritePrice(product);
        }
    }

    private void AddPrimaryNode(int entryId)
    {
        var relations = _catalog.GetCatalogRelationDto(entryId);
        relations.NodeEntryRelation.AddNodeEntryRelationRow(
            _fixture.CatalogId, entryId, _fixture.NodeId, 0, true);
        _catalog.SaveCatalogRelationDto(relations);
    }

    /// <summary>
    /// The same properties the content-API job sets on BenchmarkVariant, written straight as
    /// MetaDataPlus fields. UnCached is deliberate: a cached meta object can lose a concurrent
    /// write, and it is part of the per-row floor.
    /// </summary>
    private void WriteMetaFields(int entryId, BenchmarkProduct product)
    {
        var metaObject =
            _metaObjectRepository.Load(entryId, _fixture.MetaClass.Id, _fixture.Language, ReadMode.UnCached)
            ?? new MetaObject(_fixture.MetaClass, entryId);

        metaObject.SetMetaField(nameof(BenchmarkVariant.BenchmarkDisplayName), product.DisplayName);
        metaObject.SetMetaField(nameof(BenchmarkVariant.BenchmarkIsActive), true);
        metaObject.SetMetaField(nameof(BenchmarkVariant.BenchmarkVolume), product.Volume);

        if (metaObject.ObjectState != MetaObjectState.Unchanged)
        {
            _metaObjectRepository.Update(metaObject, _fixture.Language);
        }
    }

    /// <summary>
    /// Identical to the content-API job's price write, on purpose - it is the same API on both
    /// paths, so with the option on it adds the same constant to both sets of numbers.
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
}
