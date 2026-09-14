using System;
using System.Collections.Generic;
using System.Linq;

using EPiServer.ServiceLocation;

using Mediachase.Commerce.Catalog;
using Mediachase.Commerce.Catalog.Dto;
using Mediachase.Commerce.Catalog.Managers;
using Mediachase.Commerce.Catalog.Objects;
using Mediachase.MetaDataPlus;
using Mediachase.MetaDataPlus.Configurator;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

#nullable enable

namespace CatalogImport;

/// <summary>
/// Writes a single catalog entry (a variant) through the low-level Commerce DTO API.
/// Nothing here touches IContentRepository, so nothing here creates a content version.
/// </summary>
public class CatalogEntryWriter : ICatalogEntryWriter
{
    // The FK that fires when a cached node id points at a category that no longer exists.
    private const string NodeEntryRelationConstraint = "FK_NodeEntryRelation_CatalogNode";

    // One read serves all three paths, sized for the most demanding one. The update path genuinely
    // needs the variation row (it writes Weight), and a nightly feed is mostly updates - so probing
    // with a cheap group and re-reading on a hit would add a round trip to the common case to save
    // one on the rare case. That is the trade; it is not "ask for everything and hope".
    private static readonly CatalogEntryResponseGroup _fullResponseGroup =
        new(CatalogEntryResponseGroup.ResponseGroup.CatalogEntryFull |
            CatalogEntryResponseGroup.ResponseGroup.Variations);

    // Ids only. Used for the "what id did I just insert?" re-read below.
    private static readonly CatalogEntryResponseGroup _infoResponseGroup =
        new(CatalogEntryResponseGroup.ResponseGroup.CatalogEntryInfo);

    private static readonly DateTime _maxEndDate = new(9999, 12, 31);

    // Both of these are normally reached statically - CatalogContext.Current and
    // CatalogContext.MetaDataContext. The statics work, and they make this class impossible to
    // unit test even for its validation branches, because Upsert touches one on its second
    // executable line. Injecting only the first (which is what I did at first) does not help:
    // it moves the wall a few lines down and leaves the class exactly as untestable.
    private readonly ICatalogSystem _catalog;
    // A ServiceAccessor, not the instance. MetaDataContext is registered TRANSIENT, so capturing
    // one in a long-lived writer gives you a private context with its own MetaDataPlus cache -
    // and its own Language, which is what decides the language row SetMetaField lands in. That is
    // a different object from the process-wide CatalogContext.MetaDataContext everything else
    // uses. Resolving per call is what Optimizely's own CatalogMetaObjectRepository does.
    private readonly ServiceAccessor<MetaDataContext> _metaDataContext;
    private readonly ICategoryResolver _categoryResolver;
    private readonly ICatalogPriceWriter _priceWriter;
    private readonly IMetaObjectWriter _metaWriter;
    private readonly ILogger<CatalogEntryWriter> _logger;

    // Resolved once per writer instance, and NOT because meta classes are immutable - they are
    // not. Commerce Manager and the MetaDataPlus admin pages can add or drop fields on a live
    // context while your import is running. Pinning the definition for the duration of the run is
    // the point: a schema change landing halfway through a 250,000-row feed should not silently
    // split the run into rows written against two different shapes.
    private MetaClass? _metaClass;

    public CatalogEntryWriter(
        ICatalogSystem catalog,
        ServiceAccessor<MetaDataContext> metaDataContext,
        ICategoryResolver categoryResolver,
        ICatalogPriceWriter priceWriter,
        IMetaObjectWriter metaWriter,
        ILogger<CatalogEntryWriter> logger)
    {
        _catalog = catalog;
        _metaDataContext = metaDataContext;
        _categoryResolver = categoryResolver;
        _priceWriter = priceWriter;
        _metaWriter = metaWriter;
        _logger = logger;
    }

