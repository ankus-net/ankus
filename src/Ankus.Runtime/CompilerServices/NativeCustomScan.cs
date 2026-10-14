using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.CompilerServices;

/// <summary>
/// Mirrors the request a generated custom scan bridge passes to its managed provider.
/// </summary>
/// <remarks>
/// Native code owns the borrowed name and data buffers. Managed code owns the output value until native code
/// releases it through the value's own release callback.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
[StructLayout(LayoutKind.Sequential)]
public struct NativeCustomScanRequest
{
    /// <summary>
    /// The planner's row estimate for the table.
    /// </summary>
    internal double _rows;

    /// <summary>
    /// The lowest startup cost among the table's existing paths.
    /// </summary>
    internal double _cheapestStartupCost;

    /// <summary>
    /// The lowest total cost among the table's existing paths.
    /// </summary>
    internal double _cheapestTotalCost;

    /// <summary>
    /// The rooted scan state, or zero before the scan begins and after it ends.
    /// </summary>
    internal nint _state;

    /// <summary>
    /// The table's OID.
    /// </summary>
    internal uint _relation;

    /// <summary>
    /// The table's row type OID.
    /// </summary>
    internal uint _rowType;

    /// <summary>
    /// One when the executor only explains the plan.
    /// </summary>
    internal int _flags;

    /// <summary>
    /// The number of columns in each row, including dropped columns.
    /// </summary>
    internal int _columns;

    /// <summary>
    /// The table's name as borrowed UTF-8.
    /// </summary>
    internal NativeValue _relationName;

    /// <summary>
    /// The table's schema name as borrowed UTF-8.
    /// </summary>
    internal NativeValue _schemaName;

    /// <summary>
    /// The chosen path's borrowed data.
    /// </summary>
    internal NativeValue _data;

    /// <summary>
    /// One borrowed byte per column, nonzero when PostgreSQL reads the column from the scan's rows.
    /// </summary>
    internal NativeValue _read;

    /// <summary>
    /// Paths or EXPLAIN properties encoded for native code, owned by managed code until released.
    /// </summary>
    internal NativeValue _output;

    /// <summary>
    /// The parallel scan's dynamic shared memory, or zero outside parallel setup.
    /// </summary>
    internal nint _shared;

    /// <summary>
    /// The bytes of shared memory the provider's layout needs.
    /// </summary>
    internal nint _sharedSize;
}

