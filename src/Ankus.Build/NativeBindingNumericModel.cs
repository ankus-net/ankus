using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Preserves native scalar interpretations independently of their object byte sizes.
/// </summary>
/// <param name="CharIsSigned">Whether plain char has a signed representation.</param>
/// <param name="WCharSize">The native wchar_t byte size.</param>
/// <param name="WCharIsSigned">Whether wchar_t has a signed representation.</param>
/// <param name="Radix">The common radix of the native floating types.</param>
/// <param name="Float">The native float precision and exponent range.</param>
/// <param name="Double">The native double precision and exponent range.</param>
/// <param name="LongDouble">The native long double precision and exponent range.</param>
internal sealed record NativeNumericModel(bool CharIsSigned, int WCharSize, bool WCharIsSigned, int Radix,
    NativeFloatingModel Float, NativeFloatingModel Double, NativeFloatingModel LongDouble);

/// <summary>
/// Retains the C floating model without confusing precision with padded object storage.
/// </summary>
/// <param name="Precision">The number of radix digits in the significand.</param>
/// <param name="MinExponent">The minimum normal exponent in the C floating model.</param>
/// <param name="MaxExponent">The maximum normal exponent in the C floating model.</param>
internal sealed record NativeFloatingModel(int Precision, int MinExponent, int MaxExponent);

/// <summary>
/// Collects, validates and checks numeric ABI facts with the compiler's standard C constants.
/// </summary>
internal static class NativeBindingNumericModel
{
    private static readonly (string Name, string Expression)[] s_facts =
    [
        ("char_signed", "(CHAR_MIN < 0)"), ("wchar_size", "sizeof(wchar_t)"), ("wchar_signed", "(WCHAR_MIN < 0)"),
        ("float_radix", "FLT_RADIX"),
        ("float_precision", "FLT_MANT_DIG"), ("float_min_exp", "FLT_MIN_EXP"), ("float_max_exp", "FLT_MAX_EXP"),
        ("double_precision", "DBL_MANT_DIG"), ("double_min_exp", "DBL_MIN_EXP"), ("double_max_exp", "DBL_MAX_EXP"),
        ("long_double_precision", "LDBL_MANT_DIG"), ("long_double_min_exp", "LDBL_MIN_EXP"), ("long_double_max_exp", "LDBL_MAX_EXP"),
    ];
    private static readonly FrozenSet<string> s_factNames = s_facts.Select(static fact => "ankus_header_" + fact.Name).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Recognizes an exact numeric target constant by name. Arbitrary compiler declarations never match.
    /// </summary>
    internal static bool IsFact(string name) => s_factNames.Contains(name);

    /// <summary>
    /// Emits numeric constants in the same translation unit as the selected declarations.
    /// </summary>
    internal static void WriteObservations(StringBuilder source)
    {
        source.AppendLine("#include <float.h>\n#include <stddef.h>\n#include <stdint.h>");
        source.AppendLine("enum {");
        foreach ((string name, string expression) in s_facts)
        {
            source.AppendLine(CultureInfo.InvariantCulture, $"    ankus_header_{name} = {expression},");
        }

        source.AppendLine("};");
    }

    /// <summary>
    /// Requires every numeric fact and preserves signed exponent limits.
    /// </summary>
    internal static NativeNumericModel Read(IReadOnlyDictionary<string, int> values)
    {
        var result = new NativeNumericModel(Flag("char_signed"), Number("wchar_size"), Flag("wchar_signed"), Number("float_radix"),
            Floating("float"), Floating("double"), Floating("long_double"));
        Validate(result);
        return result;

        int Number(string name) => values.TryGetValue("ankus_header_" + name, out int value)
            ? value : throw new FormatException("Missing native numeric target constant: " + name);
        bool Flag(string name) => Number(name) switch
        {
            0 => false,
            1 => true,
            _ => throw new FormatException("Invalid native numeric flag: " + name),
        };
        NativeFloatingModel Floating(string name) => new(Number(name + "_precision"), Number(name + "_min_exp"), Number(name + "_max_exp"));
    }

    /// <summary>
    /// Rejects incomplete or contradictory scalar models before they participate in an ABI identity.
    /// </summary>
    internal static void Validate(NativeNumericModel model)
    {
        if (model is null || model.WCharSize <= 0 || model.Radix < 2 ||
            !Valid(model.Float) || !Valid(model.Double) || !Valid(model.LongDouble) ||
            model.Float.Precision > model.Double.Precision || model.Double.Precision > model.LongDouble.Precision ||
            model.Float.MinExponent < model.Double.MinExponent || model.Double.MinExponent < model.LongDouble.MinExponent ||
            model.Float.MaxExponent > model.Double.MaxExponent || model.Double.MaxExponent > model.LongDouble.MaxExponent)
        {
            throw new FormatException("Invalid native numeric target model.");
        }

        static bool Valid(NativeFloatingModel value) => value is not null && value.Precision > 0 && value.MinExponent < 0 && value.MaxExponent > 0;
    }

    /// <summary>
    /// Encodes every fact in a fixed invariant order for native storage observation identity.
    /// </summary>
    internal static string Encode(NativeNumericModel model)
        => string.Join(',', Values(model).Select(static value => value.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Requires the final native compiler to use the same numeric representations as the collecting frontend.
    /// </summary>
    internal static void WriteChecks(StringBuilder source, NativeNumericModel model)
    {
        int[] values = Values(model);
        source.AppendLine("#include <float.h>\n#include <stddef.h>\n#include <stdint.h>");
        for (int index = 0; index < s_facts.Length; index++)
        {
            (string name, string expression) = s_facts[index];
            source.AppendLine(CultureInfo.InvariantCulture,
                $"_Static_assert({expression} == {values[index]}, \"Native numeric model changed: {name}\");");
        }
    }

    private static int[] Values(NativeNumericModel model)
    {
        Validate(model);
        return [model.CharIsSigned ? 1 : 0, model.WCharSize, model.WCharIsSigned ? 1 : 0, model.Radix,
            model.Float.Precision, model.Float.MinExponent, model.Float.MaxExponent,
            model.Double.Precision, model.Double.MinExponent, model.Double.MaxExponent,
            model.LongDouble.Precision, model.LongDouble.MinExponent, model.LongDouble.MaxExponent];
    }
}
