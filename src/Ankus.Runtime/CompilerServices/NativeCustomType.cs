using System;

namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    /// <summary>
    /// Gets whether this value carries a generated base type's binary payload.
    /// </summary>
    internal readonly bool IsCustomType => _auxiliary1 == -7;

    /// <summary>
    /// Reads a present generated custom type into independent managed storage.
    /// </summary>
    /// <typeparam name="T">The generated managed type.</typeparam>
    /// <returns>The decoded value.</returns>
    public readonly T ReadCustom<T>() => (T)PgTypeRegistry.Require(typeof(T)).Read(this);

    /// <summary>
    /// Borrows a statically validated native-layout input until its current callback exits.
    /// </summary>
    /// <typeparam name="T">The packed native payload.</typeparam>
    /// <returns>A checked view, writable in place only when native detoasting already made a private copy.</returns>
    public readonly PgVarlena<T> ReadVarlena<T>() where T : unmanaged
    {
        PgVarlenaTypeMapping<T> mapping = PgTypeRegistry.RequireVarlena<T>();
        ReadOnlySpan<byte> payload = ReadCustomPayload(mapping.GetOid());
        if (payload.Length != mapping.Size)
        {
            throw new PgException("22P03", "Invalid native-layout custom-type value.");
        }

        if (_integer == 0 || _temporalInfinity is not (0 or 1))
        {
            throw new InvalidOperationException("Invalid native varlena input envelope.");
        }

        return new PgVarlena<T>((nint)_integer, (nint)_data, _temporalInfinity != 0,
            PgMemoryContext.Current, NativeMemoryContext.BorrowScope);
    }

    /// <summary>
    /// Promotes an input to the callback-result context before a set or aggregate retains it.
    /// </summary>
    /// <typeparam name="T">The packed native payload.</typeparam>
    /// <returns>An independent native value with the result owner's lifetime.</returns>
    public readonly PgVarlena<T> ReadOwnedVarlena<T>() where T : unmanaged =>
        new(ReadCustom<T>(), PgMemoryContext.Callback);

    /// <summary>
    /// Reads an array with native element allocations owned by the set or aggregate result context.
    /// </summary>
    /// <typeparam name="T">The generated array element type.</typeparam>
    /// <returns>The shaped array whose native elements survive intermediate callbacks.</returns>
    public readonly PgArray<T> ReadCallbackArray<T>()
    {
        NativeValue value = this;
        return PgMemoryContext.Callback.Run(() => value.ReadArray<T>());
    }

    /// <summary>
    /// Encodes a generated custom type into owned native transport.
    /// </summary>
    /// <typeparam name="T">The generated managed type.</typeparam>
    /// <param name="value">The managed value or SQL NULL.</param>
    /// <returns>The owned transport envelope.</returns>
    public static NativeValue FromCustom<T>(T value) => value is null ? new()
    {
        IsNull = 1
    } :
        PgTypeRegistry.Require(typeof(T)).Write(value);

    /// <summary>
    /// Reads a borrowed binary payload only after checking exact type and transport shape.
    /// </summary>
    internal readonly ReadOnlySpan<byte> ReadCustomPayload(uint oid)
    {
        if (!IsCustomType || _isNull != 0 || unchecked((uint)_auxiliary2) != oid || oid == 0 ||
            _length < 0 || _length != 0 && _data == null)
        {
            throw new InvalidOperationException("Invalid custom PostgreSQL type payload or type identity.");
        }

        return new(_data, _length);
    }

    /// <summary>
    /// Copies a binary payload and its exact SQL type into allocator-matched native storage.
    /// </summary>
    internal static NativeValue FromCustomPayload(ReadOnlySpan<byte> payload, uint oid)
    {
        NativeValue result = FromBytes(payload);
        result._auxiliary1 = -7;
        result._auxiliary2 = unchecked((int)oid);
        return result;
    }
}
