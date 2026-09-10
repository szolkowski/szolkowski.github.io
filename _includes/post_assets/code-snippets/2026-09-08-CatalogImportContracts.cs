// ---------------------------------------------------------------------------------------------
// Supporting types for the three writer snippets.
//
// CatalogEntryWriter.cs, CatalogBulkImporter.cs and CatalogChangeBatch.cs are the illustrations
// from the post. The post introduces these companion types inline, in prose, rather than in the
// code modals - which reads better but means the three files do not compile on their own.
//
// This file closes that gap: drop it in alongside them and all four compile. It is not needed by
// the benchmark jobs, which are self-contained.
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

// ---------------------------------------------------------------------------------------------
// The importer's result model and its side of the job conversation.
// ---------------------------------------------------------------------------------------------

public sealed record ParsedRow<T>(int RowNumber, T? Record, string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record RejectedRecord(string Identifier, int RowNumber, string Error);

public sealed class ImportSection
{
    /// <summary>
    /// How many rejections are kept in full. The COUNT is always exact; this only bounds the
    /// detail list. Without a cap, a feed that rejects every row retains one object per row for
    /// the whole run - a quarter of a million of them on the file this post is about - and the
    /// summary built from them is unreadable anyway.
    /// </summary>
    public const int MaxRetainedRejections = 1_000;

    private readonly List<RejectedRecord> _rejections = new();

    public int Received { get; private set; }

    public int Accepted { get; private set; }

    public int Rejected { get; private set; }

    public IReadOnlyList<RejectedRecord> Rejections => _rejections;

    /// <summary>True when rejections were dropped from the detail list but still counted.</summary>
    public bool RejectionsTruncated => Rejected > MaxRetainedRejections;

    public void Receive() => Received++;

    public void Accept() => Accepted++;

    public void Reject(string identifier, int rowNumber, string error)
    {
        Rejected++;

        if (_rejections.Count < MaxRetainedRejections)
        {
            _rejections.Add(new RejectedRecord(identifier, rowNumber, error));
        }
    }
}

public sealed class ImportResult
{
    public ImportSection Products { get; } = new();

    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Set when a human pressed Stop. NOT set by an abort - the two are different outcomes and a
    /// consumer that renders "partial run" off this flag alone will render an abort as a complete
    /// one. Check both, or check Received against Accepted + Rejected.
    /// </summary>
    public bool Stopped { get; set; }

    /// <summary>
    /// Set when the run gave up early because the failures stopped looking like bad data and
    /// started looking like a broken dependency.
    ///
    /// Note that this is a FLAG, not an exception. Aborting still has to produce the report -
    /// the counts and the rejections gathered up to this point are the deliverable, and throwing
    /// them away to signal "something went wrong" would defeat the entire point of the design.
    /// </summary>
    public bool Aborted { get; set; }

    public string? AbortReason { get; set; }
}

/// <summary>
/// The job's side of the conversation. Status drives the progress line in the scheduled-jobs UI;
/// Rejected and Info become log entries.
/// </summary>
public interface IImportObserver
{
    bool StopRequested { get; }

    void Status(string message);

    void Rejected(string message);

    void Info(string message);
}
