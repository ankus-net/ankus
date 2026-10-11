using System.CommandLine;
using System.Runtime.InteropServices;
using System.Xml;

namespace Ankus.Tool;

internal static partial class ToolCommand
{
    /// <summary>
    /// Registers literal MSBuild properties without consuming SQL client or schema arguments.
    /// </summary>
    /// <param name="command">The project command.</param>
    private static void AddPropertyOption(Command command)
    {
        var property = new Option<string[]>("--property", "-p")
        {
            Description = "Literal MSBuild name=value property; repeat for multiple properties.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        property.Validators.Add(result =>
        {
            foreach (string value in result.GetValueOrDefault<string[]>() ?? [])
            {
                if (PropertyError(value) is string error)
                {
                    result.AddError(error);
                }
            }
        });
        command.Options.Add(property);
    }

    /// <summary>
    /// Rejects missing or invalid property names while preserving literal and empty values.
    /// </summary>
    /// <param name="property">One literal assignment.</param>
    /// <returns>The validation error, or null for a valid assignment.</returns>
    private static string? PropertyError(string property)
    {
        int separator = property.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return "MSBuild properties must contain name=value with a valid property name.";
        }

        try
        {
            XmlConvert.VerifyNCName(property[..separator]);
        }
        catch (XmlException)
        {
            return "MSBuild properties must contain name=value with a valid property name.";
        }

        return null;
    }

    /// <summary>
    /// Reads case-insensitive global properties and validates the host publication contract.
    /// </summary>
    /// <param name="result">The parsed project command.</param>
    /// <returns>The literal properties; repeated names retain the last value as in MSBuild.</returns>
    private static Dictionary<string, string> BuildProperties(ParseResult result)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!result.CommandResult.Command.Options.Any(static option => option.Name == "--property"))
        {
            return properties;
        }

        foreach (string property in result.GetValue<string[]>("--property") ?? [])
        {
            int separator = property.IndexOf('=', StringComparison.Ordinal);
            properties[property[..separator]] = property[(separator + 1)..];
        }

        if (properties.TryGetValue("RuntimeIdentifier", out string? runtime) && runtime != RuntimeInformation.RuntimeIdentifier)
        {
            throw new ArgumentException("Project commands publish for the host RuntimeIdentifier; use --target-info to build for another machine.");
        }

        if (properties.ContainsKey("AnkusTargetInfo"))
        {
            throw new ArgumentException("Select target information with --target-info.");
        }

        if (properties.TryGetValue("SelfContained", out string? selfContained) &&
            !string.Equals(selfContained, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Native AOT project commands require SelfContained=true.");
        }

        return properties;
    }
}