    public WriteOutcome Upsert(ProductImportItem item)
    {
        // Validation is ours to do. The DTO layer runs no content-type validation at all:
        // a missing required field becomes a NULL column, not a rejected save.
        if (string.IsNullOrWhiteSpace(item.Sku))
        {
            return WriteOutcome.Failure("Sku is required.");
        }

        if (item.IsActive is null)
        {
            return WriteOutcome.Failure("IsActive is required.");
        }

        var metaClass = ResolveMetaClass();
        var existing = _catalog.GetCatalogEntryDto(item.Sku, _fullResponseGroup);

        var existingRow = existing.CatalogEntry
            .Cast<CatalogEntryDto.CatalogEntryRow>()
            .FirstOrDefault(e => e.MetaClassId == metaClass.Id);

        // A feed "delete" is a soft delete: flip a meta flag, leave the row in place.
        if (item.IsActive == false)
        {
            if (existingRow is not null)
            {
                Deactivate(existingRow, metaClass);
            }

            return WriteOutcome.Success();
        }

        return existingRow is null
            ? Create(item, metaClass, allowStaleCategoryRetry: true)
            : Update(existing, existingRow, item, metaClass);
    }

    /// <summary>
    /// Inserts a new variant: entry row, variation row, primary node relation, meta fields, prices.
    /// </summary>
    private WriteOutcome Create(ProductImportItem item, MetaClass metaClass, bool allowStaleCategoryRetry)
    {
        // Upsert has already rejected a null or blank SKU, but the compiler cannot see that
        // across the call, so pin it once here rather than sprinkling ! down the method.
        var sku = item.Sku!;

        var missing = FirstMissingRequiredField(item);
        if (missing is not null)
        {
            return WriteOutcome.Failure(missing + " is required when creating an entry.");
        }

        var category = _categoryResolver.Resolve(item.CategoryCode);
        if (category is null)
        {
            return WriteOutcome.Failure("Unknown category code: " + item.CategoryCode);
        }

        var (catalogId, nodeId) = category.Value;

        var dto = new CatalogEntryDto();
        var entryRow = dto.CatalogEntry.NewCatalogEntryRow();
        entryRow.CatalogId = catalogId;
        entryRow.Code = sku;
        entryRow.Name = Truncate(item.DisplayName ?? sku, 100);
        entryRow.ClassTypeId = EntryType.Variation;
        entryRow.MetaClassId = metaClass.Id;
        entryRow.IsActive = true;
        entryRow.IsPublished = true;
        entryRow.StartDate = DateTime.UtcNow;
        entryRow.EndDate = _maxEndDate;

        // Set by hand. This is the field the content layer would normally own, and it is what
        // makes the row addressable as IContent afterwards. Forget it and the entry is orphaned
        // from the content world.
        entryRow.ContentGuid = Guid.NewGuid();

        // Strongly typed DataSets do not accept a null assignment - you call the generated SetXxxNull().
        entryRow.SetContentAssetsIDNull();
        entryRow.SetTemplateNameNull();

        dto.CatalogEntry.AddCatalogEntryRow(entryRow);

        dto.Variation.AddVariationRow(
            parentCatalogEntryRowByFK_Variation_CatalogEntry: entryRow,
            ListPrice: 0m,
            TaxCategoryId: 0,
            TrackInventory: false,
            WarehouseId: 0,
            Weight: item.Weight ?? 0d,
            PackageId: 0,
            MinQuantity: 0m,
            MaxQuantity: 100000m,
            Length: 0d,
            Height: 0d,
            Width: 0d);

        _catalog.SaveCatalogEntry(dto);

        var entryId = ResolveEntryId(entryRow, sku, metaClass, catalogId);
        if (entryId <= 0)
        {
            // We saved something and cannot prove which row it is, so we must not guess - the id
            // is about to be handed to a recursive delete. Report it and leave the row alone.
            return WriteOutcome.Failure(
                $"Saved '{sku}' but could not read its id back; the entry may exist without a " +
                "category, meta fields or prices and needs checking by hand.");
        }

        try
        {
            AddPrimaryNode(entryId, catalogId, nodeId);

            var metaObject = _metaWriter.LoadOrCreate(entryId, metaClass);
            ApplyMetaFields(metaObject, item);
            _metaWriter.Persist(metaObject);

            _priceWriter.SetPrices(sku, item.ListPrice, item.RetailPrice!.Value);
        }
        catch (Exception ex)
        {
            // The entry row is already committed by now. A half-created entry with no category
            // and no price is worse than no entry at all, so this is the one place in the whole
            // import where all-or-nothing is the right call - undo it.
            try
            {
                _catalog.DeleteCatalogEntry(entryId, recursive: true);
            }
            catch (Exception cleanupEx)
            {
                throw new AggregateException(
                    "Creating " + sku + " failed, and rolling the partial entry back also failed.",
                    ex,
                    cleanupEx);
            }

            // A cached category id can outlive the category itself - someone deleted it, or the
            // database was restored under a running app. Drop the cache entry and try once more.
            // This is a correctness retry, not a resilience retry: it is keyed on one exact FK.
            if (allowStaleCategoryRetry && IsStaleCategoryReference(ex))
            {
                _logger.LogWarning(ex,
                    "Category {CategoryCode} resolved to a node that no longer exists while creating {Sku}. Retrying once.",
                    item.CategoryCode,
                    sku);

                _categoryResolver.Invalidate(item.CategoryCode!);
                return Create(item, metaClass, allowStaleCategoryRetry: false);
            }

            throw;
        }

        CatalogChangeBatch.EntryChanged(catalogId, entryId, nodeId, parentChanged: true);
        return WriteOutcome.Success();
    }

