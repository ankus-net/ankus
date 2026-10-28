using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Defines a generated base type's codec and statically closed collection conversions.
/// </summary>
internal abstract class CustomTypeMapping(string name, string? schema)
{
    /// <summary>
    /// Gets whether the statically registered contract permits CLR reference-array covariance.
    /// </summary>
    internal abstract bool IsReferenceType { get; }

    /// <summary>
    /// Gets whether this mapping is a typed alternative to the canonical managed SQL value.
    /// </summary>
    internal virtual bool IsAlternate => false;

    /// <summary>
    /// Converts a statically registered alternative representation without changing SQL identity.
    /// </summary>
    internal virtual object ConvertScalar(object value) => value;

    /// <summary>
    /// Resolves the current variable-length base-type identity.
    /// </summary>
    internal uint GetOid(bool missingOk = false) => NativeBackend.ResolveCustomType(name, schema, missingOk);

    /// <summary>
    /// Resolves the current array identity through the guarded catalog lookup.
    /// </summary>
    internal uint GetArrayOid() => NativeBackend.EnumArrayOid(GetOid());

    /// <summary>
    /// Checks assignability using the statically generated managed contract.
    /// </summary>
    internal abstract bool Accepts(object value);

    /// <summary>
    /// Checks vector assignability using the statically generated element contracts.
    /// </summary>
    internal abstract bool AcceptsArray(Array value);

    /// <summary>
    /// Parses a present text input.
    /// </summary>
    internal abstract object Parse(string text);

    /// <summary>
    /// Formats a present managed value.
    /// </summary>
    internal abstract string Format(object value);

    /// <summary>
    /// Reads a present stored value into independent managed storage.
    /// </summary>
    internal abstract object Read(NativeValue value);

    /// <summary>
    /// Serializes a present managed value into owned native transport.
    /// </summary>
    internal abstract NativeValue Write(object value);

    /// <summary>
    /// Wraps a statically supported vector while preserving element identity.
    /// </summary>
    internal abstract IPgArray Wrap(Array value);

    /// <summary>
    /// Converts owned arrays to the requested statically generated representation.
    /// </summary>
    internal abstract object Convert(IPgArray value, Type type);

    /// <summary>
    /// Reads a nullable array from validated native transport.
    /// </summary>
    internal abstract IPgArray ReadArray(NativeValue value, uint oid);
}
