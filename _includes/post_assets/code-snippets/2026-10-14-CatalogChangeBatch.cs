using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Mediachase.Commerce.Catalog.Events;

#nullable enable

namespace CatalogImport;

/// <summary>
/// Collects catalog changes made through the DTO API and raises a single coalesced
/// Commerce event when the scope closes, instead of one event per written row.
///
/// The DTO API raises nothing on your behalf. Without this, either nobody hears about
/// the import at all, or - if you raise per row - a 250,000-row feed turns into 250,000
/// event broadcasts, which on a multi-instance setup is 250,000 Service Bus messages.
/// </summary>
public static class CatalogChangeBatch
{
    // AsyncLocal, not a plain static: the ambient batch follows the logical call stack, so a
    // writer several layers down can report a change without every method signature growing a
    // batch parameter, and two independent imports do not share one batch.
    //
    // READ THAT LAST CLAUSE CAREFULLY - it means two independent *flows*. AsyncLocal flows INTO
    // child tasks, so parallelising the row loop (Parallel.ForEach, Task.WhenAll over rows) gives
    // every worker the SAME Batch instance, concurrently mutating plain HashSet<int> fields and
    // doing a non-atomic |= on the parent flags. That is a corrupted-hashtable bug, not a
    // lost-update bug, and parallelising the loop is the most obvious next optimisation for an
    // importer running at a few rows per second. If you go there, lock the batch or swap the sets
    // for ConcurrentDictionary<int, byte> first.
    private static readonly AsyncLocal<Batch?> _current = new();

    /// <summary>
    /// Invoked when a subscriber throws during the batch broadcast. Wire this to your logging once
    /// at startup. Leaving it null swallows the failure - which is the right trade for an import
    /// (a bad subscriber must not kill the run) and the wrong one for silence, so wire it up.
    /// </summary>
    public static Action<Exception>? OnBroadcastFailed { get; set; }

    /// <summary>
    /// Opens a batch scope. One per batch, and they do not nest.
    ///
    /// Nesting used to be "supported" here, which meant an inner scope raised its own events and
    /// its ids were never folded into the outer one - the opposite of coalescing, dressed up as a
    /// feature. Rather than half-support it, this throws: a nested Begin() is a bug in the caller,
    /// and finding out at the call site beats finding out from a missing broadcast.
    /// </summary>
    public static IDisposable Begin()
    {
        if (_current.Value is not null)
        {
            throw new InvalidOperationException(
                "A catalog change batch is already open on this flow. Batches do not merge - open " +
                "exactly one per batch.");
        }

        var batch = new Batch();
        _current.Value = batch;
        return batch;
    }

    public static void EntryChanged(int catalogId, int entryId, int nodeId, bool parentChanged)
    {
        var batch = _current.Value;

        // No open scope means no event. This is deliberate - but it is also the trap:
        // a writer called outside Begin() writes rows and tells nobody. If you split the
        // writers out for reuse, make the scope a precondition, not an assumption.
        if (batch is null)
        {
            return;
        }

        Add(batch.CatalogIds, catalogId);
        Add(batch.EntryIds, entryId);
        Add(batch.EntryParentNodeIds, nodeId);
        batch.EntryParentChanged |= parentChanged;
    }

    /// <summary>
    /// For the category writer, which this post does not show. Nothing in the entry writer calls
    /// it - noted so you can tell which paths here are exercised and which are the same shape
    /// waiting for the sibling class.
    /// </summary>
    public static void NodeChanged(int catalogId, int nodeId, bool parentChanged)
    {
        var batch = _current.Value;
        if (batch is null)
        {
            return;
        }

        Add(batch.CatalogIds, catalogId);
        Add(batch.NodeIds, nodeId);
        batch.NodeParentChanged |= parentChanged;
    }