    /// <summary>
    /// Updates an existing variant by mutating the hydrated DTO in place.
    /// The DataSet is the change tracker: SaveCatalogEntry writes only rows whose RowState is dirty.
    /// </summary>
    private WriteOutcome Update(
        CatalogEntryDto existing,
        CatalogEntryDto.CatalogEntryRow entryRow,
        ProductImportItem item,
        MetaClass metaClass)
    {

        var entryId = entryRow.CatalogEntryId;

        var catalogId = 0;
        var nodeId = 0;
        var moveRequested = !string.IsNullOrWhiteSpace(item.CategoryCode);
        var parentChanged = false;

        if (moveRequested)
        {
            var category = _categoryResolver.Resolve(item.CategoryCode);
            if (category is null)
            {
                return WriteOutcome.Failure("Unknown category code: " + item.CategoryCode);
            }

            (catalogId, nodeId) = category.Value;

            try
            {
                parentChanged = MovePrimaryNode(entryId, catalogId, nodeId);
            }
            // The same stale-cache retry the create path gets, and it matters MORE here: a nightly
            // feed is mostly updates, and without this a single deleted category poisons every
            // subsequent row that references it - nothing would ever invalidate the cached id, so
            // the failures repeat until the run gives up.
            catch (Exception ex) when (IsStaleCategoryReference(ex))
            {
                _logger.LogWarning(ex,
                    "Category {CategoryCode} resolved to a node that no longer exists while updating {Sku}. Retrying once.",
                    item.CategoryCode,
                    item.Sku);

                _categoryResolver.Invalidate(item.CategoryCode!);

                var fresh = _categoryResolver.Resolve(item.CategoryCode);
                if (fresh is null)
                {
                    return WriteOutcome.Failure("Unknown category code: " + item.CategoryCode);
                }

                (catalogId, nodeId) = fresh.Value;

                // If this second attempt throws, it escapes Update. The entry is then committed
                // with no primary node - MovePrimaryNode deletes before it adds - and no event is
                // raised for it. It is the same shape as the create-path orphan, and it self-heals
                // the same way: the next run finds primary == null and re-files it.
                parentChanged = MovePrimaryNode(entryId, catalogId, nodeId);
            }
        }

        if (item.RetailPrice.HasValue)
        {
            _priceWriter.SetPrices(item.Sku!, item.ListPrice, item.RetailPrice.Value);
        }

        // Every write is guarded. This is what turns a partial payload into a partial update
        // instead of nulling out every column the feed happened not to send this time.
        if (item.DisplayName is not null)
        {
            entryRow.Name = Truncate(item.DisplayName, 100);
        }

        if (item.Weight.HasValue)
        {
            var variations = entryRow.GetVariationRows();
            if (variations.Length > 0)
            {
                variations[0].Weight = item.Weight.Value;
            }
            else
            {
                // An entry with no variation row cannot carry a weight. Silently dropping the
                // value would be the worst outcome in an import whose whole promise is that
                // failures are visible, so say so rather than pretending the write happened.
                _logger.LogWarning(
                    "No variation row for {Sku}; weight {Weight} was not written.",
                    item.Sku,
                    item.Weight.Value);
            }
        }

        _catalog.SaveCatalogEntry(existing);

        var metaObject = _metaWriter.LoadOrCreate(entryId, metaClass);
        ApplyMetaFields(metaObject, item);
        _metaWriter.Persist(metaObject);

        CatalogChangeBatch.EntryChanged(entryRow.CatalogId, entryId, parentChanged ? nodeId : 0, parentChanged);
        return WriteOutcome.Success();
    }

