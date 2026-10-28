using System;

namespace Ankus.CompilerServices;

public partial struct NativeValue
{
    /// <summary>
    /// Converts relation array elements and releases all acquired references if vector shape conversion fails.
    /// </summary>
    /// <typeparam name="T">The generated relation element type.</typeparam>
    /// <returns>A vector that preserves the original default lower bound and one-dimensional shape.</returns>
    public readonly T[] ReadRelationVector<T>()
    {
        PgArray<T> array = ReadArray<T>();
        try
        {
            return array.ToVector();
        }
        catch (Exception primary)
        {
            NativeRelationScope.Release(array, primary);
            throw;
        }
    }

    /// <summary>
    /// Opens a present regclass datum with AccessShare and returns a separately disposable relation reference.
    /// </summary>
    /// <returns>The owned relation reference. SQL NULL must be handled by the caller before conversion.</returns>
    public readonly PgRelation ReadRelation()
    {
        if (IsNull != 0)
        {
            throw new InvalidOperationException("SQL NULL is not a relation reference.");
        }

        return PgRelation.Open(checked((uint)Integral));
    }

    /// <summary>
    /// Copies a live relation's exact OID without consuming the reference.
    /// </summary>
    /// <param name="value">The live relation.</param>
    /// <returns>The scalar regclass transport.</returns>
    public static NativeValue FromRelation(PgRelation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new NativeValue { Integral = value.Oid };
    }
}