    /// <summary>
    /// Also unused by the writer shown here. Note what does NOT call it: the rollback in
    /// CatalogEntryWriter.Create. That is deliberate - the entry was created and deleted inside
    /// one failed row, nothing downstream ever heard it existed, and announcing a delete for it
    /// would be noise about a row that never logically arrived.
    /// </summary>
    public static void EntryDeleted(int catalogId, int entryId)
    {
        var batch = _current.Value;
        if (batch is null)
        {
            return;
        }

        Add(batch.CatalogIds, catalogId);
        Add(batch.DeletedEntryIds, entryId);
    }

    /// <summary>
    /// Ids come back as 0 from rows that were never persisted. Sets also give us
    /// de-duplication for free: a SKU touched twice in one batch is reported once.
    /// </summary>
    private static void Add(HashSet<int> set, int id)
    {
        if (id > 0)
        {
            set.Add(id);
        }
    }

    private sealed class Batch : IDisposable
    {
        private bool _disposed;

        public HashSet<int> CatalogIds { get; } = new();

        public HashSet<int> NodeIds { get; } = new();

        public HashSet<int> EntryIds { get; } = new();

        public HashSet<int> EntryParentNodeIds { get; } = new();

        public HashSet<int> DeletedEntryIds { get; } = new();

        public bool NodeParentChanged { get; set; }

        public bool EntryParentChanged { get; set; }

        public void Dispose()
        {
            // Disposing twice would re-raise every event. `using` never does this, but Begin()
            // hands out a public IDisposable and callers do surprising things with those.
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Clear the slot BEFORE broadcasting, not after. RaiseEvent runs subscriber code
            // synchronously, and a subscriber that writes catalog rows of its own would otherwise
            // either accumulate them into a batch that has already broadcast (lost silently) or
            // call Begin() and hit the "already open" throw. Once we are past the last write,
            // no batch is open as far as anyone else is concerned.
            _current.Value = null;

            // Each raise is isolated. RaiseEvent runs subscriber code synchronously, so one
            // badly-behaved subscriber would otherwise (a) suppress the two broadcasts below it
            // and (b) throw out of Dispose, out of the caller's `using`, and kill an import that
            // has spent three sections of this post learning not to die on one bad row.
            if (NodeIds.Count > 0)
            {
                RaiseSafely(CatalogEventBroadcaster.CatalogNodeUpdatedEventType,
                    NodeIds, Array.Empty<int>(), NodeParentChanged);
            }

            if (EntryIds.Count > 0)
            {
                RaiseSafely(CatalogEventBroadcaster.CatalogEntryUpdatedEventType,
                    EntryParentNodeIds, EntryIds, EntryParentChanged);
            }

            if (DeletedEntryIds.Count > 0)
            {
                RaiseSafely(CatalogEventBroadcaster.CatalogEntryDeletedEventType,
                    Array.Empty<int>(), DeletedEntryIds, hasChangedParent: false);
            }
        }

        private void RaiseSafely(
            string eventType,
            IReadOnlyCollection<int> nodeIds,
            IReadOnlyCollection<int> entryIds,
            bool hasChangedParent)
        {
            try
            {
                Raise(eventType, nodeIds, entryIds, hasChangedParent);
            }
            catch (Exception ex)
            {
                // Guarded too. The post tells you to wire this to your logging, and a logging sink
                // that throws would recreate the exact failure RaiseSafely exists to prevent.
                try { OnBroadcastFailed?.Invoke(ex); }
                catch { /* a failing failure handler is not this class's problem */ }
            }
        }

        private void Raise(
            string eventType,
            IReadOnlyCollection<int> nodeIds,
            IReadOnlyCollection<int> entryIds,
            bool hasChangedParent) =>
            CatalogEventBroadcaster.RaiseEvent(new CatalogContentUpdateEventArgs
            {
                EventType = eventType,

                // Copies, though the properties are IEnumerable<int> and would accept the sets
                // directly. Three allocations per batch - not per row - to avoid handing a
                // subscriber a live reference to collections this class still owns.
                CatalogIds = CatalogIds.ToArray(),
                CatalogNodeIds = nodeIds.ToArray(),
                CatalogEntryIds = entryIds.ToArray(),
                HasChangedParent = hasChangedParent,
            });
    }
}