    /// <summary>
    /// Soft delete. The entry row stays active and published - only the feed flag flips,
    /// so remember that your read side has to filter on it.
    /// </summary>
    private void Deactivate(CatalogEntryDto.CatalogEntryRow entryRow, MetaClass metaClass)
    {
        var metaObject = _metaWriter.LoadOrCreate(entryRow.CatalogEntryId, metaClass);
        metaObject.SetMetaField(nameof(Product.IsActiveInFeed), false);
        _metaWriter.Persist(metaObject);

        CatalogChangeBatch.EntryChanged(entryRow.CatalogId, entryRow.CatalogEntryId, nodeId: 0, parentChanged: false);
    }

    /// <summary>
    /// Resolves the meta class by the CLR type name of your catalog content type - the bridge that
    /// keeps the DTO layer and the content layer describing the same thing.
    ///
    /// Careful with that phrasing though: it holds only while [CatalogContentType.MetaClassName] is
    /// left unset. Set it, and the meta class is named by the attribute rather than by the type, and
    /// renaming the class becomes harmless - which is exactly why the benchmark jobs in this post
    /// set it explicitly.
    ///
    /// A null here means the content-type sync has not run, and saying so beats letting a
    /// NullReferenceException fall out of row one of a quarter-million-row feed.
    /// </summary>
    private MetaClass ResolveMetaClass() =>
        _metaClass ??= MetaClass.Load(_metaDataContext(), nameof(Product))
            ?? throw new InvalidOperationException(
                $"No meta class named '{nameof(Product)}'. Start the site once so the content-type " +
                "sync provisions it, and check the class has not been renamed.");

    /// <summary>
    /// The identity value is not reliably written back onto the row after SaveCatalogEntry, so we
    /// sometimes have to re-read it. Taking [0] blind would mean the id handed to the recursive
    /// delete in Create's catch block might belong to something we never created, so this filters
    /// on meta class AND catalog - the two facts we know about the row we just inserted. Returns 0
    /// when the row cannot be identified, which the caller must treat as "do not delete".
    ///
    /// Note this filter is deliberately STRICTER than the one in Upsert, which matches on meta
    /// class alone because it has not resolved a catalog yet. That looseness is a real limitation:
    /// if the same SKU exists under the same meta class in a different catalog, Upsert will take
    /// the update path against the foreign row. This writer assumes SKUs are unique per meta class
    /// across catalogs - true for a single-catalog solution, which is most of them, and worth
    /// checking before you paste it into a multi-catalog one.
    /// </summary>
    private int ResolveEntryId(
        CatalogEntryDto.CatalogEntryRow entryRow,
        string sku,
        MetaClass metaClass,
        int catalogId)
    {
        if (entryRow.CatalogEntryId > 0)
        {
            return entryRow.CatalogEntryId;
        }

        var match = _catalog.GetCatalogEntryDto(sku, _infoResponseGroup)
            .CatalogEntry
            .Cast<CatalogEntryDto.CatalogEntryRow>()
            .FirstOrDefault(e => e.MetaClassId == metaClass.Id && e.CatalogId == catalogId);

        return match?.CatalogEntryId ?? 0;
    }

