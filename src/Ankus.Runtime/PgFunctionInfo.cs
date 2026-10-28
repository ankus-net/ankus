using System;
using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Holds an immutable, detached snapshot of the function metadata exposed by pgrx's PgProc.
/// </summary>
/// <remarks>
/// Catalog strings and arrays are copied before the native cache pin is released. This snapshot
/// survives catalog changes and backend callbacks; obtain another snapshot to observe changes.
/// Default expression trees require a live backend and an explicit memory-context owner.
/// </remarks>
public sealed class PgFunctionInfo
{
    private readonly string? _defaults;

    /// <summary>
    /// Copies and normalizes the selected-header catalog transport without retaining native buffers.
    /// </summary>
    internal PgFunctionInfo(uint oid, ReadOnlySpan<NativeValue> values)
    {
        if (values.Length != 24)
        {
            throw new InvalidOperationException("Invalid function catalog field count.");
        }

        Oid = oid;
        OwnerOid = Read<uint>(values[0], 26);
        Cost = Read<float>(values[1], 700);
        Rows = Read<float>(values[2], 700);
        uint variadic = Read<uint>(values[3], 26);
        VariadicElementTypeOid = variadic == 0 ? null : variadic;
        SupportFunctionOid = Read<uint>(values[4], 26);
        Kind = values[5].Integral switch
        {
            'f' => PgFunctionKind.Function,
            'p' => PgFunctionKind.Procedure,
            'a' => PgFunctionKind.Aggregate,
            'w' => PgFunctionKind.Window,
            _ => throw new InvalidOperationException("Unrecognized PostgreSQL function kind."),
        };
        IsSecurityDefiner = Read<bool>(values[6], 16);
        IsLeakProof = Read<bool>(values[7], 16);
        LanguageOid = Read<uint>(values[8], 26);
        Source = Read<string>(values[9], 25);
        Binary = Read<string?>(values[10], 25);
        string[]? configuration = Read<string[]?>(values[11], 1009);
        Configuration = configuration is null ? null : Array.AsReadOnly(configuration);
        InputArgumentCount = Read<short>(values[13], 21);
        DefaultArgumentCount = Read<short>(values[14], 21);
        string?[] names = Read<string?[]?>(values[15], 1009) ?? new string?[InputArgumentCount];
        ArgumentNames = Array.AsReadOnly(names);
        string? modes = Read<string?>(values[12], 25);
        PgArgumentMode[] argumentModes = new PgArgumentMode[modes?.Length ?? names.Length];
        for (int index = 0; index < argumentModes.Length; index++)
        {
            argumentModes[index] = (modes is null ? 'i' : modes[index]) switch
            {
                'i' => PgArgumentMode.In,
                'o' => PgArgumentMode.Out,
                'b' => PgArgumentMode.InOut,
                'v' => PgArgumentMode.Variadic,
                't' => PgArgumentMode.Table,
                _ => throw new InvalidOperationException("Unrecognized PostgreSQL argument mode."),
            };
        }

        ArgumentModes = Array.AsReadOnly(argumentModes);
        uint[] inputs = Read<uint[]?>(values[16], 1028) ?? [];
        InputArgumentTypeOids = Array.AsReadOnly(inputs);
        AllArgumentTypeOids = Array.AsReadOnly(Read<uint[]?>(values[17], 1028) ?? inputs);
        ReturnTypeOid = Read<uint>(values[18], 26);
        IsStrict = Read<bool>(values[19], 16);
        Volatility = values[20].Integral switch
        {
            'i' => PgVolatility.Immutable,
            's' => PgVolatility.Stable,
            'v' => PgVolatility.Volatile,
            _ => throw new InvalidOperationException("Unrecognized PostgreSQL function volatility."),
        };
        ParallelSafety = values[21].Integral switch
        {
            's' => PgParallelSafety.Safe,
            'r' => PgParallelSafety.Restricted,
            'u' => PgParallelSafety.Unsafe,
            _ => throw new InvalidOperationException("Unrecognized PostgreSQL parallel safety."),
        };
        ReturnsSet = Read<bool>(values[22], 16);
        _defaults = Read<string?>(values[23], 25);
    }

    /// <summary>
    /// Gets the routine's catalog OID.
    /// </summary>
    public uint Oid { get; }

