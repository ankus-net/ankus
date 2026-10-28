using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Validates native-backed configuration properties and statically resolves their managed hooks.
/// </summary>
internal sealed class GucDeclaration
{
    /// <summary>
    /// Gets the defining partial property.
    /// </summary>
    internal IPropertySymbol Property
    {
        get;
        private set;
    } = null!;

    /// <summary>
    /// Gets the exact PostgreSQL setting name.
    /// </summary>
    internal string Name
    {
        get;
        private set;
    } = string.Empty;

    /// <summary>
    /// Gets the stable transport kind: Boolean, integer, real, string, or enum.
    /// </summary>
    internal int Kind
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the validated boot value, using a dense ordinal for an enum.
    /// </summary>
    internal object? Default
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the retained short description.
    /// </summary>
    internal string ShortDescription
    {
        get;
        private set;
    } = string.Empty;

    /// <summary>
    /// Gets the optional retained long description.
    /// </summary>
    internal string? LongDescription
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the stable PostgreSQL setting context.
    /// </summary>
    internal int Context
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the stable flag bits mapped through target PostgreSQL headers.
    /// </summary>
    internal int Flags
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the unit choice mapped through target PostgreSQL headers.
    /// </summary>
    internal int Unit
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the integer or real lower bound.
    /// </summary>
    internal object? Minimum
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the integer or real upper bound.
    /// </summary>
    internal object? Maximum
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets declaration-ordered enum labels and their dense values, retaining aliases and hidden entries.
    /// </summary>
    internal List<(IFieldSymbol Field, string Name, int Ordinal, bool Hidden)> Labels { get; } = [];

    /// <summary>
    /// Gets the optional normalization and validation callback.
    /// </summary>
    internal IMethodSymbol? Check
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the optional accepted-value notification callback.
    /// </summary>
    internal IMethodSymbol? Assign
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets the optional display callback.
    /// </summary>
    internal IMethodSymbol? Show
    {
        get;
        private set;
    }

    /// <summary>
    /// Gets whether registration or access can call managed hook code.
    /// </summary>
    internal bool HasHooks => Check is not null || Assign is not null || Show is not null;