    private void AddPrimaryNode(int entryId, int catalogId, int nodeId)
    {
        var relations = _catalog.GetCatalogRelationDto(entryId);
        relations.NodeEntryRelation.AddNodeEntryRelationRow(catalogId, entryId, nodeId, 0, true);
        _catalog.SaveCatalogRelationDto(relations);
    }

    /// <summary>
    /// Re-files an entry under a different category. The DTO cannot hold a deleted primary row
    /// and a new one at the same time, so this is delete -> save -> re-read -> add.
    /// </summary>
    private bool MovePrimaryNode(int entryId, int catalogId, int nodeId)
    {
        var relations = _catalog.GetCatalogRelationDto(entryId);
        var primary = relations.NodeEntryRelation
            .Cast<CatalogRelationDto.NodeEntryRelationRow>()
            .FirstOrDefault(r => r.IsPrimary);

        // Already where it belongs. Skipping here saves two round trips on every unchanged row,
        // and over a quarter of a million rows that is most of them.
        if (primary is not null && primary.CatalogNodeId == nodeId)
        {
            // Returns false so the caller can stamp HasChangedParent honestly. Passing "a category
            // code was supplied" as "the parent changed" makes the flag permanently true over a
            // quarter of a million mostly-unchanged rows, which makes it useless to subscribers.
            return false;
        }

        if (primary is not null)
        {
            primary.Delete();
            _catalog.SaveCatalogRelationDto(relations);
            relations = _catalog.GetCatalogRelationDto(entryId);
        }

        relations.NodeEntryRelation.AddNodeEntryRelationRow(catalogId, entryId, nodeId, 0, true);
        _catalog.SaveCatalogRelationDto(relations);
        return true;
    }

    /// <summary>
    /// No isCreate flag: Upsert rejects a null IsActive before either path runs, so the "default it
    /// to true on create" branch this used to carry was unreachable. Dead branches in published
    /// code are worse than missing ones - somebody will maintain them.
    /// </summary>
    private static void ApplyMetaFields(MetaObject metaObject, ProductImportItem item)
    {
        if (item.DisplayName is not null)
        {
            // Two things worth knowing about this one line.
            //
            // DisplayName is inherited from EntryContentBase and IS a MetaDataPlus field - 512
            // characters, and culture-specific. So it needs truncating like the 100-character Name
            // column does (a longer value throws at the MetaDataPlus layer, which surfaces as an
            // opaque "unexpected error" against a row whose actual problem is a long string), and
            // it is written per language. The IMetaObjectWriter behind this takes no language
            // parameter, which is fine for a single-language catalog and a bug waiting to happen
            // on a multi-language one.
            metaObject.SetMetaField(nameof(Product.DisplayName), Truncate(item.DisplayName, 512));
        }

        metaObject.SetMetaField(nameof(Product.IsActiveInFeed), item.IsActive!.Value);

        if (item.Volume is not null)
        {
            metaObject.SetMetaField(nameof(Product.Volume), item.Volume);
        }
    }

    /// <summary>
    /// Walks the whole exception tree, not just the outermost exception. By the time a Commerce
    /// write failure reaches us the SqlException is usually wrapped - and the rollback path above
    /// can wrap it again in an AggregateException. Testing only the top-level exception compiles,
    /// reads fine, and means the retry silently never fires.
    /// </summary>
    private static bool IsStaleCategoryReference(Exception ex) =>
        ExceptionTree.Flatten(ex).Any(inner =>
            inner is SqlException { Number: 547 } &&
            inner.Message.Contains(NodeEntryRelationConstraint, StringComparison.OrdinalIgnoreCase));

    private static string? FirstMissingRequiredField(ProductImportItem item)
    {
        if (string.IsNullOrWhiteSpace(item.DisplayName))
        {
            return nameof(item.DisplayName);
        }

        if (string.IsNullOrWhiteSpace(item.CategoryCode))
        {
            return nameof(item.CategoryCode);
        }

        return item.RetailPrice.HasValue ? null : nameof(item.RetailPrice);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