    /// <summary>
    /// Gets the owning role's OID.
    /// </summary>
    public uint OwnerOid { get; }

    /// <summary>
    /// Gets the estimated cost in cpu_operator_cost units, per row for a set.
    /// </summary>
    public float Cost { get; }

    /// <summary>
    /// Gets the estimated result row count, or zero for a scalar.
    /// </summary>
    public float Rows { get; }

    /// <summary>
    /// Gets the variadic element type, or null when no variadic parameter exists.
    /// </summary>
    public uint? VariadicElementTypeOid { get; }

    /// <summary>
    /// Gets the planner support function OID, or zero when absent.
    /// </summary>
    public uint SupportFunctionOid { get; }

    /// <summary>
    /// Gets whether this row describes a function, procedure, aggregate or window function.
    /// </summary>
    public PgFunctionKind Kind { get; }

    /// <summary>
    /// Gets whether invocation uses the owner's privileges.
    /// </summary>
    public bool IsSecurityDefiner { get; }

    /// <summary>
    /// Gets whether the function is declared leakproof.
    /// </summary>
    public bool IsLeakProof { get; }

    /// <summary>
    /// Gets the implementation language OID.
    /// </summary>
    public uint LanguageOid { get; }

    /// <summary>
    /// Gets the language-specific implementation source or entry symbol.
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// Gets additional language-specific invocation information, or null.
    /// </summary>
    public string? Binary { get; }

    /// <summary>
    /// Gets the function-local configuration settings, or null when absent.
    /// </summary>
    public IReadOnlyList<string>? Configuration { get; }

    /// <summary>
    /// Gets all argument modes, including synthesized IN modes when the catalog array is absent.
    /// </summary>
    public IReadOnlyList<PgArgumentMode> ArgumentModes { get; }

    /// <summary>
    /// Gets the number of input arguments, including INOUT and VARIADIC.
    /// </summary>
    public int InputArgumentCount { get; }

    /// <summary>
    /// Gets the number of trailing input arguments with defaults.
    /// </summary>
    public int DefaultArgumentCount { get; }

    /// <summary>
    /// Gets argument names, retaining empty names and synthesizing null input names when the catalog array is absent.
    /// </summary>
    public IReadOnlyList<string?> ArgumentNames { get; }

    /// <summary>
    /// Gets the ordered input signature, including INOUT and VARIADIC arguments.
    /// </summary>
    public IReadOnlyList<uint> InputArgumentTypeOids { get; }

    /// <summary>
    /// Gets all argument types, including OUT and TABLE arguments.
    /// </summary>
    public IReadOnlyList<uint> AllArgumentTypeOids { get; }

    /// <summary>
    /// Gets the declared return type OID.
    /// </summary>
    public uint ReturnTypeOid { get; }

    /// <summary>
    /// Gets whether a null input prevents invocation and produces SQL NULL.
    /// </summary>
    public bool IsStrict { get; }

    /// <summary>
    /// Gets the declared volatility.
    /// </summary>
    public PgVolatility Volatility { get; }

    /// <summary>
    /// Gets the declared parallel execution safety.
    /// </summary>
    public PgParallelSafety ParallelSafety { get; }

    /// <summary>
    /// Gets whether the function returns multiple values.
    /// </summary>
    public bool ReturnsSet { get; }

    /// <summary>
    /// Parses the captured defaults into actual PostgreSQL expression trees without evaluating them.
    /// </summary>
    /// <param name="context">The explicit owner of the list and every expression node.</param>
    /// <returns>Null when no defaults exist; otherwise the ordered last N input defaults.</returns>
    /// <remarks>
    /// Dispose the returned list to release its container. Nodes remain owned by the context
    /// until reset or deletion. Raw pointers require selected-version native node layouts and
    /// must never outlive the context. Each call creates independent native trees.
    /// </remarks>
    public PgList<nint>? GetDefaultArguments(PgMemoryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _defaults is null ? null : PgList.ParseDefaults(_defaults, DefaultArgumentCount, context);
    }

    /// <summary>
    /// Converts one owned transport field using its normalized PostgreSQL type.
    /// </summary>
    private static T Read<T>(NativeValue value, uint oid) => SpiRow.Convert<T>(SpiType.FromNative(value, oid));
}
