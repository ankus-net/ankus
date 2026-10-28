using System;

namespace Ankus;

/// <summary>
/// Generates a native callback address for a statically declared managed handler.
/// </summary>
/// <param name="method">The name of a synchronous static handler in the property's containing type.</param>
/// <remarks>
/// Apply this attribute to a static partial getter-only property whose type is a generated native
/// function pointer. The handler must match that type's Invoke signature exactly and cannot
/// declare System.Diagnostics.Conditional, including on either part of a partial handler.
/// Every native invocation must execute the handler, independently of compilation symbols. The native entry
/// point belongs to the loaded extension; storing its address does not extend the module's lifetime.
/// Callback declarations may live in a referenced project using the same generated native binding contract.
/// Use PgModuleLoad for native hook registration that must precede a parallel worker's first executor entry.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class PgNativeCallbackAttribute(string method) : Attribute
{
    /// <summary>
    /// Gets the statically selected managed handler name.
    /// </summary>
    public string Method { get; } = !string.IsNullOrWhiteSpace(method)
        ? method : throw new ArgumentException("A native callback requires a managed handler name.", nameof(method));
}
