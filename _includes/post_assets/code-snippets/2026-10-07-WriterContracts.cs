// ---------------------------------------------------------------------------------------------
// Supporting types for the catalog entry writer (Part 2).
//
// CatalogEntryWriter.cs introduces these companions in prose rather than in its code modal,
// which reads better but means the writer does not compile on its own. Drop this in alongside
// it and both build against a clean Optimizely Commerce 15 project.
//
// Part 3 also needs this file: CatalogBulkImporter uses ProductImportItem, WriteOutcome,
// ExceptionTree and ICatalogEntryWriter from here.
// ---------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;

using Mediachase.MetaDataPlus;
using Mediachase.MetaDataPlus.Configurator;

#nullable enable

namespace CatalogImport;

// ---------------------------------------------------------------------------------------------
// The row being imported, and the writer's result type.
// ---------------------------------------------------------------------------------------------

public sealed class ProductImportItem
{
    /// <summary>The natural key. Upserting on it is what makes replaying a feed safe.</summary>
    public string? Sku { get; set; }

    public string? DisplayName { get; set; }

    public string? CategoryCode { get; set; }

    public bool? IsActive { get; set; }

    public decimal? RetailPrice { get; set; }

    public decimal? ListPrice { get; set; }

    public double? Weight { get; set; }

    public string? Volume { get; set; }
}

public sealed record WriteOutcome(bool Succeeded, string? Error)
{
    public static WriteOutcome Success() => new(true, null);

    public static WriteOutcome Failure(string error) => new(false, error);
}

// ---------------------------------------------------------------------------------------------
// The catalog content type the writer targets by name.
//
// DELIBERATELY NOT a [CatalogContentType]. CatalogEntryWriter only ever uses this through
// nameof(Product) and nameof(Product.X), so a plain class is enough - and marking it up as a real
// catalog content type would provision a second meta class in your Commerce database the moment
// you started the site, which is not something a set of article snippets should do to you.
//
// In a real solution this is your own [CatalogContentType] VariationContent subclass.
// ---------------------------------------------------------------------------------------------

public sealed class Product
{
    public string DisplayName { get; set; } = string.Empty;

    public bool IsActiveInFeed { get; set; }

    public string Volume { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------------------------
// Exception-tree walking.
//
// Shared rather than duplicated, because both places that need it need it for the same reason:
// by the time a Commerce failure reaches you the interesting exception is usually wrapped, and
// testing only the outermost one compiles, reads fine, and silently never matches.
// ---------------------------------------------------------------------------------------------

public static class ExceptionTree
{
    public static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
            {
                yield return inner;
            }
        }
        else if (exception.InnerException is not null)
        {
            foreach (var inner in Flatten(exception.InnerException))
            {
                yield return inner;
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------
// The writer's collaborators.
// ---------------------------------------------------------------------------------------------

public interface ICatalogEntryWriter
{
    WriteOutcome Upsert(ProductImportItem item);
}

/// <summary>
/// Caches code -> (catalogId, nodeId), because resolving it per row is a round trip you would
/// otherwise pay 250,000 times. Invalidate exists for the stale-node retry.
/// </summary>
public interface ICategoryResolver
{
    (int CatalogId, int NodeId)? Resolve(string? categoryCode);

    void Invalidate(string categoryCode);
}

/// <summary>
/// Wraps IPriceService. See the throughput section of the post for why this deserves a batching
/// overload rather than the per-SKU call the writer makes.
/// </summary>
public interface ICatalogPriceWriter
{
    void SetPrices(string entryCode, decimal? listPrice, decimal salePrice);
}

/// <summary>
/// Load-or-create a MetaObject and persist it only when it is actually dirty.
/// </summary>
public interface IMetaObjectWriter
{
    MetaObject LoadOrCreate(int objectId, MetaClass metaClass);

    void Persist(MetaObject metaObject);
}
