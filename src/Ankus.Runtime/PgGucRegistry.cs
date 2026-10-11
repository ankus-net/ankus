using System.Runtime.InteropServices;

namespace Ankus;

/// <summary>
/// Defines PostgreSQL configuration parameters whose names and metadata are computed at run time, as pgrx's
/// <c>GucRegistry</c> does.
/// </summary>
/// <remarks>
/// <para>
/// Call these methods from a <see cref="PgModuleLoadAttribute"/> method, as a pgrx extension calls <c>GucRegistry</c>
/// from <c>_PG_init</c>. PostgreSQL owns each definition for the life of the process: parsing, permissions, source
/// priority, <c>SET</c>, <c>RESET</c> and placeholder adoption behave as for attribute-declared settings.
/// </para>
/// <para>
/// Defining a parameter again with identical metadata returns another reader for it, so a retried module load is
/// harmless. A different definition with the same name, or a name declared by a <see cref="PgGucAttribute"/>, is
/// rejected. <see cref="PgGucContext.Postmaster"/> parameters can be defined only while
/// <c>shared_preload_libraries</c> is processed. Names follow PostgreSQL's custom setting rules, as the attributes require.
/// </para>
/// <para>
/// Prefer the <see cref="PgGucAttribute"/> family for names known at compile time. Attributes add compile-time
/// validation and typed C# enum settings. Both forms accept check, assign and show hooks; pass
/// <see cref="PgGucHooks{T}"/> here, as pgrx's <c>define_*_guc_with_hooks</c> functions take hook functions.
/// </para>
/// </remarks>
public static class PgGucRegistry
{
    private const PgGucOptions AllOptions = PgGucOptions.NoShowAll | PgGucOptions.NoResetAll | PgGucOptions.Report |
        PgGucOptions.DisallowInFile | PgGucOptions.SuperuserOnly | PgGucOptions.IsName | PgGucOptions.NotWhileSecurityRestricted |
        PgGucOptions.DisallowInAutoFile | PgGucOptions.Explain | PgGucOptions.RuntimeComputed;

    /// <summary>
    /// Defines a Boolean configuration parameter.
    /// </summary>
    /// <param name="name">The qualified name, including the extension's prefix.</param>
    /// <param name="defaultValue">The value before any configuration source sets one.</param>
    /// <param name="shortDescription">The short description shown in PostgreSQL's configuration metadata.</param>
    /// <param name="longDescription">An optional long description.</param>
    /// <param name="context">When and by whom the parameter can be changed.</param>
    /// <param name="flags">PostgreSQL configuration flags.</param>
    /// <param name="hooks">Optional check, assign and show hooks.</param>
    /// <returns>A reader for the parameter's current value.</returns>
    /// <exception cref="ArgumentException">Text is blank or contains a zero character, or an option is undefined.</exception>
    /// <exception cref="PgException">PostgreSQL rejected the definition.</exception>
    public static PgGucSetting<bool> DefineBool(string name, bool defaultValue, string shortDescription, string? longDescription = null,
        PgGucContext context = PgGucContext.UserSet, PgGucOptions flags = PgGucOptions.None, PgGucHooks<bool>? hooks = null)
    {
        var definition = new NativeGucDefinition { _kind = 0, _bootBoolean = defaultValue ? (byte)1 : (byte)0 };
        return Define(name, shortDescription, longDescription, context, flags, PgGucUnit.None, ref definition, null, null, hooks);
    }

