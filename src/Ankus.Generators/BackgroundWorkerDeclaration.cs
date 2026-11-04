using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Selects finite managed worker entries and validates their native export contracts.
/// </summary>
/// <param name="Target">The fully qualified managed invocation target.</param>
/// <param name="EntryPoint">The exact exported native symbol.</param>
/// <param name="Callback">The assembly-specific managed dispatcher symbol.</param>
internal sealed record BackgroundWorkerDeclaration(string Target, string EntryPoint, string Callback)
{
    /// <summary>
    /// Retains C keywords and PostgreSQL loader symbols that cannot identify a worker export.
    /// </summary>
    private static readonly HashSet<string> s_reserved = new(StringComparer.Ordinal)
    {
        "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum", "extern",
        "float", "for", "goto", "if", "inline", "int", "long", "register", "restrict", "return", "short", "signed",
        "sizeof", "static", "struct", "switch", "typedef", "union", "unsigned", "void", "volatile", "while", "bool",
        "true", "false", "alignas", "alignof", "constexpr", "nullptr", "static_assert", "thread_local", "typeof",
        "typeof_unqual", "Pg_magic_func", "_PG_init", "_PG_fini", OutputPluginDeclaration.ExportName,
    };

    /// <summary>
    /// Identifies a worker independently of SQL function discovery.
    /// </summary>
    internal static bool IsWorker(IMethodSymbol method) => method.GetAttributes().Any(static attribute =>
        attribute.AttributeClass?.ToDisplayString() == "Ankus.PgBackgroundWorkerAttribute");

    /// <summary>
    /// Validates one normalized worker signature, container, metadata and portable native identity.
    /// </summary>
    /// <param name="method">The attributed method, normalized to its partial definition.</param>
    /// <param name="context">The current semantic diagnostic receiver.</param>
    /// <returns>The immutable invocation and export contracts, or null after an error.</returns>
    internal static BackgroundWorkerDeclaration? Create(IMethodSymbol method, GeneratorDiagnostics context)
    {
        if (method.MethodKind != MethodKind.Ordinary)
        {
            return Report(WorkerDeclarationDiagnostics.s_kind, Attribute("Ankus.PgBackgroundWorkerAttribute"));
        }

        if (!method.IsStatic)
        {
            return Report(WorkerDeclarationDiagnostics.s_static, method.Locations.FirstOrDefault());
        }

        if (method.IsAsync || method.PartialImplementationPart?.IsAsync == true)
        {
            return Report(WorkerDeclarationDiagnostics.s_async, Modifier(SyntaxKind.AsyncKeyword));
        }

        if (method.IsGenericMethod)
        {
            return Report(WorkerDeclarationDiagnostics.s_generic, Syntax()?.TypeParameterList?.GetLocation());
        }

        if (method.IsAbstract)
        {
            return Report(WorkerDeclarationDiagnostics.s_abstract, Modifier(SyntaxKind.AbstractKeyword));
        }

        if (method.IsVirtual)
        {
            return Report(WorkerDeclarationDiagnostics.s_virtual, Modifier(SyntaxKind.VirtualKeyword));
        }

        if (method.IsExtern)
        {
            return Report(WorkerDeclarationDiagnostics.s_extern, Modifier(SyntaxKind.ExternKeyword));
        }

        if (!method.ReturnsVoid)
        {
            return Report(WorkerDeclarationDiagnostics.s_result, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken),
                method.ReturnType.ToDisplayString());
        }

        if (method.Parameters.Length != 1)
        {
            return Report(WorkerDeclarationDiagnostics.s_parameterCount, Syntax()?.ParameterList.GetLocation());
        }

        IParameterSymbol parameter = method.Parameters[0];
        if (parameter.RefKind != RefKind.None)
        {
            return Report(WorkerDeclarationDiagnostics.s_parameterRefKind, ParameterSyntax()?.GetLocation(), parameter.Name);
        }

        if (parameter.Type.SpecialType != SpecialType.System_UIntPtr)
        {
            return Report(WorkerDeclarationDiagnostics.s_parameterType, ParameterSyntax()?.Type?.GetLocation(), parameter.Type.ToDisplayString());
        }

        if (method.IsPartialDefinition && method.PartialImplementationPart is null)
        {
            return Report(WorkerDeclarationDiagnostics.s_implementation, method.Locations.FirstOrDefault());
        }