    /// <summary>
    /// Gets the exact managed property type including reference nullability.
    /// </summary>
    internal string ManagedType => Property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
        SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));

    /// <summary>
    /// Identifies one of the five typed GUC attributes.
    /// </summary>
    /// <param name="attribute">The semantic attribute.</param>
    /// <returns>Whether the attribute declares a configuration property.</returns>
    internal static bool IsGucAttribute(AttributeData attribute)
        => attribute.AttributeClass?.ToDisplayString() is "Ankus.PgGucBoolAttribute" or "Ankus.PgGucIntAttribute" or
            "Ankus.PgGucRealAttribute" or "Ankus.PgGucStringAttribute" or "Ankus.PgGucEnumAttribute";

    /// <summary>
    /// Reads a property contract and reports a precise diagnostic before generating code or native registration.
    /// </summary>
    /// <param name="property">The candidate property.</param>
    /// <param name="compilation">The compilation owning imported enum metadata.</param>
    /// <param name="context">The generator diagnostic context.</param>
    /// <returns>A validated native declaration, or null on failure.</returns>
    internal static GucDeclaration? Create(IPropertySymbol property, Compilation compilation, GeneratorDiagnostics context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        AttributeData[] attributes = [.. property.GetAttributes().Where(IsGucAttribute)];
        if (attributes.Length != 1)
        {
            return Invalid(GucDeclarationDiagnostics.AttributeCount,
                attributes.Skip(1).FirstOrDefault()?.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation());
        }

        AttributeData attribute = attributes[0];
        BasePropertyDeclarationSyntax? syntax = property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as BasePropertyDeclarationSyntax;
        if (property.IsIndexer)
        {
            return Invalid(GucDeclarationDiagnostics.Indexer);
        }

        if (!property.IsStatic)
        {
            return Invalid(GucDeclarationDiagnostics.Static);
        }

        if (property.RefKind != RefKind.None)
        {
            return Invalid(GucDeclarationDiagnostics.Reference, syntax?.Type.GetLocation());
        }

        if (property.GetMethod is null || property.SetMethod is not null)
        {
            return Invalid(GucDeclarationDiagnostics.Accessors,
                syntax?.AccessorList?.Accessors.FirstOrDefault(static accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration) ||
                    accessor.IsKind(SyntaxKind.InitAccessorDeclaration))?.Keyword.GetLocation());
        }

        if (!property.IsPartialDefinition)
        {
            return Invalid(GucDeclarationDiagnostics.Partial);
        }

        if (property.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
        {
            return Invalid(GucDeclarationDiagnostics.Access);
        }

        if (property.PartialImplementationPart is not null)
        {
            return Invalid(GucDeclarationDiagnostics.Implementation, property.PartialImplementationPart.Locations.FirstOrDefault());
        }

        for (INamedTypeSymbol? type = property.ContainingType; type is not null; type = type.ContainingType)
        {
            Location? location = type.Locations.FirstOrDefault(static item => item.IsInSource);
            if (type.TypeKind != TypeKind.Class)
            {
                return Invalid(GucDeclarationDiagnostics.ContainerKind, location);
            }

            if (type.IsGenericType)
            {
                return Invalid(GucDeclarationDiagnostics.GenericContainer, location);
            }

            if (type.IsFileLocal)
            {
                return Invalid(GucDeclarationDiagnostics.FileContainer, location);
            }

            if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return Invalid(GucDeclarationDiagnostics.ContainerAccess, location);
            }

            foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(cancellationToken) is not TypeDeclarationSyntax declaration)
                {
                    return Invalid(GucDeclarationDiagnostics.ContainerPartial, location);
                }

                if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    return Invalid(GucDeclarationDiagnostics.ContainerPartial, declaration.Identifier.GetLocation());
                }
            }
        }

        if (attribute.ConstructorArguments.Length != 3)
        {
            return Invalid(GucDeclarationDiagnostics.AttributeCount, attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation());
        }

        if (attribute.ConstructorArguments[0].Value is not string name || !IsName(name))
        {
            return Invalid(GucDeclarationDiagnostics.Name, Argument(0));
        }

        if (attribute.ConstructorArguments[2].Value is not string description || !SqlText.IsText(description))
        {
            return Invalid(GucDeclarationDiagnostics.Description, Argument(2));
        }

        int kind = attribute.AttributeClass!.Name switch
        {
            "PgGucBoolAttribute" => 0,
            "PgGucIntAttribute" => 1,
            "PgGucRealAttribute" => 2,
            "PgGucStringAttribute" => 3,
            _ => 4,
        };
        SpecialType expected = kind switch
        {
            0 => SpecialType.System_Boolean,
            1 => SpecialType.System_Int32,
            2 => SpecialType.System_Double,
            3 => SpecialType.System_String,
            _ => SpecialType.None,
        };
        if (kind == 4 ? property.Type.TypeKind != TypeKind.Enum : property.Type.SpecialType != expected)
        {
            return Invalid(GucDeclarationDiagnostics.Type, syntax?.Type.GetLocation(), attribute.AttributeClass.Name);
        }

        var result = new GucDeclaration
        {
            Property = property,
            Name = name,
            Kind = kind,
            Default = attribute.ConstructorArguments[1].Value,
            ShortDescription = description,
            LongDescription = AttributeValues.Get<string?>(attribute, "LongDescription", null),
            Context = AttributeValues.Get(attribute, "Context", 6),
            Flags = AttributeValues.Get(attribute, "Flags", 0),
            Unit = AttributeValues.Get(attribute, "Unit", 0),
        };
        if (result.LongDescription is not null && !SqlText.IsText(result.LongDescription))
        {
            return Invalid(GucDeclarationDiagnostics.LongDescription, Option("LongDescription"));
        }

        if (result.Context is < 0 or > 6)
        {
            return Invalid(GucDeclarationDiagnostics.Context, Option("Context"));
        }

        if (result.Flags < 0 || (result.Flags & ~1023) != 0)
        {
            return Invalid(GucDeclarationDiagnostics.Flags, Option("Flags"));
        }

        if (result.Unit is < 0 or > 8)
        {
            return Invalid(GucDeclarationDiagnostics.Unit, Option("Unit"));
        }

        if ((result.Flags & 32) != 0 && kind != 3)
        {
            return Invalid(GucDeclarationDiagnostics.NameFlag, Option("Flags"));
        }

        if (result.Unit != 0 && kind is not (1 or 2))
        {
            return Invalid(GucDeclarationDiagnostics.UnitKind, Option("Unit"));
        }

        switch (kind)
        {
            case 1:
                int minimum = AttributeValues.Get(attribute, "Minimum", int.MinValue);
                int maximum = AttributeValues.Get(attribute, "Maximum", int.MaxValue);
                if (minimum > maximum)
                {
                    return Invalid(GucDeclarationDiagnostics.IntegerBounds, Option("Minimum") ?? Option("Maximum"));
                }

                if (result.Default is not int integer || integer < minimum || integer > maximum)
                {
                    return Invalid(GucDeclarationDiagnostics.IntegerDefault, Argument(1));
                }

                result.Minimum = minimum;
                result.Maximum = maximum;
                break;
            case 2:
                double lower = AttributeValues.Get(attribute, "Minimum", double.MinValue);
                double upper = AttributeValues.Get(attribute, "Maximum", double.MaxValue);
                if (double.IsNaN(lower) || double.IsNaN(upper) || lower > upper)
                {
                    return Invalid(GucDeclarationDiagnostics.RealBounds,
                        double.IsNaN(upper) ? Option("Maximum") : Option("Minimum") ?? Option("Maximum"));
                }

                if (result.Default is not double real || double.IsNaN(real) || real < lower || real > upper)
                {
                    return Invalid(GucDeclarationDiagnostics.RealDefault, Argument(1));
                }

                result.Minimum = lower;
                result.Maximum = upper;
                break;
            case 3:
                if (result.Default is string text && !SqlText.IsText(text))
                {
                    return Invalid(GucDeclarationDiagnostics.StringDefault, Argument(1));
                }

                if (result.Default is null && property.NullableAnnotation != NullableAnnotation.Annotated)
                {
                    return Invalid(GucDeclarationDiagnostics.NullableDefault, syntax?.Type.GetLocation());
                }

                break;
            case 4:
                if (!SymbolEqualityComparer.Default.Equals(attribute.ConstructorArguments[1].Type, property.Type))
                {
                    return Invalid(GucDeclarationDiagnostics.EnumDefault, Argument(1));
                }

                if (!result.ReadEnum(attribute, compilation, context))
                {
                    return null;
                }

                break;
        }

        foreach (string role in new[] { "Check", "Assign", "Show" })
        {
            string? hookName = AttributeValues.Get<string?>(attribute, role, null);
            if (hookName is null)
            {
                continue;
            }

            IMethodSymbol[] candidates = [.. property.ContainingType.GetMembers(hookName).OfType<IMethodSymbol>()];
            string? problem = candidates.Length switch
            {
                0 => $"'{property.ContainingType.Name}' has no method named '{hookName}'",
                1 => result.HookProblem(candidates[0], role),
                _ => $"'{hookName}' has {candidates.Length} overloads",
            };
            if (problem is not null)
            {
                DiagnosticDescriptor descriptor = role switch
                {
                    "Check" => GucDeclarationDiagnostics.Check,
                    "Assign" => GucDeclarationDiagnostics.Assign,
                    _ => GucDeclarationDiagnostics.Show,
                };
                return Invalid(descriptor, Option(role), result.ManagedType, problem);
            }

            switch (role)
            {
                case "Check":
                    result.Check = candidates[0];
                    break;
                case "Assign":
                    result.Assign = candidates[0];
                    break;
                case "Show":
                    result.Show = candidates[0];
                    break;
            }
        }

        return result;

        Location? Argument(int index) => DatumMappingDiagnostics.Argument(attribute, index, cancellationToken);

        Location? Option(string name) => FunctionDeclarationDiagnostics.Option(attribute, name, cancellationToken);

        GucDeclaration? Invalid(DiagnosticDescriptor descriptor, Location? location = null, params string[] arguments)
        {
            context.Report(descriptor, location ?? property.Locations.FirstOrDefault(static item => item.IsInSource), arguments);
            return null;
        }
    }

    /// <summary>
    /// Maps PostgreSQL's ASCII-only case folding without changing high-bit Unicode characters.
    /// </summary>
    /// <param name="value">The setting or label name.</param>
    /// <returns>A deterministic comparison key.</returns>
    internal static string Fold(string value) => new([.. value.Select(static character => character is >= 'A' and <= 'Z' ? (char)(character + 32) : character)]);

    /// <summary>
    /// Validates the dotted custom identifier grammar without imposing SQL identifier truncation limits.
    /// </summary>
    private static bool IsName(string value)
    {
        if (!SqlText.IsText(value) || !value.Contains('.'))
        {
            return false;
        }

        foreach (string component in value.Split('.'))
        {
            if (component.Length == 0 || !IsStart(component[0]) ||
                component.Skip(1).Any(static character => !IsStart(character) && character is not (>= '0' and <= '9') && character != '$'))
            {
                return false;
            }
        }

        return true;

        static bool IsStart(char character) => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_' or >= '\u0080';
    }

    /// <summary>
    /// Builds a dense native enum table without narrowing the underlying managed values.
    /// </summary>
    private bool ReadEnum(AttributeData attribute, Compilation compilation, GeneratorDiagnostics context)
    {
        var values = new Dictionary<object, int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        bool metadataEnum = Property.Type.DeclaringSyntaxReferences.Length == 0;
        Dictionary<string, string?>? imported = Property.Type is INamedTypeSymbol type && metadataEnum ?
            GucEnumMetadata.ReadLabels(type, compilation, context.CancellationToken) : null;
        Location? propertyLocation = (Property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as
            BasePropertyDeclarationSyntax)?.Type.GetLocation();
        foreach (IFieldSymbol field in Property.Type.GetMembers().OfType<IFieldSymbol>().Where(static field => field.HasConstantValue))
        {
            AttributeData? label = field.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgGucLabelAttribute");
            string? name = label is null ? field.Name : label.ConstructorArguments.FirstOrDefault().Value as string;
            if (label is not null && metadataEnum)
            {
                name = imported is not null && imported.TryGetValue(field.MetadataName, out string? exact) ? exact : null;
            }

            Location? location = (label is null ? field.Locations.FirstOrDefault(static item => item.IsInSource) :
                DatumMappingDiagnostics.Argument(label, 0, context.CancellationToken));
            if (location?.SourceTree is not { } tree || !compilation.ContainsSyntaxTree(tree))
            {
                location = propertyLocation;
            }

            if (name is null || !SqlText.IsText(name))
            {
                context.Report(GucDeclarationDiagnostics.EnumLabel, location);
                return false;
            }

            if (!names.Add(Fold(name)))
            {
                context.Report(GucDeclarationDiagnostics.DuplicateLabel, location);
                return false;
            }

            if (!values.TryGetValue(field.ConstantValue!, out int ordinal))
            {
                ordinal = values.Count;
                values.Add(field.ConstantValue!, ordinal);
            }

            Labels.Add((field, name, ordinal, label is not null && AttributeValues.Get(label, "Hidden", false)));
        }

        if (Default is null || !values.TryGetValue(Default, out int boot))
        {
            context.Report(GucDeclarationDiagnostics.EnumDefault, DatumMappingDiagnostics.Argument(attribute, 1, context.CancellationToken));
            return false;
        }

        Default = boot;
        return true;
    }

    /// <summary>
    /// Finds the first way a hook breaks its invocation shape, nullable contract or typed result.
    /// </summary>
    /// <param name="method">The named hook.</param>
    /// <param name="role">Check, Assign or Show.</param>
    /// <returns>The problem the diagnostic states, or null when the hook can be called.</returns>
    private string? HookProblem(IMethodSymbol method, string role)
    {
        string name = $"'{method.Name}'";
        string? attribute = method.GetAttributes().Select(static item => item.AttributeClass?.ToDisplayString()).FirstOrDefault(static item =>
            item is "System.Diagnostics.ConditionalAttribute" or "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute");
        IParameterSymbol? passed = method.Parameters.FirstOrDefault(static parameter =>
            parameter.RefKind != RefKind.None || parameter.IsOptional || parameter.IsParams);
        if (!method.IsStatic)
        {
            return name + " is not static";
        }

        if (method.MethodKind != MethodKind.Ordinary)
        {
            return name + " is not an ordinary method";
        }

        if (method.IsGenericMethod)
        {
            return name + " is generic";
        }

        if (method.IsAsync || method.PartialImplementationPart?.IsAsync == true)
        {
            return name + " is async";
        }

        if (method.IsAbstract || method.IsExtern || method.IsVirtual || method.IsPartialDefinition && method.PartialImplementationPart is null)
        {
            return name + " has no body the generated hook can call";
        }

        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            return name + " returns by reference";
        }

        if (method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
        {
            return name + " is not public or internal";
        }

        if (attribute is not null)
        {
            return name + " has " + attribute.Substring(attribute.LastIndexOf('.') + 1).Replace("Attribute", string.Empty);
        }

        if (method.Parameters.Length != 2)
        {
            return $"{name} takes {method.Parameters.Length} parameter{(method.Parameters.Length == 1 ? string.Empty : "s")}";
        }

        if (passed is not null)
        {
            return $"{name} parameter '{passed.Name}' " + (passed.RefKind != RefKind.None ? "is passed by reference" : passed.IsParams ? "is params" : "has a default");
        }

        if (!SymbolEqualityComparer.IncludeNullability.Equals(method.Parameters[0].Type, Property.Type))
        {
            return $"{name} takes {method.Parameters[0].Type.ToDisplayString()} as its first parameter";
        }

        IParameterSymbol second = method.Parameters[1];
        SymbolDisplayFormat display = SymbolDisplayFormat.CSharpErrorMessageFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
        string result = method.ReturnType.WithNullableAnnotation(method.ReturnNullableAnnotation).ToDisplayString(display);
        if (role == "Check")
        {
            if (second.Type.ToDisplayString() != "Ankus.PgGucSource")
            {
                return $"{name} takes {second.Type.ToDisplayString()} as its second parameter";
            }

            return method.ReturnNullableAnnotation != NullableAnnotation.Annotated && method.ReturnType is INamedTypeSymbol checkResult &&
                checkResult.OriginalDefinition.ToDisplayString() == "Ankus.PgGucCheckResult<T>" &&
                SymbolEqualityComparer.IncludeNullability.Equals(checkResult.TypeArguments[0], Property.Type)
                ? null
                : $"{name} returns {result}";
        }

        if (second.Type is not INamedTypeSymbol { Name: "PgGucExtra" } extra || extra.ContainingNamespace.ToDisplayString() != "Ankus" ||
            second.NullableAnnotation != NullableAnnotation.Annotated)
        {
            return $"{name} takes {second.Type.WithNullableAnnotation(second.NullableAnnotation).ToDisplayString(display)} as its second parameter";
        }

        bool returns = role == "Assign" ? method.ReturnsVoid :
            method.ReturnType.SpecialType == SpecialType.System_String && method.ReturnNullableAnnotation != NullableAnnotation.Annotated;
        return returns ? null : $"{name} returns {result}";
    }
}
