using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Validates backend test declarations and emits finite catalogs for ordinary host test discovery.
/// </summary>
/// <param name="Owner">The detached lexical owner of the host discovery catalog.</param>
/// <param name="Case">The exact immutable discovery metadata.</param>
/// <param name="Order">The managed display key used for deterministic catalog ordering.</param>
/// <param name="Function">The validated native test's SQL declaration.</param>
internal sealed record PgTestDeclaration(PgTestCatalogModel.Owner Owner, PgTestCatalogModel.Case Case, string Order, FunctionDeclaration Function)
{
    /// <summary>
    /// Identifies test methods independently of whether this build enables their native exports.
    /// </summary>
    internal static bool IsTest(IMethodSymbol method)
        => method.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgTestAttribute");

    /// <summary>
    /// Validates the test shape, catalog ownership and report metadata without executing author code.
    /// </summary>
    /// <param name="method">The attributed backend test method.</param>
    /// <param name="context">The current diagnostic destination.</param>
    /// <returns>The detached discovery and SQL contracts, or null after a validation failure.</returns>
    internal static PgTestDeclaration? Create(IMethodSymbol method, GeneratorDiagnostics context)
    {
        if (!ConditionalEntryDeclaration.Validate(method, context))
        {
            return null;
        }

        if (!method.IsStatic)
        {
            return Invalid(PgTestDeclarationDiagnostics.Static, method.Locations.FirstOrDefault());
        }

        if (method.IsAsync)
        {
            return Invalid(PgTestDeclarationDiagnostics.Synchronous, Modifier(SyntaxKind.AsyncKeyword));
        }

        if (method.IsGenericMethod)
        {
            return Invalid(PgTestDeclarationDiagnostics.GenericMethod, method.Locations.FirstOrDefault());
        }

        if (method.IsAbstract)
        {
            return Invalid(PgTestDeclarationDiagnostics.Abstract, Modifier(SyntaxKind.AbstractKeyword));
        }

        if (!method.ReturnsVoid)
        {
            return Invalid(PgTestDeclarationDiagnostics.Result, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
        }

        if (method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
        {
            return Invalid(PgTestDeclarationDiagnostics.Accessibility, method.Locations.FirstOrDefault());
        }

        FunctionParameter[] parameters = FunctionParameter.Create(method);
        for (int index = 0; index < parameters.Length; index++)
        {
            FunctionParameter parameter = parameters[index];
            IParameterSymbol symbol = method.Parameters[index];
            if (!parameter.IsInjected)
            {
                return Invalid(PgTestDeclarationDiagnostics.SqlArgument, symbol.Locations.FirstOrDefault(), symbol.Name);
            }

            if (parameter.RefKind != RefKind.None)
            {
                return Invalid(PgTestDeclarationDiagnostics.ReferenceArgument, symbol.Locations.FirstOrDefault(), symbol.Name);
            }
        }

        foreach (AttributeData attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() is "Ankus.PgFunctionAttribute" or "Ankus.PgOperatorAttribute" or
                "Ankus.PgCastAttribute" or "Ankus.PgTriggerAttribute" or "Ankus.PgEventTriggerAttribute" or
                "Ankus.PgInitializeAttribute" or "Ankus.PgModuleLoadAttribute" or "Ankus.PgBackgroundWorkerAttribute")
            {
                return Invalid(PgTestDeclarationDiagnostics.ConflictingRole,
                    attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation(), attribute.AttributeClass.Name);
            }
        }

        for (INamedTypeSymbol? type = method.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.TypeKind != TypeKind.Class)
            {
                return Invalid(PgTestDeclarationDiagnostics.ContainerKind, type.Locations.FirstOrDefault(), type.Name);
            }

            if (type.Arity != 0)
            {
                return Invalid(PgTestDeclarationDiagnostics.GenericContainer, type.Locations.FirstOrDefault(), type.Name);
            }

            if (type.IsFileLocal)
            {
                return Invalid(PgTestDeclarationDiagnostics.FileContainer, type.Locations.FirstOrDefault(), type.Name);
            }

            if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return Invalid(PgTestDeclarationDiagnostics.ContainerAccessibility, type.Locations.FirstOrDefault(), type.Name);
            }

            SyntaxNode? incomplete = type.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(context.CancellationToken))
                .FirstOrDefault(static declaration => declaration is not TypeDeclarationSyntax syntax ||
                    !syntax.Modifiers.Any(SyntaxKind.PartialKeyword));
            if (incomplete is not null)
            {
                return Invalid(PgTestDeclarationDiagnostics.PartialContainer,
                    (incomplete as TypeDeclarationSyntax)?.Identifier.GetLocation() ?? incomplete.GetLocation(), type.Name);
            }
        }

        if (method.ContainingType.Name == "PostgresTests")
        {
            return Invalid(PgTestDeclarationDiagnostics.ReservedOwner, method.ContainingType.Locations.FirstOrDefault());
        }

        for (INamedTypeSymbol? type = method.ContainingType; type is not null; type = type.BaseType)
        {
            ISymbol? conflict = type.GetMembers("PostgresTests").FirstOrDefault();
            if (conflict is not null)
            {
                Location? location = SymbolEqualityComparer.Default.Equals(conflict.ContainingAssembly, method.ContainingAssembly)
                    ? conflict.Locations.FirstOrDefault(static value => value.IsInSource)
                    : method.Locations.FirstOrDefault(static value => value.IsInSource);
                return Invalid(PgTestDeclarationDiagnostics.ReservedMember, location, type.Name);
            }
        }

        AttributeData test = method.GetAttributes().First(static attribute => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgTestAttribute");
        string? expected = test.NamedArguments.FirstOrDefault(static argument => argument.Key == "ExpectedError").Value.Value as string;
        string? ignored = test.NamedArguments.FirstOrDefault(static argument => argument.Key == "IgnoreReason").Value.Value as string;
        if (expected is not null && !SqlText.IsText(expected))
        {
            return Invalid(PgTestDeclarationDiagnostics.ExpectedError, FunctionDeclarationDiagnostics.Option(test, "ExpectedError", context.CancellationToken));
        }

        if (ignored is not null && !SqlText.IsText(ignored))
        {
            return Invalid(PgTestDeclarationDiagnostics.IgnoreText, FunctionDeclarationDiagnostics.Option(test, "IgnoreReason", context.CancellationToken));
        }

        if (ignored is not null && string.IsNullOrWhiteSpace(ignored))
        {
            return Invalid(PgTestDeclarationDiagnostics.IgnoreReason, FunctionDeclarationDiagnostics.Option(test, "IgnoreReason", context.CancellationToken));
        }

        string name = method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        using SHA256 hash = SHA256.Create();
        byte[] digest = hash.ComputeHash(Encoding.UTF8.GetBytes(method.ContainingAssembly.Name + ":" + name));
        string sqlName = "ankus_test_" + string.Concat(digest.Take(16).Select(static value => value.ToString("x2", CultureInfo.InvariantCulture)));
        FunctionDeclaration? declaration = FunctionDeclaration.Create(method, sqlName, context);
        return declaration is null ? null : new(PgTestCatalogModel.Owner.Create(method.ContainingType),
            new(name, sqlName, declaration.Schema, expected, ignored), method.ToDisplayString(), declaration);

        PgTestDeclaration? Invalid(DiagnosticDescriptor descriptor, Location? location, params string[] details)
        {
            context.Report(descriptor, location ?? method.Locations.FirstOrDefault(), [method.Name, .. details]);
            return null;
        }

        Location? Modifier(SyntaxKind kind)
            => new[] { method, method.PartialImplementationPart, method.PartialDefinitionPart }.OfType<IMethodSymbol>()
                .SelectMany(static declaration => declaration.DeclaringSyntaxReferences)
                .Select(reference => reference.GetSyntax(context.CancellationToken)).OfType<MethodDeclarationSyntax>()
                .SelectMany(static declaration => declaration.Modifiers).Where(modifier => modifier.IsKind(kind))
                .Select(static modifier => modifier.GetLocation()).FirstOrDefault();
    }

    /// <summary>
    /// Emits immutable catalogs on their author-declared partial classes, independent of native test inclusion.
    /// </summary>
    /// <param name="catalog">The ordered metadata belonging to one lexical owner.</param>
    /// <returns>The owner's catalog source without the shared generated-file header.</returns>
    internal static string EmitCatalog(PgTestCatalogModel catalog)
    {
        var source = new StringBuilder();
        bool namespaced = catalog.Container.Namespace is not null;
        if (namespaced)
        {
            source.AppendLine("namespace " + catalog.Container.Namespace + "\n{");
        }

        foreach (string container in catalog.Container.Declarations)
        {
            source.AppendLine(container + "\n{");
        }

        source.AppendLine("/// <summary>\n/// Supplies backend test metadata without initializing its declaring type.\n/// </summary>");
        source.AppendLine("public static class PostgresTests\n{");
        source.AppendLine("/// <summary>\n/// Gets the statically discovered PostgreSQL tests declared by this type.\n/// </summary>");
        source.AppendLine("public static global::System.Collections.Generic.IReadOnlyList<global::Ankus.PgTestCase> Cases { get; } =");
        source.AppendLine("global::System.Array.AsReadOnly(new global::Ankus.PgTestCase[]\n{");
        foreach (PgTestCatalogModel.Case test in catalog.Cases)
        {
            source.AppendLine("new global::Ankus.PgTestCase(" + string.Join(", ", new[]
                { test.Name, test.Schema, test.FunctionName, test.ExpectedError, test.IgnoreReason }
                .Select(static value => value is null ? "null" : SymbolDisplay.FormatLiteral(value, quote: true))) + "),");
        }

        source.AppendLine("});");
        source.AppendLine("}");
        foreach (string _ in catalog.Container.Declarations)
        {
            source.AppendLine("}");
        }

        if (namespaced)
        {
            source.AppendLine("}");
        }

        return source.ToString();
    }
}