        if (method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
        {
            return Report(WorkerDeclarationDiagnostics.s_accessibility, Modifier(method.DeclaredAccessibility is Accessibility.Private or
                Accessibility.ProtectedAndInternal ? SyntaxKind.PrivateKeyword : SyntaxKind.ProtectedKeyword));
        }

        for (INamedTypeSymbol? type = method.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.IsGenericType)
            {
                return Report(WorkerDeclarationDiagnostics.s_genericContainer, type.Locations.FirstOrDefault(), type.Name);
            }

            if (type.IsFileLocal)
            {
                return Report(WorkerDeclarationDiagnostics.s_fileContainer, type.Locations.FirstOrDefault(), type.Name);
            }

            if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return Report(WorkerDeclarationDiagnostics.s_containerAccessibility, type.Locations.FirstOrDefault(), type.Name);
            }
        }

        ImmutableArray<AttributeData> metadata = method.GetAttributes();
        AttributeData? conditional = Find(metadata, "System.Diagnostics.ConditionalAttribute");
        if (conditional is not null)
        {
            return Report(WorkerDeclarationDiagnostics.s_conditional, Location(conditional));
        }

        AttributeData? unmanaged = Find(metadata, "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute");
        if (unmanaged is not null)
        {
            return Report(WorkerDeclarationDiagnostics.s_unmanagedOnly, Location(unmanaged));
        }

        AttributeData? phase = metadata.FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() is
            "Ankus.PgInitializeAttribute" or "Ankus.PgModuleLoadAttribute");
        if (phase is not null)
        {
            return Report(WorkerDeclarationDiagnostics.s_initializationPhase, Location(phase), AttributeName(phase));
        }

        AttributeData? role = metadata.FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() is
            "Ankus.PgFunctionAttribute" or "Ankus.PgTriggerAttribute" or "Ankus.PgEventTriggerAttribute" or
            "Ankus.PgOperatorAttribute" or "Ankus.PgCastAttribute");
        if (role is not null)
        {
            return Report(WorkerDeclarationDiagnostics.s_sqlRole, Location(role), AttributeName(role));
        }

        AttributeData? result = method.GetReturnTypeAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ContainingNamespace.ToDisplayString() == "Ankus");
        if (result is not null)
        {
            return Report(WorkerDeclarationDiagnostics.s_resultMetadata, Location(result), AttributeName(result));
        }

        AttributeData? argument = parameter.GetAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ContainingNamespace.ToDisplayString() == "Ankus");
        if (argument is not null)
        {
            return Report(WorkerDeclarationDiagnostics.s_parameterMetadata, Location(argument), AttributeName(argument));
        }

        AttributeData marker = metadata.First(static attribute => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgBackgroundWorkerAttribute");
        string entry = marker.NamedArguments.FirstOrDefault(static pair => pair.Key == "EntryPoint").Value.Value as string ?? method.Name;
        Location? exportLocation = FunctionDeclarationDiagnostics.Option(marker, "EntryPoint", context.CancellationToken) ?? method.Locations.FirstOrDefault();
        if (entry.Length > 95)
        {
            return Report(WorkerDeclarationDiagnostics.s_exportLength, exportLocation, entry);
        }

        if (s_reserved.Contains(entry) || entry.StartsWith("ankus_", StringComparison.Ordinal) || entry.StartsWith("pg_finfo_", StringComparison.Ordinal))
        {
            return Report(WorkerDeclarationDiagnostics.s_reservedExport, exportLocation, entry);
        }

        if (entry.Length == 0 || !IsLetter(entry[0]) || entry.Any(static value => !IsLetter(value) && value is not (>= '0' and <= '9') && value != '_'))
        {
            return Report(WorkerDeclarationDiagnostics.s_invalidExport, exportLocation, entry);
        }

        return new(MethodInvocation.Create(method).Target, entry, PgFunctionGenerator.GetCallbackName(method, "worker"));

        BackgroundWorkerDeclaration? Report(DiagnosticDescriptor descriptor, Location? location, params string[] details)
        {
            context.Report(descriptor, location ?? method.Locations.FirstOrDefault(), [method.Name, .. details]);
            return null;
        }

        Location? Attribute(string name) => Location(Find(method.GetAttributes(), name));

        Location? Location(AttributeData? attribute) => attribute?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation();

        MethodDeclarationSyntax? Syntax() => method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as MethodDeclarationSyntax;

        ParameterSyntax? ParameterSyntax() => Syntax()?.ParameterList.Parameters.FirstOrDefault();

        Location? Modifier(SyntaxKind kind)
        {
            IMethodSymbol authored = method.PartialImplementationPart ?? method;
            var syntax = authored.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as MethodDeclarationSyntax;
            SyntaxToken token = syntax?.Modifiers.FirstOrDefault(value => value.IsKind(kind)) ?? default;
            return token.RawKind != 0 ? token.GetLocation() : method.Locations.FirstOrDefault();
        }
    }

    /// <summary>
    /// Reports a duplicate native identity only after the current extension-wide worker inventory is known.
    /// </summary>
    /// <param name="location">The current declaring source location.</param>
    /// <param name="name">The original managed method name.</param>
    /// <param name="entry">The conflicting native export.</param>
    /// <param name="context">The current production diagnostic receiver.</param>
    internal static void ReportDuplicate(Location? location, string name, string entry, GeneratorDiagnostics context)
        => context.Report(WorkerDeclarationDiagnostics.s_duplicateExport, location, name, entry);

    /// <summary>
    /// Recognizes the portable ASCII letters admitted in an exported native symbol.
    /// </summary>
    private static bool IsLetter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    /// <summary>
    /// Finds one exact semantic attribute identity without matching unrelated names.
    /// </summary>
    private static AttributeData? Find(IEnumerable<AttributeData> metadata, string name)
        => metadata.FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == name);

    /// <summary>
    /// Names the actual conflicting attribute in a separately classified diagnostic.
    /// </summary>
    private static string AttributeName(AttributeData attribute) => attribute.AttributeClass!.Name.Replace("Attribute", string.Empty);
}
