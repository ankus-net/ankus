using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Carries a native-owned SPI result and its allocator-specific cleanup callback.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSpiResult
{
    /// <summary>
    /// Points to the column metadata array.
    /// </summary>
    internal NativeSpiColumn* _columns;

    /// <summary>
    /// Points to row-major result cells.
    /// </summary>
    internal NativeValue* _values;

    /// <summary>
    /// Contains the final statement's processed-row count.
    /// </summary>
    internal long _rowsAffected;

    /// <summary>
    /// Contains the materialized row count.
    /// </summary>
    internal int _rowCount;

    /// <summary>
    /// Contains the result column count.
    /// </summary>
    internal int _columnCount;

    /// <summary>
    /// Releases all native result allocations without invoking PostgreSQL APIs.
    /// </summary>
    internal delegate* unmanaged[Cdecl]<NativeSpiResult*, void> _release;

    /// <summary>
    /// Contains the identity of an opened or resolved cursor.
    /// </summary>
    internal long _cursorId;

    /// <summary>
    /// Contains the owned UTF-8 portal name.
    /// </summary>
    internal NativeValue _cursorName;

    /// <summary>
    /// Contains an owned scalar result from a native helper, including UTF-8 text and temporal values.
    /// </summary>
    internal NativeValue _text;

    /// <summary>
    /// Contains the invoked function's catalog identity for a function-context snapshot.
    /// </summary>
    internal uint _functionOid;

    /// <summary>
    /// Contains the resolved function result type for a function-context snapshot.
    /// </summary>
    internal uint _resultTypeOid;

    /// <summary>
    /// Contains the invocation collation, or zero when none applies.
    /// </summary>
    internal uint _collationOid;

    /// <summary>
    /// Contains a reset-sensitive native identity for the function call site.
    /// </summary>
    internal nint _functionSite;

    /// <summary>
    /// Contains the registered identity of the function's cache memory context.
    /// </summary>
    internal nint _functionMemory;

    /// <summary>
    /// Contains the function cache owner's captured reset generation.
    /// </summary>
    internal nuint _functionGeneration;

    /// <summary>
    /// Copies every cell and column into managed objects before native memory is released.
    /// </summary>
    /// <returns>The independent managed result.</returns>
    internal readonly SpiResult ToManaged()
    {
        var columns = new SpiColumn[_columnCount];
        for (int index = 0; index < columns.Length; index++)
        {
            columns[index] = new SpiColumn(_columns[index]._name.ReadString(), _columns[index]._typeOid);
        }

        IReadOnlyList<SpiColumn> metadata = Array.AsReadOnly(columns);
        var rows = new SpiRow[_rowCount];
        EnumMapping?[]? enumMappings = null;
        for (int row = 0; row < rows.Length; row++)
        {
            object?[] cells = new object?[_columnCount];
            for (int column = 0; column < cells.Length; column++)
            {
                NativeValue value = _values[row * _columnCount + column];
                uint oid = _columns[column]._baseTypeOid;
                if (value.IsEnum && value.IsNull == 0)
                {
                    // Scope the lookup to this immutable result, never across queries or catalog changes.
                    enumMappings ??= new EnumMapping?[_columnCount];
                    EnumMapping mapping = enumMappings[column] ??= PgEnumRegistry.FindOid(oid);
                    cells[column] = mapping.FromLabel(value.ReadString());
                }
                else
                {
                    cells[column] = SpiType.FromNative(value, oid);
                }
            }

            rows[row] = new SpiRow(cells, metadata);
        }

        return new SpiResult(metadata, rows, _rowsAffected);
    }
}
