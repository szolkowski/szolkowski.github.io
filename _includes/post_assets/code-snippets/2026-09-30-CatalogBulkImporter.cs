using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;

using EPiServer.Authorization;
using EPiServer.Security;

using Microsoft.Extensions.Logging;

#nullable enable

namespace CatalogImport;

/// <summary>
/// Drives the import: chunk the rows, verify each one, write it, and record the outcome.
/// A single bad row is an outcome to report, never a reason to stop.
/// </summary>
public class CatalogBulkImporter
{
    // Big enough that the per-batch overhead disappears, small enough that a Stop click
    // lands within a few seconds and progress reporting stays useful.
    private const int BatchSize = 500;

    // Logging every batch is noise; logging none of them is a black box.
    private const int BatchLogInterval = 10;

    // Past this many rejections we stop narrating them one by one. See Reject below.
    private const int MaxIndividuallyReportedRejections = 200;

    // Consecutive THROWN failures that mean "the dependency is down", not "the feed is dirty".
    // Returned failures - a missing price, an unknown category - are bad data and do not count:
    // a feed can legitimately be 100% rejects and that is a report, not an outage.
    private const int MaxConsecutiveFailures = 100;

    private readonly ICatalogEntryWriter _writer;
    private readonly IPrincipalAccessor _principalAccessor;
    private readonly ILogger<CatalogBulkImporter> _logger;

    private int _consecutiveFailures;

    public CatalogBulkImporter(
        ICatalogEntryWriter writer,
        IPrincipalAccessor principalAccessor,
        ILogger<CatalogBulkImporter> logger)
    {
        _writer = writer;
        _principalAccessor = principalAccessor;
        _logger = logger;
    }

    public ImportResult Import(IEnumerable<ParsedRow<ProductImportItem>> rows, IImportObserver observer)
    {
        var result = new ImportResult();
        var totalStopwatch = Stopwatch.StartNew();

        // Per-run state, reset per run. As an instance field it would otherwise carry over: end
        // one run at 99 consecutive failures and the next would trip on its first row.
        _consecutiveFailures = 0;

        var batchNumber = 0;
        var processed = 0;

        foreach (var batch in rows.Chunk(BatchSize))
        {
            // Stop is cooperative and checked at batch boundaries only. Rows already written
            // stay written - a stopped import is a partial success, and the report says so.
            if (observer.StopRequested)
            {
                result.Stopped = true;
                break;
            }

            batchNumber++;
            var batchStopwatch = Stopwatch.StartNew();

            ProcessBatch(batch, result, observer);

            batchStopwatch.Stop();

            // Break here, before any counting or narration. An aborting batch stops partway
            // through its rows, so adding batch.Length would report rows we never attempted and
            // the throughput line below would divide a partial batch by a full one. In a design
            // whose whole claim is that the report is the deliverable, a final status line that
            // overstates what happened is not a rounding error.
            if (result.Aborted)
            {
                break;
            }

            processed += batch.Length;

            observer.Status($"Products: processed {processed} rows");

            if (batchNumber == 1 || batchNumber % BatchLogInterval == 0)
            {
                var seconds = batchStopwatch.Elapsed.TotalSeconds;
                var rate = seconds > 0 ? $"{batch.Length / seconds:N0} rows/s" : "n/a";
                observer.Info($"Batch {batchNumber}: {batch.Length} rows in {seconds:N2} s ({rate})");
            }
        }

        totalStopwatch.Stop();
        result.Duration = totalStopwatch.Elapsed;

        // The counts are always exact; the detail list is capped. Say so, otherwise a caller
        // reading "every failed row" off a truncated list draws the wrong conclusion silently.
        if (result.Products.RejectionsTruncated)
        {
            // Two different caps, and they are easy to confuse: this narration stops at
            // MaxIndividuallyReportedRejections, while the in-memory detail list on the result
            // stops at MaxRetainedRejections. Only the totals are uncapped.
            observer.Info(
                $"{result.Products.Rejected:N0} rejections in total. " +
                $"{MaxIndividuallyReportedRejections:N0} were listed individually above and " +
                $"{ImportSection.MaxRetainedRejections:N0} are retained on the result; " +
                "the total is exact.");
        }

        return result;
    }

