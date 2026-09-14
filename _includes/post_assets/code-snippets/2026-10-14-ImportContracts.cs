// ---------------------------------------------------------------------------------------------
// Supporting types for the bulk importer (Part 3).
//
// Requires WriterContracts.cs from Part 2. CatalogBulkImporter reaches into it for
// ProductImportItem, WriteOutcome, ExceptionTree and ICatalogEntryWriter; this file adds only
// the parse-and-report types the importer introduces on its own.
//
// Neither file is needed by the benchmark jobs in Part 5, which are self-contained.
// ---------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;

#nullable enable

namespace CatalogImport;

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
