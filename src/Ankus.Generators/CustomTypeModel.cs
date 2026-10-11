using System.Globalization;
using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Holds a validated custom type's storage and conversion contracts independently of compiler state.
/// </summary>
/// <param name="Name">The exact catalog name.</param>
/// <param name="Schema">The fixed schema, or null for the extension schema.</param>
/// <param name="Managed">The globally qualified payload type.</param>
/// <param name="Symbol">The assembly-specific native symbol suffix.</param>
/// <param name="IsValueType">Whether registration uses a value-type contract.</param>
/// <param name="Codec">The optional explicitly authored storage codec.</param>
/// <param name="TextCodec">The optional explicitly authored text codec.</param>
/// <param name="Serializer">The optional statically validated serialization graph.</param>
/// <param name="NativeSize">The proved native payload size, or zero for serialized storage.</param>
/// <param name="NativePadding">Comma-separated offset and length pairs of the native layout's padding.</param>
/// <param name="BinaryProtocol">Whether binary send and receive functions are generated.</param>
/// <param name="Alignment">The validated PostgreSQL storage alignment.</param>
/// <param name="NullInputErrorMessage">The optional error for a null text input.</param>
internal sealed record CustomTypeModel(string Name, string? Schema, string Managed, string Symbol, bool IsValueType,
    string? Codec, string? TextCodec, SerializationModel? Serializer, int NativeSize, string NativePadding, bool BinaryProtocol,
    string Alignment, string? NullInputErrorMessage)
{
    /// <summary>
    /// Gets the qualified SQL type name.
    /// </summary>
    internal string Sql => Io.Sql;

    /// <summary>
    /// Gets the independent scalar I/O rendering contract.
    /// </summary>
    internal CustomTypeIoModel Io => new(new(Name, Schema, Managed), Symbol, IsValueType, BinaryProtocol, Alignment, NullInputErrorMessage);

    /// <summary>
    /// Gets whether storage has a proved native layout.
    /// </summary>
    internal bool NativeLayout => NativeSize != 0;

    /// <summary>
    /// Qualifies an I/O name, retaining the existing bounded native-symbol fallback for long identifiers.
    /// </summary>
    /// <param name="role">The input, output, receive or send suffix.</param>
    /// <returns>The qualified function name.</returns>
    internal string Function(string role)
        => Io.Function(role);

    /// <summary>
    /// Gets the native entry point for an I/O role.
    /// </summary>
    /// <param name="role">The input, output, receive or send suffix.</param>
    /// <returns>The exact exported symbol.</returns>
    internal string NativeFunction(string role) => Io.NativeFunction(role);

    /// <summary>
    /// Renders native padding pairs as the generated array argument.
    /// </summary>
    /// <param name="padding">Comma-separated offset and length pairs.</param>
    /// <returns>The C# array expression.</returns>
    private static string Padding(string padding) => padding.Length == 0 ? "[]" : "new int[] { " + padding.Replace(",", ", ") + " }";

    /// <summary>
    /// Emits closed registration with nullable and collection instantiations visible to Native AOT.
    /// </summary>
    /// <param name="contract">The immutable registration inputs.</param>
    /// <param name="source">The managed initialization body.</param>
    internal static void EmitRegistration(RegistrationContract contract, StringBuilder source)
    {
        source.AppendLine("        global::Ankus.CompilerServices.PgTypeRegistry.Register" + (contract.NativeSize != 0 ? "Native" : contract.IsValueType ? "Value" : "Reference") + "<" + contract.Managed + ">(" +
            Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(contract.Name, true) + ", " +
            (contract.Schema is null ? "null" : Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(contract.Schema, true)) + ", " +
            (contract.NativeSize != 0 ? contract.NativeSize.ToString(CultureInfo.InvariantCulture) + ", " + Padding(contract.NativePadding) + ", " : string.Empty) +
            "static () => new " + contract.Codec + "());");
    }

    /// <summary>
    /// Emits the owned generated serializer or statically sized native-storage wrapper.
    /// </summary>
    /// <param name="contract">The immutable storage rendering inputs.</param>
    /// <param name="source">The managed dispatcher declarations.</param>
    internal static void EmitSerializer(SerializerContract contract, StringBuilder source)
    {
        if (contract.NativeSize != 0)
        {
            source.AppendLine("    private sealed class Codec_" + contract.Symbol + " : global::Ankus.PgTypeCodec<" + contract.Managed + ">");
            source.AppendLine("    {");
            source.AppendLine("        private readonly global::Ankus.CompilerServices.PgNativeTypeCodec<" + contract.Managed + "> _codec = new(" +
                contract.NativeSize.ToString(CultureInfo.InvariantCulture) + ", " + Padding(contract.NativePadding) + ", static () => new " +
                contract.TextCodec + "());");
            source.AppendLine("        public override " + contract.Managed + " Parse(string text) => _codec.Parse(text);");
            source.AppendLine("        public override string Format(" + contract.Managed + " value) => _codec.Format(value);");
            source.AppendLine("        public override " + contract.Managed + " Read(global::System.ReadOnlySpan<byte> payload) => _codec.Read(payload);");
            source.AppendLine("        public override void Write(" + contract.Managed + " value, global::System.Buffers.IBufferWriter<byte> destination) => _codec.Write(value, destination);");
            source.AppendLine("    }");
        }
        else
        {
            contract.Serializer?.Emit("Codec_" + contract.Symbol, source, contract.TextCodec);
        }
    }

    /// <summary>
    /// Emits the backend catalog check for this validated base type.
    /// </summary>
    /// <param name="contract">The immutable catalog identity.</param>
    /// <param name="source">The native supported-type predicate.</param>
    internal static void EmitNativeTypeCheck(CatalogContract contract, StringBuilder source)
    {
        source.AppendLine("    {");
        source.AppendLine("        const AnkusValue name = { .data = (unsigned char *) " + Utf8Literal(contract.Name) + ", .length = " + Encoding.UTF8.GetByteCount(contract.Name).ToString(CultureInfo.InvariantCulture) + " };");
        source.AppendLine(contract.Schema is null ? "        const AnkusValue schema = { .is_null = 1 };" :
            "        const AnkusValue schema = { .data = (unsigned char *) " + Utf8Literal(contract.Schema) + ", .length = " + Encoding.UTF8.GetByteCount(contract.Schema).ToString(CultureInfo.InvariantCulture) + " };");
        source.AppendLine("        if (ankus_resolve_named_type(&name, &schema, true, TYPTYPE_BASE) == type) return true;");
        source.AppendLine("    }");
    }

    /// <summary>
    /// Escapes exact UTF-8 bytes for a generated native string literal.
    /// </summary>
    private static string Utf8Literal(string value) => "\"" + string.Concat(Encoding.UTF8.GetBytes(value).Select(static item =>
        "\\x" + item.ToString("x2", CultureInfo.InvariantCulture))) + "\"";

    /// <summary>
    /// Defines only the constants consumed by managed registration.
    /// </summary>
    /// <param name="Name">The exact catalog name.</param>
    /// <param name="Schema">The optional fixed schema.</param>
    /// <param name="Managed">The globally qualified payload.</param>
    /// <param name="IsValueType">Whether nullable value registrations are required.</param>
    /// <param name="NativeSize">The native payload size, or zero for serialized storage.</param>
    /// <param name="NativePadding">Comma-separated offset and length pairs of the native layout's padding.</param>
    /// <param name="Codec">The exact constructed codec name.</param>
    internal sealed record RegistrationContract(string Name, string? Schema, string Managed, bool IsValueType, int NativeSize, string NativePadding,
        string Codec);

    /// <summary>
    /// Defines only the graph and codec constants consumed by storage rendering.
    /// </summary>
    /// <param name="Managed">The globally qualified payload.</param>
    /// <param name="Symbol">The generated codec identity.</param>
    /// <param name="NativeSize">The native payload size, or zero for serialized storage.</param>
    /// <param name="NativePadding">Comma-separated offset and length pairs of the native layout's padding.</param>
    /// <param name="TextCodec">The optional explicitly authored text codec.</param>
    /// <param name="Serializer">The optional immutable serialization graph.</param>
    internal sealed record SerializerContract(string Managed, string Symbol, int NativeSize, string NativePadding, string? TextCodec,
        SerializationModel? Serializer);

    /// <summary>
    /// Defines only the catalog identity consumed by native supported-type checking.
    /// </summary>
    /// <param name="Name">The exact catalog name.</param>
    /// <param name="Schema">The optional fixed schema.</param>
    internal sealed record CatalogContract(string Name, string? Schema);
}
