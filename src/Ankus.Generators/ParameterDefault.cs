using System;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Translates C# optional-parameter constants and value-type defaults into exact SQL defaults.
/// </summary>
internal static class ParameterDefault
{
    /// <summary>
    /// Formats a constant without loading the extension assembly or losing numeric precision.
    /// </summary>
    /// <param name="parameter">The optional parameter.</param>
    /// <param name="type">Its supported scalar or array contract.</param>
    /// <returns>The SQL expression, or null when an explicit SQL default is required.</returns>
    internal static string? Create(IParameterSymbol parameter, FunctionType type)
    {
        object? value = parameter.ExplicitDefaultValue;
        if (type.Enumeration is { } enumeration && value is not null)
        {
            EnumLabel? label = enumeration.Labels.FirstOrDefault(item => Equals(item.Value, value));
            return label is null ? null : SqlText.Literal(label.Label) + "::" + type.Sql;
        }

        if (value is null)
        {
            if (type.Nullable || type.Reference)
            {
                return "NULL";
            }

            string? text = type.Managed switch
            {
                "global::System.Guid" => "00000000-0000-0000-0000-000000000000",
                "global::Ankus.PgJson" or "global::Ankus.PgJsonb" => "null",
                "global::Ankus.PgNumeric" => "0",
                "global::Ankus.PgPoint" => "(0,0)",
                "global::Ankus.PgLineSegment" => "[(0,0),(0,0)]",
                "global::Ankus.PgBox" => "(0,0),(0,0)",
                "global::Ankus.PgCircle" => "<(0,0),0>",
                "global::Ankus.PgInet" or "global::Ankus.PgCidr" or "global::System.Net.IPNetwork" => "0.0.0.0/0",
                "global::Ankus.PgDate" or "global::Ankus.PgTimestamp" => "2000-01-01",
                "global::Ankus.PgTimestampTz" => "2000-01-01 00:00:00+00",
                "global::Ankus.PgTime" => "00:00:00",
                "global::Ankus.PgTimeTz" => "00:00:00+00",
                "global::Ankus.PgInterval" or "global::System.TimeSpan" => "0 seconds",
                "global::System.DateOnly" or "global::System.DateTime" => "0001-01-01",
                "global::System.DateTimeOffset" => "0001-01-01 00:00:00+00",
                "global::System.TimeOnly" => "00:00:00",
                _ => null,
            };
            return text is null ? null : SqlText.Literal(text) + "::" + type.Sql;
        }

        return value switch
        {
            bool boolean => boolean ? "TRUE" : "FALSE",
            string text when SqlText.IsText(text) => SqlText.Literal(text),
            float number => SqlText.Literal(number.ToString("R", CultureInfo.InvariantCulture)) + "::real",
            double number => SqlText.Literal(number.ToString("R", CultureInfo.InvariantCulture)) + "::double precision",
            sbyte or short or int or long or uint or decimal => "(" + ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture) + ")::" + type.Sql,
            _ => null,
        };
    }
}