    /// <summary>
    /// Defines an integer configuration parameter.
    /// </summary>
    /// <param name="name">The qualified name, including the extension's prefix.</param>
    /// <param name="defaultValue">The value before any configuration source sets one.</param>
    /// <param name="shortDescription">The short description shown in PostgreSQL's configuration metadata.</param>
    /// <param name="minimum">The inclusive minimum.</param>
    /// <param name="maximum">The inclusive maximum.</param>
    /// <param name="longDescription">An optional long description.</param>
    /// <param name="context">When and by whom the parameter can be changed.</param>
    /// <param name="flags">PostgreSQL configuration flags.</param>
    /// <param name="unit">The unit PostgreSQL accepts and displays.</param>
    /// <param name="hooks">Optional check, assign and show hooks.</param>
    /// <returns>A reader for the parameter's current value.</returns>
    /// <exception cref="ArgumentException">Text is blank or contains a zero character, or an option is undefined.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is empty or excludes the default.</exception>
    /// <exception cref="PgException">PostgreSQL rejected the definition.</exception>
    public static PgGucSetting<int> DefineInt(string name, int defaultValue, string shortDescription, int minimum = int.MinValue,
        int maximum = int.MaxValue, string? longDescription = null, PgGucContext context = PgGucContext.UserSet,
        PgGucOptions flags = PgGucOptions.None, PgGucUnit unit = PgGucUnit.None, PgGucHooks<int>? hooks = null)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimum, maximum);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultValue, minimum);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(defaultValue, maximum);
        var definition = new NativeGucDefinition { _kind = 1, _bootInteger = defaultValue, _minimumInteger = minimum, _maximumInteger = maximum };
        return Define(name, shortDescription, longDescription, context, flags, unit, ref definition, null, null, hooks);
    }

    /// <summary>
    /// Defines a floating-point configuration parameter.
    /// </summary>
    /// <param name="name">The qualified name, including the extension's prefix.</param>
    /// <param name="defaultValue">The value before any configuration source sets one.</param>
    /// <param name="shortDescription">The short description shown in PostgreSQL's configuration metadata.</param>
    /// <param name="minimum">The inclusive minimum.</param>
    /// <param name="maximum">The inclusive maximum.</param>
    /// <param name="longDescription">An optional long description.</param>
    /// <param name="context">When and by whom the parameter can be changed.</param>
    /// <param name="flags">PostgreSQL configuration flags.</param>
    /// <param name="unit">The unit PostgreSQL accepts and displays.</param>
    /// <param name="hooks">Optional check, assign and show hooks.</param>
    /// <returns>A reader for the parameter's current value.</returns>
    /// <exception cref="ArgumentException">Text is blank or contains a zero character, or an option is undefined.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound or the default is not a number, the range is empty, or it excludes the default.</exception>
    /// <exception cref="PgException">PostgreSQL rejected the definition.</exception>
    public static PgGucSetting<double> DefineReal(string name, double defaultValue, string shortDescription, double minimum = double.MinValue,
        double maximum = double.MaxValue, string? longDescription = null, PgGucContext context = PgGucContext.UserSet,
        PgGucOptions flags = PgGucOptions.None, PgGucUnit unit = PgGucUnit.None, PgGucHooks<double>? hooks = null)
    {
        if (double.IsNaN(defaultValue) || double.IsNaN(minimum) || double.IsNaN(maximum))
        {
            throw new ArgumentOutOfRangeException(double.IsNaN(defaultValue) ? nameof(defaultValue) : double.IsNaN(minimum) ? nameof(minimum) : nameof(maximum),
                "Configuration values and bounds must be numbers.");
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimum, maximum);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultValue, minimum);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(defaultValue, maximum);
        var definition = new NativeGucDefinition { _kind = 2, _bootReal = defaultValue, _minimumReal = minimum, _maximumReal = maximum };
        return Define(name, shortDescription, longDescription, context, flags, unit, ref definition, null, null, hooks);
    }

    /// <summary>
    /// Defines a string configuration parameter.
    /// </summary>
    /// <param name="name">The qualified name, including the extension's prefix.</param>
    /// <param name="defaultValue">The value before any configuration source sets one, or null for an absent value.</param>
    /// <param name="shortDescription">The short description shown in PostgreSQL's configuration metadata.</param>
    /// <param name="longDescription">An optional long description.</param>
    /// <param name="context">When and by whom the parameter can be changed.</param>
    /// <param name="flags">PostgreSQL configuration flags.</param>
    /// <param name="hooks">Optional check, assign and show hooks.</param>
    /// <returns>A reader for the parameter's current value.</returns>
    /// <exception cref="ArgumentException">Text is blank or contains a zero character, or an option is undefined.</exception>
    /// <exception cref="PgException">PostgreSQL rejected the definition.</exception>
    public static PgGucSetting<string?> DefineString(string name, string? defaultValue, string shortDescription, string? longDescription = null,
        PgGucContext context = PgGucContext.UserSet, PgGucOptions flags = PgGucOptions.None, PgGucHooks<string?>? hooks = null)
    {
        var definition = new NativeGucDefinition { _kind = 3, _bootNull = defaultValue is null ? (byte)1 : (byte)0 };
        return Define(name, shortDescription, longDescription, context, flags, PgGucUnit.None, ref definition, defaultValue, null, hooks);
    }

    /// <summary>
    /// Defines an enumerated configuration parameter whose labels are supplied at run time.
    /// </summary>
    /// <param name="name">The qualified name, including the extension's prefix.</param>
    /// <param name="defaultValue">The ordinal used before any configuration source sets one.</param>
    /// <param name="shortDescription">The short description shown in PostgreSQL's configuration metadata.</param>
    /// <param name="options">The accepted labels in display order.</param>
    /// <param name="longDescription">An optional long description.</param>
    /// <param name="context">When and by whom the parameter can be changed.</param>
    /// <param name="flags">PostgreSQL configuration flags.</param>
    /// <param name="hooks">Optional check, assign and show hooks; their values are label ordinals.</param>
    /// <returns>A reader for the current ordinal.</returns>
    /// <remarks>Use <see cref="PgGucEnumAttribute"/> for a typed C# enum known at compile time.</remarks>
    /// <exception cref="ArgumentException">Text is blank or contains a zero character, an option is undefined, or no label is supplied.</exception>
    /// <exception cref="ArgumentOutOfRangeException">No label has the default ordinal.</exception>
    /// <exception cref="PgException">PostgreSQL rejected the definition.</exception>
    public static PgGucSetting<int> DefineEnum(string name, int defaultValue, string shortDescription, IReadOnlyList<PgGucEnumOption> options,
        string? longDescription = null, PgGucContext context = PgGucContext.UserSet, PgGucOptions flags = PgGucOptions.None,
        PgGucHooks<int>? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
        {
            throw new ArgumentException("An enumerated configuration parameter requires at least one label.", nameof(options));
        }

        bool found = false;
        foreach (PgGucEnumOption option in options)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(option.Name, nameof(options));
            found |= option.Value == defaultValue;
        }

        if (!found)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue), defaultValue, "No label has the default ordinal.");
        }

        var definition = new NativeGucDefinition { _kind = 4, _bootInteger = defaultValue };
        return Define(name, shortDescription, longDescription, context, flags, PgGucUnit.None, ref definition, null, options, hooks);
    }

    /// <summary>
    /// Applies PostgreSQL's custom configuration name rules, which PgGuc attributes also enforce.
    /// </summary>
    private static bool IsCustomName(string name)
    {
        if (!name.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string component in name.Split('.'))
        {
            if (component.Length == 0 || !IsStart(component[0]))
            {
                return false;
            }

            foreach (char character in component.AsSpan(1))
            {
                if (!IsStart(character) && !char.IsAsciiDigit(character) && character != '$')
                {
                    return false;
                }
            }
        }

        return true;

        static bool IsStart(char character) => char.IsAsciiLetter(character) || character == '_' || character >= '\u0080';
    }

    /// <summary>
    /// Validates shared metadata, copies text to native memory for the call and registers the parameter.
    /// </summary>
    private static unsafe PgGucSetting<T> Define<T>(string name, string shortDescription, string? longDescription, PgGucContext context,
        PgGucOptions flags, PgGucUnit unit, ref NativeGucDefinition definition, string? bootString, IReadOnlyList<PgGucEnumOption>? options,
        PgGucHooks<T>? hooks)
    {
        byte[] encodedName = NativeGuc.EncodeName(name);
        if (!IsCustomName(name))
        {
            throw new ArgumentException(
                "The configuration name must contain dotted components that each start with a letter, an underscore or a non-ASCII character, " +
                "followed by letters, digits, underscores, dollar signs or non-ASCII characters.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(shortDescription);
        if (!Enum.IsDefined(context))
        {
            throw new ArgumentException("The configuration context is not defined.", nameof(context));
        }

        if ((flags & ~AllOptions) != 0)
        {
            throw new ArgumentException("The configuration flags include an undefined option.", nameof(flags));
        }

        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentException("The configuration unit is not defined.", nameof(unit));
        }

        // PostgreSQL runs the check on the default while it registers the parameter, so the hooks are reserved first.
        bool reserved = false;
        if (hooks is { Bits: not 0 })
        {
            definition._slot = RuntimeGucHooks.Reserve(name, definition._kind, hooks, options, out reserved);
            definition._hooks = hooks.Bits;
        }

        int count = options?.Count ?? 0;
        byte** optionNames = null;
        int* optionValues = null;
        byte* optionHidden = null;
        try
        {
            definition._shortDescription = NativeGuc.AllocateText(shortDescription, nameof(shortDescription));
            definition._longDescription = NativeGuc.AllocateText(longDescription, nameof(longDescription));
            definition._bootString = NativeGuc.AllocateText(bootString, "defaultValue");
            if (options is not null)
            {
                optionNames = (byte**)NativeMemory.AllocZeroed((nuint)count, (nuint)sizeof(byte*));
                optionValues = (int*)NativeMemory.Alloc((nuint)count, sizeof(int));
                optionHidden = (byte*)NativeMemory.Alloc((nuint)count);
                for (int index = 0; index < count; index++)
                {
                    optionNames[index] = NativeGuc.AllocateText(options[index].Name, nameof(options));
                    optionValues[index] = options[index].Value;
                    optionHidden[index] = options[index].Hidden ? (byte)1 : (byte)0;
                }
            }

            definition._optionNames = optionNames;
            definition._optionValues = optionValues;
            definition._optionHidden = optionHidden;
            definition._optionCount = count;
            definition._context = (int)context;
            definition._flags = (uint)flags;
            definition._unit = (int)unit;
            fixed (NativeGucDefinition* native = &definition)
            {
                NativeGuc.Define(encodedName, native);
            }

            reserved = false;
            return new PgGucSetting<T>(name, definition._kind);
        }
        finally
        {
            NativeMemory.Free(definition._shortDescription);
            NativeMemory.Free(definition._longDescription);
            NativeMemory.Free(definition._bootString);
            if (optionNames is not null)
            {
                for (int index = 0; index < count; index++)
                {
                    NativeMemory.Free(optionNames[index]);
                }
            }

            NativeMemory.Free(optionNames);
            NativeMemory.Free(optionValues);
            NativeMemory.Free(optionHidden);
            if (reserved)
            {
                RuntimeGucHooks.Release(name, definition._slot);
            }
        }
    }
}
