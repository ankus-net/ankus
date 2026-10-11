namespace Ankus;

/// <summary>
/// Retains a closed scalar mapping's metadata without resolving catalog identities or constructing user code.
/// </summary>
internal abstract class DatumTypeMapping(string name, string? schema, PgTypeOrigin origin, Type converterType,
    bool canRead, bool canWrite, DatumTypeMapping? rangeBound = null)
{
    /// <summary>
    /// Gets the scalar mapping whose catalog identity must match a registered range's subtype.
    /// </summary>
    internal DatumTypeMapping? RangeBound { get; } = rangeBound;

    /// <summary>
    /// Gets whether the exact mapping supports native inputs.
    /// </summary>
    internal bool CanRead { get; } = canRead;

    /// <summary>
    /// Gets whether the exact mapping supports native outputs.
    /// </summary>
    internal bool CanWrite { get; } = canWrite;

    /// <summary>
    /// Checks whether another generated registration describes this exact conversion contract.
    /// </summary>
    internal bool Matches(string candidateName, string? candidateSchema, PgTypeOrigin candidateOrigin,
        Type candidateConverterType, bool candidateCanRead, bool candidateCanWrite, DatumTypeMapping? candidateRangeBound = null)
        => string.Equals(name, candidateName, StringComparison.Ordinal) &&
            string.Equals(schema, candidateSchema, StringComparison.Ordinal) && origin == candidateOrigin &&
            converterType == candidateConverterType && CanRead == candidateCanRead && CanWrite == candidateCanWrite &&
            ReferenceEquals(RangeBound, candidateRangeBound);

    /// <summary>
    /// Resolves the concrete type in the current backend on every operation.
    /// </summary>
    internal uint GetOid()
    {
        uint oid = NativeBackend.ResolveDatumType(name, schema);
        if (RangeBound is { } bound)
        {
            uint subtype = NativeBackend.RangeSubtype(oid);
            uint expected = bound.GetOid();
            if (subtype != expected)
            {
                throw new InvalidCastException($"PostgreSQL range subtype OID {subtype} does not match mapped bound type OID {expected}.");
            }
        }

        return oid;
    }

    /// <summary>
    /// Rejects reading before invoking a factory or accessing native storage.
    /// </summary>
    internal void RequireRead()
    {
        if (!CanRead)
        {
            throw new NotSupportedException("The mapped PostgreSQL type has no datum reader.");
        }
    }

    /// <summary>
    /// Rejects writing before invoking a factory or allocating native storage.
    /// </summary>
    internal void RequireWrite()
    {
        if (!CanWrite)
        {
            throw new NotSupportedException("The mapped PostgreSQL type has no datum writer.");
        }
    }

    /// <summary>
    /// Validates a datum before bypassing the converter for SQL NULL or reading a present value.
    /// </summary>
    internal object? Read(PgDatum value)
    {
        RequireRead();
        value.Lifetime.Validate();
        uint oid = GetOid();
        ValidateIdentity(value, oid);
        return value.IsNull ? null : ReadPresent(value);
    }

    /// <summary>
    /// Reads a managed row or composite cell with the same exact type check as a raw read. A present cell is rebuilt as a
    /// datum in a temporary context, which is deleted after the reader returns its independent value.
    /// </summary>
    internal object? ReadCell(object? cell, uint cellTypeOid)
    {
        RequireRead();
        uint oid = GetOid();
        if (cellTypeOid != oid)
        {
            throw new InvalidCastException($"PostgreSQL datum type OID {cellTypeOid} does not match mapped type OID {oid}.");
        }

        if (cell is null)
        {
            return null;
        }

        using PgMemoryContext temporary = PgMemoryContext.Create("Ankus mapped cell");
        return ReadPresent(NativeBackend.RebuildCell(cell, oid, new PgDatumLifetime(temporary)));
    }

    /// <summary>
    /// Writes a value for a managed row or composite cell, which keeps a copy independent of the writer's native storage.
    /// </summary>
    internal object? WriteCell(object? value, uint capturedOid)
    {
        RequireWrite();
        if (GetOid() != capturedOid)
        {
            throw new InvalidOperationException("The mapped PostgreSQL type has changed since the value was created.");
        }

        if (value is null)
        {
            return null;
        }

        using PgMemoryContext temporary = PgMemoryContext.Create("Ankus mapped cell");
        return NativeBackend.DetachDatum(WriteChecked(value, capturedOid, temporary));
    }

    /// <summary>
    /// Writes the declared managed contract while rejecting stale captured parameter identities.
    /// </summary>
    internal NativeValue Write(object? value, uint capturedOid = 0)
    {
        RequireWrite();
        uint oid = GetOid();
        if (capturedOid != 0 && capturedOid != oid)
        {
            throw new InvalidOperationException("The mapped PostgreSQL parameter type has changed since the parameter was created.");
        }

        if (value is null)
        {
            return new NativeValue { IsNull = 1 };
        }

        return NativeValue.FromPolymorphic(WriteChecked(value, oid, PgMemoryContext.Current));
    }

    /// <summary>
    /// Writes a present array element into an explicitly supplied owner without consuming returned aliases.
    /// </summary>
    internal PgDatum WriteDatum(object value, uint expectedOid, PgMemoryContext destination)
    {
        RequireWrite();
        if (GetOid() != expectedOid)
        {
            throw new InvalidOperationException("The mapped PostgreSQL element type changed during array construction.");
        }

        return WriteChecked(value, expectedOid, destination);
    }

    /// <summary>
    /// Checks the present writer's exact identity and lifetime, including a returned SQL NULL datum.
    /// </summary>
    private PgDatum WriteChecked(object value, uint oid, PgMemoryContext destination)
    {
        PgDatum datum = WritePresent(value, oid, destination);
        datum.Lifetime.Validate();
        ValidateIdentity(datum, oid);
        return datum;
    }

    /// <summary>
    /// Converts a present input to an independent managed value.
    /// </summary>
    protected abstract object ReadPresent(PgDatum value);

    /// <summary>
    /// Converts a present managed value to a non-null native handle.
    /// </summary>
    protected abstract PgDatum WritePresent(object value, uint oid, PgMemoryContext destination);

    /// <summary>
    /// Checks nominal SQL identity independently of storage representation and NULL.
    /// </summary>
    private static void ValidateIdentity(PgDatum value, uint expected)
    {
        if (value.TypeOid != expected)
        {
            throw new InvalidCastException($"PostgreSQL datum type OID {value.TypeOid} does not match mapped type OID {expected}.");
        }
    }
}