    private void ProcessBatch(
        ParsedRow<ProductImportItem>[] batch,
        ImportResult result,
        IImportObserver observer)
    {
        // The DTO API reads the ambient principal, and a scheduled job has not got one.
        // Elevate for the batch, restore afterwards - and open the event batch in the same
        // scope so exactly one coalesced catalog event is raised per 500 rows.
        var previousPrincipal = _principalAccessor.Principal;
        _principalAccessor.Principal = new GenericPrincipal(
            new GenericIdentity(nameof(CatalogBulkImporter)),
            new[] { Roles.Administrators });

        try
        {
            // Begin() throws if a scope is already open on this flow. That is a caller bug, but it
            // must not become an exception that escapes Import and takes the report with it - the
            // same reasoning as the circuit breaker below. Record it as an abort instead.
            IDisposable scope;
            try
            {
                scope = CatalogChangeBatch.Begin();
            }
            catch (InvalidOperationException ex)
            {
                result.Aborted = true;
                result.AbortReason = "Could not open a catalog change batch: " + ex.Message;
                observer.Info(result.AbortReason);
                return;
            }

            // Note the explicit `using` block rather than `using var`. A `using var` here would
            // dispose at the end of the METHOD - after the finally below has already restored the
            // principal - so the coalesced broadcast, and every subscriber it runs synchronously,
            // would execute unelevated. That is the exact condition the elevation exists to
            // prevent, and it is an easy one to introduce by "tidying" this into a using var.
            using (scope)
            {
                foreach (var row in batch)
                {
                    if (result.Aborted)
                    {
                        break;
                    }

                    result.Products.Receive();
                    ProcessRow(row, result, observer);
                }
            }
        }
        finally
        {
            _principalAccessor.Principal = previousPrincipal;
        }
    }

    private void ProcessRow(
        ParsedRow<ProductImportItem> row,
        ImportResult result,
        IImportObserver observer)
    {
        // Layer 1 - the row never parsed. It still gets counted and reported, with its
        // physical line number, because "row 148213 had an unescaped quote" is the only
        // thing that lets anyone fix the feed.
        if (!row.Succeeded || row.Record is null)
        {
            Reject(result, observer, "(unparseable)", row.RowNumber, row.Error ?? "Row could not be parsed.");
            return;
        }

        var item = row.Record;
        var identifier = item.Sku ?? "(no sku)";

        // Layer 2 - expected failures. The writer returns them, it does not throw them.
        // Reserve exceptions for the genuinely unexpected.
        WriteOutcome outcome;
        try
        {
            outcome = _writer.Upsert(item);
        }
        // Layer 3 - the unexpected. One try/catch per record is the whole soft-fail mechanism:
        // this row is lost, the other 249,999 are not. Note the filter: OutOfMemoryException is
        // not a bad row, it is a dead process, and "carry on and reject the remaining 249,000
        // one at a time" is the worst possible response to it.
        //
        // And note that the filter walks the tree rather than testing `ex` directly. The writer
        // wraps a failed rollback in an AggregateException, so an OOM can arrive here already
        // nested - at which point a top-level test matches nothing and the guard silently does
        // not guard. Same trap the writer's own stale-node check documents.
        catch (Exception ex) when (!ExceptionTree.Flatten(ex).Any(e => e is OutOfMemoryException))
        {
            _logger.LogError(ex, "Unexpected error importing {Sku} at row {RowNumber}.", identifier, row.RowNumber);
            Reject(result, observer, identifier, row.RowNumber, "Unexpected error: " + ex.Message);

            // A run of consecutive failures is not a bad feed, it is a broken dependency - a
            // dropped connection, a full disk. Rejecting every remaining row individually would
            // take hours and produce a report nobody can read, so stop and say why.
            //
            // Note what this does NOT do: throw. `result` is a local, so throwing out of here
            // would destroy every count and every rejection gathered so far and hand the caller
            // one row's exception message instead of the report. Aborting is an outcome like any
            // other failure in this design - it is recorded, it is explained, and the partial
            // report still comes back.
            if (++_consecutiveFailures >= MaxConsecutiveFailures)
            {
                result.Aborted = true;
                result.AbortReason =
                    $"{MaxConsecutiveFailures} rows threw in a row (last: {ex.Message}); aborting " +
                    "rather than rejecting the rest of the feed one row at a time.";
                observer.Info(result.AbortReason);
            }

            return;
        }

        if (outcome.Succeeded)
        {
            // Reset only on an actual success. Resetting on a RETURNED failure too would mean one
            // malformed row interleaved among timeouts keeps the breaker permanently disarmed.
            _consecutiveFailures = 0;
            result.Products.Accept();
        }
        else
        {
            Reject(result, observer, identifier, row.RowNumber, outcome.Error!);
        }
    }

    /// <summary>
    /// Records a rejection and reports it in one place, so a rejection can never be counted
    /// without being reported or reported without being counted.
    ///
    /// Individual reporting is capped. Every observer.Rejected call is a log write, and a log
    /// write is a database round trip - so a feed that rejects every row would otherwise turn
    /// into 250,000 round trips serialised on the job thread, which is slower than importing the
    /// file successfully and looks exactly like a hang. The counts stay exact; only the
    /// row-by-row narration stops.
    /// </summary>
    private static void Reject(
        ImportResult result,
        IImportObserver observer,
        string identifier,
        int rowNumber,
        string error)
    {
        result.Products.Reject(identifier, rowNumber, error);

        if (result.Products.Rejected <= MaxIndividuallyReportedRejections)
        {
            observer.Rejected($"Row {rowNumber} - {identifier} - {error}");
        }
        else if (result.Products.Rejected == MaxIndividuallyReportedRejections + 1)
        {
            observer.Rejected(
                $"More than {MaxIndividuallyReportedRejections} rejections; further rows will be " +
                "counted but not listed individually. See the summary for the total.");
        }
    }
}