/// <summary>
/// Runs a custom scan provider's planning, execution and EXPLAIN calls for generated native bridges.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class NativeCustomScan
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    /// <summary>
    /// Dispatches one native custom scan operation to the provider and reports its outcome.
    /// </summary>
    /// <typeparam name="TState">The provider's per-scan state.</typeparam>
    /// <param name="operation">
    /// 0 plans, 1 begins, 2 produces a row, 3 rescans, 4 ends, 5 ends during abort, 6 explains, 7 produces the previous row,
    /// 8 marks the position and 9 restores it. A parallel provider's overload also handles operations 10 to 14.
    /// </param>
    /// <param name="request">The native request.</param>
    /// <param name="row">The column values a produced row fills, released by native code.</param>
    /// <param name="error">The diagnostic written when the provider fails.</param>
    /// <param name="execute">The backend operation entry for this call.</param>
    /// <param name="memory">The memory context API for this call.</param>
    /// <param name="plan">The provider's planning call.</param>
    /// <param name="begin">The provider's scan start.</param>
    /// <param name="read">The provider's row reader.</param>
    /// <param name="rescan">The provider's rescan, or null to dispose and begin again.</param>
    /// <param name="explain">The provider's EXPLAIN writer, or null when it adds no properties.</param>
    /// <param name="readBackward">The provider's backward row reader, or null when it reads only forward.</param>
    /// <param name="mark">The provider's position mark, or null when it has none.</param>
    /// <param name="restore">The provider's position restore, or null when it has none.</param>
    /// <param name="parallel">Whether the provider can offer parallel paths.</param>
    /// <returns>0 when the operation succeeded or produced a row, 1 after an error and 2 when the scan has no more rows.</returns>
    public static int Dispatch<TState>(int operation, NativeCustomScanRequest* request, NativeValue* row, NativeCallError* error, nint execute,
        nint memory, delegate*<PgCustomScanPlanner, void> plan, delegate*<PgCustomScanContext, TState> begin,
        delegate*<TState, PgHeapTuple, bool> read, delegate*<TState, void> rescan, delegate*<TState, PgExplainWriter, void> explain,
        delegate*<TState, PgHeapTuple, bool> readBackward, delegate*<TState, void> mark, delegate*<TState, void> restore, bool parallel)
        where TState : class
    {
        bool abortCleanup = operation == 5;
        nint previous = NativeBackend.Enter(execute, abortCleanup);
        nint previousMemory = 0;
        bool memoryEntered = false;
        try
        {
            previousMemory = NativeMemoryContext.Enter(memory);
            memoryEntered = true;
            switch (operation)
            {
                case 0:
                    Plan(request, plan, parallel);
                    return 0;
                case 1:
                    request->_state = Begin(request, begin);
                    return 0;
                case 2:
                    return Read(request, row, read) ? 0 : 2;
                case 3:
                    Rescan(request, begin, rescan);
                    return 0;
                case 4:
                case 5:
                    End<TState>(ref request->_state);
                    return 0;
                case 6:
                    Explain(request, explain);
                    return 0;
                case 7 when readBackward != null:
                    return Read(request, row, readBackward) ? 0 : 2;
                case 8 when mark != null:
                    mark(Get<TState>(request->_state).State!);
                    return 0;
                case 9 when restore != null:
                    restore(Get<TState>(request->_state).State!);
                    return 0;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown or unsupported custom scan operation.");
            }
        }
        // codeql[cs/catch-of-all-exceptions] Native code calls this, so every exception goes back to PostgreSQL as an error instead of unwinding through it.
        catch (Exception exception)
        {
            NativeError.Write(exception, error);
            return 1;
        }
        finally
        {
            if (memoryEntered)
            {
                NativeMemoryContext.Exit(previousMemory);
            }

            NativeBackend.Exit(previous, abortCleanup);
        }
    }

    /// <summary>
    /// Dispatches one native operation to a parallel provider, including the shared memory setup the leader and workers
    /// run.
    /// </summary>
    /// <typeparam name="TState">The provider's per-scan state.</typeparam>
    /// <typeparam name="TShared">The provider's shared memory layout.</typeparam>
    /// <param name="operation">
    /// The operations of <see cref="Dispatch{TState}"/>, and 10 sizes the shared memory, 11 initializes and attaches it in the
    /// leader, 12 initializes it again before a rescan, 13 attaches it in a worker and 14 detaches it at shutdown.
    /// </param>
    /// <param name="request">The native request.</param>
    /// <param name="row">The column values a produced row fills, released by native code.</param>
    /// <param name="error">The diagnostic written when the provider fails.</param>
    /// <param name="execute">The backend operation entry for this call.</param>
    /// <param name="memory">The memory context API for this call.</param>
    /// <param name="plan">The provider's planning call.</param>
    /// <param name="begin">The provider's scan start.</param>
    /// <param name="read">The provider's row reader.</param>
    /// <param name="rescan">The provider's rescan, or null to dispose and begin again.</param>
    /// <param name="explain">The provider's EXPLAIN writer, or null when it adds no properties.</param>
    /// <param name="readBackward">The provider's backward row reader, or null when it reads only forward.</param>
    /// <param name="mark">The provider's position mark, or null when it has none.</param>
    /// <param name="restore">The provider's position restore, or null when it has none.</param>
    /// <param name="initializeShared">The provider's shared memory initialization.</param>
    /// <param name="attachShared">The provider's shared memory attachment.</param>
    /// <returns>0 when the operation succeeded or produced a row, 1 after an error and 2 when the scan has no more rows.</returns>
    public static int Dispatch<TState, TShared>(int operation, NativeCustomScanRequest* request, NativeValue* row, NativeCallError* error,
        nint execute, nint memory, delegate*<PgCustomScanPlanner, void> plan, delegate*<PgCustomScanContext, TState> begin,
        delegate*<TState, PgHeapTuple, bool> read, delegate*<TState, void> rescan, delegate*<TState, PgExplainWriter, void> explain,
        delegate*<TState, PgHeapTuple, bool> readBackward, delegate*<TState, void> mark, delegate*<TState, void> restore,
        delegate*<TState, ref TShared, void> initializeShared, delegate*<TState, PgCustomScanSharedMemory<TShared>, void> attachShared)
        where TState : class
        where TShared : unmanaged
    {
        if (operation < 10)
        {
            return Dispatch(operation, request, row, error, execute, memory, plan, begin, read, rescan, explain, readBackward, mark, restore, true);
        }

        nint previous = NativeBackend.Enter(execute, abortCleanup: false);
        nint previousMemory = 0;
        bool memoryEntered = false;
        try
        {
            previousMemory = NativeMemoryContext.Enter(memory);
            memoryEntered = true;
            if (operation == 10)
            {
                request->_sharedSize = sizeof(TShared);
                return 0;
            }

            Scan<TState> scan = Get<TState>(request->_state);
            if (operation == 14)
            {
                scan.DetachShared();
                return 0;
            }

            if (request->_shared == 0 || operation > 14)
            {
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown or unsupported custom scan operation.");
            }

            var shared = (TShared*)request->_shared;
            if (operation != 13)
            {
                // PostgreSQL does not clear shared memory, and a rescan reuses the memory the last workers left.
                *shared = default;
                initializeShared(scan.State!, ref *shared);
                if (operation == 12 && scan.Shared is not null)
                {
                    return 0;
                }
            }

            scan.DetachShared();
            var handle = new PgCustomScanSharedMemory<TShared>(shared);
            scan.Shared = handle;
            scan.AttachShared = state => attachShared(state, handle);
            scan.AttachShared(scan.State!);
            return 0;
        }
        // codeql[cs/catch-of-all-exceptions] Native code calls this, so every exception goes back to PostgreSQL as an error instead of unwinding through it.
        catch (Exception exception)
        {
            NativeError.Write(exception, error);
            return 1;
        }
        finally
        {
            if (memoryEntered)
            {
                NativeMemoryContext.Exit(previousMemory);
            }

            NativeBackend.Exit(previous, abortCleanup: false);
        }
    }

    private static void Plan(NativeCustomScanRequest* request, delegate*<PgCustomScanPlanner, void> plan, bool parallel)
    {
        var paths = new List<PgCustomScanPath>();
        byte[] read = request->_read.ReadBytes();
        plan(new PgCustomScanPlanner(request->_relation, request->_schemaName.ReadString(), request->_relationName.ReadString(),
            request->_rows, request->_cheapestStartupCost, request->_cheapestTotalCost, paths, request->_rowType, request->_columns, read,
            parallel));
        var buffer = new ArrayBufferWriter<byte>();
        WriteInt(buffer, paths.Count);
        foreach (PgCustomScanPath path in paths)
        {
            WriteDouble(buffer, path.StartupCost);
            WriteDouble(buffer, path.TotalCost);
            WriteInt(buffer, path.Rows is null ? 0 : 1);
            WriteDouble(buffer, path.Rows ?? 0);
            WriteInt(buffer, path.Data.Length);
            buffer.Write(path.Data.Span);
            WriteInt(buffer, path.Workers);
            IReadOnlyList<PgCustomScanSortKey> keys = path.SortKeys ?? [];
            WriteInt(buffer, keys.Count);
            foreach (PgCustomScanSortKey key in keys)
            {
                WriteInt(buffer, key.Ordinal);
                WriteInt(buffer, (key.Descending ? 1 : 0) | (key.NullsFirst ? 2 : 0));
            }
        }

        request->_output = NativeValue.FromBytes(buffer.WrittenSpan);
    }

    private static nint Begin<TState>(NativeCustomScanRequest* request, delegate*<PgCustomScanContext, TState> begin)
        where TState : class
    {
        PgTupleDescriptor descriptor = PgTupleDescriptor.Load(request->_rowType);
        if (descriptor.Attributes.Count != request->_columns)
        {
            throw new InvalidOperationException("The custom scan table's row type does not match its scan columns.");
        }

        byte[] data = request->_data.ReadBytes();
        byte[] read = request->_read.ReadBytes();
        bool explainOnly = (request->_flags & 1) != 0;
        TState state = begin(new PgCustomScanContext(request->_relation, descriptor, data, explainOnly, read))
            ?? throw new InvalidOperationException("A custom scan provider's Begin returned null.");
        try
        {
            return GCHandle.ToIntPtr(GCHandle.Alloc(new Scan<TState>(state, request->_relation, descriptor, data, explainOnly, read)));
        }
        catch
        {
            (state as IDisposable)?.Dispose();
            throw;
        }
    }

    private static bool Read<TState>(NativeCustomScanRequest* request, NativeValue* row, delegate*<TState, PgHeapTuple, bool> read)
        where TState : class
    {
        Scan<TState> scan = Get<TState>(request->_state);
        PgHeapTuple tuple = scan.Descriptor.CreateTuple();
        if (!read(scan.State!, tuple))
        {
            return false;
        }

        int written = 0;
        try
        {
            for (; written < tuple.Count; written++)
            {
                PgTupleAttributeInfo attribute = scan.Descriptor.Attributes[written];
                object? cell = attribute.IsDropped || attribute.IsUnavailable ? null : tuple.GetStoredCell(written);
                if (cell is null)
                {
                    // A column PostgreSQL does not read can stay unset; nothing sees its NULL.
                    if (attribute.IsNotNull && !attribute.IsDropped && (written >= scan.Read.Length || scan.Read[written] != 0))
                    {
                        throw new InvalidOperationException($"Custom scan rows cannot hold NULL in NOT NULL column '{attribute.Name}'.");
                    }

                    row[written] = default;
                    row[written].IsNull = 1;
                    continue;
                }

                row[written] = SpiType.ToNative(cell, PgTypeRegistry.FindValue(cell, attribute.BaseTypeOid),
                    cell is Array array ? PgTypeRegistry.FindArrayValue(array, attribute.BaseTypeOid) : null);
            }
        }
        catch
        {
            for (int index = 0; index < written; index++)
            {
                row[index].Release();
            }

            throw;
        }

        return true;
    }

    private static void Rescan<TState>(NativeCustomScanRequest* request, delegate*<PgCustomScanContext, TState> begin,
        delegate*<TState, void> rescan)
        where TState : class
    {
        Scan<TState> scan = Get<TState>(request->_state);
        if (rescan != null)
        {
            rescan(scan.State!);
            return;
        }

        // Without a rescan capability the scan starts over with the context it began with, and keeps a parallel scan's
        // shared memory.
        TState? previous = scan.State;
        scan.State = null;
        (previous as IDisposable)?.Dispose();
        scan.State = begin(new PgCustomScanContext(scan.Relation, scan.Descriptor, scan.Data, scan.ExplainOnly, scan.Read))
            ?? throw new InvalidOperationException("A custom scan provider's Begin returned null.");
        scan.AttachShared?.Invoke(scan.State);
    }

    private static void End<TState>(ref nint handle)
        where TState : class
    {
        if (handle == 0)
        {
            return;
        }

        var root = GCHandle.FromIntPtr(handle);
        handle = 0;
        object? target = root.Target;
        root.Free();
        if (target is not Scan<TState> scan)
        {
            return;
        }

        scan.DetachShared();
        if (scan.State is IDisposable state)
        {
            state.Dispose();
        }
    }

    private static void Explain<TState>(NativeCustomScanRequest* request, delegate*<TState, PgExplainWriter, void> explain)
        where TState : class
    {
        var properties = new List<PgExplainWriter.Property>();
        if (explain != null && request->_state != 0 && Get<TState>(request->_state).State is TState state)
        {
            explain(state, new PgExplainWriter(properties));
        }

        var buffer = new ArrayBufferWriter<byte>();
        WriteInt(buffer, properties.Count);
        foreach (PgExplainWriter.Property property in properties)
        {
            WriteInt(buffer, property.Kind);
            WriteText(buffer, property.Label);
            WriteText(buffer, property.Unit);
            WriteLong(buffer, property.Integer);
            WriteDouble(buffer, property.Real);
            WriteInt(buffer, property.Digits);
            WriteText(buffer, property.Text);
        }

        request->_output = NativeValue.FromBytes(buffer.WrittenSpan);
    }

    private static Scan<TState> Get<TState>(nint handle)
        where TState : class
        => handle != 0 && GCHandle.FromIntPtr(handle).Target is Scan<TState> scan
            ? scan
            : throw new InvalidOperationException("The custom scan has no active state.");

    private static void WriteInt(ArrayBufferWriter<byte> buffer, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(buffer.GetSpan(4), value);
        buffer.Advance(4);
    }

    private static void WriteLong(ArrayBufferWriter<byte> buffer, long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(buffer.GetSpan(8), value);
        buffer.Advance(8);
    }

    private static void WriteDouble(ArrayBufferWriter<byte> buffer, double value)
    {
        BinaryPrimitives.WriteDoubleBigEndian(buffer.GetSpan(8), value);
        buffer.Advance(8);
    }

    private static void WriteText(ArrayBufferWriter<byte> buffer, string? value)
    {
        if (value is null)
        {
            WriteInt(buffer, -1);
            return;
        }

        byte[] bytes = s_utf8.GetBytes(value);
        WriteInt(buffer, bytes.Length);
        buffer.Write(bytes);
    }

    /// <summary>
    /// Roots one executing scan's state with the context needed to begin it again.
    /// </summary>
    /// <typeparam name="TState">The provider's per-scan state.</typeparam>
    /// <param name="state">The provider's state.</param>
    /// <param name="relation">The table's OID.</param>
    /// <param name="descriptor">The table's columns.</param>
    /// <param name="data">The chosen path's data.</param>
    /// <param name="explainOnly">Whether the executor only explains the plan.</param>
    /// <param name="read">One byte per column, nonzero when PostgreSQL reads it.</param>
    private sealed class Scan<TState>(TState state, uint relation, PgTupleDescriptor descriptor, byte[] data, bool explainOnly,
        byte[] read)
        where TState : class
    {
        public TState? State
        {
            get;
            set;
        } = state;

        public uint Relation { get; } = relation;

        public PgTupleDescriptor Descriptor { get; } = descriptor;

        public byte[] Data { get; } = data;

        public bool ExplainOnly { get; } = explainOnly;

        public byte[] Read { get; } = read;

        /// <summary>
        /// Gets or sets the parallel scan's shared memory handle in this process, or null when it has none.
        /// </summary>
        public IPgCustomScanSharedMemory? Shared
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the provider call that passes <see cref="Shared"/> to a state, kept for states a rescan begins.
        /// </summary>
        public Action<TState>? AttachShared
        {
            get;
            set;
        }

        /// <summary>
        /// Stops the provider from reaching shared memory PostgreSQL is about to free.
        /// </summary>
        public void DetachShared()
        {
            Shared?.Detach();
            Shared = null;
            AttachShared = null;
        }
    }
}
