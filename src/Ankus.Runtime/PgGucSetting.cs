using System;
using System.Runtime.CompilerServices;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Reads a configuration parameter defined at run time through <see cref="PgGucRegistry"/>.
/// </summary>
/// <typeparam name="T">
/// The value type: <see cref="bool"/>, <see cref="int"/> for integer and enumerated parameters, <see cref="double"/>,
/// or nullable <see cref="string"/>.
/// </typeparam>
/// <remarks>
/// Like a generated setting getter, <see cref="Value"/> reads PostgreSQL's current native value and must be read from an
/// extension callback on the backend thread.
/// </remarks>
public sealed class PgGucSetting<T>
{
    private readonly int _kind;

    /// <summary>
    /// Creates a reader for a registered definition.
    /// </summary>
    /// <param name="name">The qualified configuration name.</param>
    /// <param name="kind">The native configuration kind.</param>
    internal PgGucSetting(string name, int kind)
    {
        Name = name;
        _kind = kind;
    }

    /// <summary>
    /// Gets the qualified configuration name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the parameter's current value.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is read outside an extension callback or on another thread.</exception>
    public T Value
    {
        get
        {
            if (typeof(T) == typeof(bool))
            {
                bool value = NativeGuc.ReadBoolean(Name);
                return Unsafe.As<bool, T>(ref value);
            }

            if (typeof(T) == typeof(int))
            {
                int value = _kind == 4 ? NativeGuc.ReadEnum(Name) : NativeGuc.ReadInt32(Name);
                return Unsafe.As<int, T>(ref value);
            }

            if (typeof(T) == typeof(double))
            {
                double value = NativeGuc.ReadDouble(Name);
                return Unsafe.As<double, T>(ref value);
            }

            return GenericValue.As<T>(NativeGuc.ReadString(Name));
        }
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}
