using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Validates literal configuration prefixes and emits their native registration lifecycle.
/// </summary>
internal static class GucPrefixDeclaration
{
    /// <summary>
    /// Requires one nonnull literal value before generating a native C string.
    /// </summary>
    private static readonly DiagnosticDescriptor s_value = new(
        "ANKUS347", "Configuration prefix requires a nonnull string",
        "Supply one nonnull constant string for the configuration prefix; an empty string retains PostgreSQL's native behavior",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/configuration/#prefix-declaration-diagnostics");

    /// <summary>
    /// Rejects zero characters that would truncate the prefix at the native boundary.
    /// </summary>
    private static readonly DiagnosticDescriptor s_zero = new(
        "ANKUS348", "Configuration prefix cannot contain zero characters",
        "Remove embedded zero characters from the configuration prefix; PostgreSQL receives the complete null-terminated string",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/configuration/#prefix-declaration-diagnostics");

    /// <summary>
    /// Rejects malformed Unicode instead of replacing it while encoding native input.
    /// </summary>
    private static readonly DiagnosticDescriptor s_unicode = new(
        "ANKUS349", "Configuration prefix requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in the configuration prefix; Ankus preserves exact UTF-8 text without replacement",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/configuration/#prefix-declaration-diagnostics");

    /// <summary>
    /// Reads valid prefixes without case folding or imposing SQL identifier restrictions.
    /// </summary>
    /// <param name="attributes">The assembly prefix declarations.</param>
    /// <param name="context">The diagnostic output context.</param>
    /// <returns>Distinct literal prefixes in deterministic ordinal order.</returns>
    internal static ImmutableArray<string> Read(ImmutableArray<AttributeData> attributes, GeneratorDiagnostics context)
    {
        var prefixes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (AttributeData attribute in attributes)
        {
            Location? location = FunctionDeclarationDiagnostics.ConstructorArgument(attribute, context.CancellationToken);
            if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not string prefix)
            {
                context.Report(s_value, location);
                continue;
            }

            if (prefix.Contains('\0'))
            {
                context.Report(s_zero, location);
                continue;
            }

            if (!SqlText.IsText(prefix))
            {
                context.Report(s_unicode, location);
                continue;
            }

            prefixes.Add(prefix);
        }

        return [.. prefixes];
    }

    /// <summary>
    /// Appends version-specific prefix checking after native settings have registered.
    /// </summary>
    /// <param name="prefixes">The validated literal prefixes.</param>
    /// <param name="native">The native library source.</param>
    /// <param name="registration">The ordered native initialization statements.</param>
    internal static void Emit(EquatableArray<string> prefixes, StringBuilder native, StringBuilder registration)
    {
        if (prefixes.IsEmpty)
        {
            return;
        }

        native.AppendLine("""
            #include "utils/guc.h"
            #include "miscadmin.h"

            static void
            ankus_reserve_guc_prefix(const char *utf8)
            {
                if (IsPostmasterEnvironment && !IsUnderPostmaster)
                {
                    for (const unsigned char *character = (const unsigned char *) utf8; *character != 0; character++)
                    {
                        if (*character >= 128)
                            ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                                errmsg("Ankus shared-preload configuration prefixes must be ASCII"),
                                errhint("Load the library in a backend process to use a non-ASCII prefix.")));
                    }
                }

                char *prefix = pg_any_to_server(utf8, strlen(utf8), PG_UTF8);
                PG_TRY();
                {
            #if PG_VERSION_NUM >= 150000
                    MarkGUCPrefixReserved(prefix);
            #else
                    EmitWarningsOnPlaceholders(prefix);
            #endif
                }
                PG_FINALLY();
                {
                    if (prefix != utf8)
                        pfree(prefix);
                }
                PG_END_TRY();
            }

            """);
        foreach (string prefix in prefixes)
        {
            string literal = "\"" + string.Concat(Encoding.UTF8.GetBytes(prefix).Select(static item => "\\" + Convert.ToString(item, 8).PadLeft(3, '0'))) + "\"";
            registration.AppendLine($"        ankus_reserve_guc_prefix({literal});");
        }
    }
}
