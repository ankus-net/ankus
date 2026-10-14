using System.Globalization;
using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Renders statically bound codec readers and writers from a closed immutable graph.
/// </summary>
internal static class SerializationEmitter
{
    /// <summary>
    /// Emits a private codec inside the generated dispatcher.
    /// </summary>
    internal static void Emit(SerializationModel model, string name, StringBuilder source, string? textCodec = null)
    {
        string root = model.Nodes[0].Managed;
        source.AppendLine("    private sealed class " + name + (textCodec is null ? string.Empty : "()") +
            " : global::Ankus.CompilerServices.PgSerializedTypeCodec<" + root + ">" + (textCodec is null ? string.Empty : "(static () => new " + textCodec + "())"));
        source.AppendLine("    {");
        source.AppendLine("        protected override " + root + " ReadValue(ref global::Ankus.PgTypeReader reader) => Read_0(ref reader);");
        source.AppendLine("        protected override void WriteValue(global::Ankus.CompilerServices.PgTypeWriter writer, " + root + " value) => Write_0(writer, value);");
        foreach (SerializationModel.Node node in model.Nodes)
        {
            EmitRead(model, node, source);
            EmitWrite(model, node, source);
        }

        source.AppendLine("    }");
    }

    /// <summary>
    /// Emits exact scalar conversions and owned container construction.
    /// </summary>
    private static void EmitRead(SerializationModel model, SerializationModel.Node node, StringBuilder source)
    {
        source.AppendLine("        private static " + node.Managed + " Read_" + node.Index + "(ref global::Ankus.PgTypeReader reader)");
        source.AppendLine("        {");
        if (node.CanBeNull)
        {
            source.AppendLine("            if (reader.ReadNull()) return null;");
        }
        else
        {
            source.AppendLine("            if (reader.ReadNull()) throw new global::System.FormatException(\"A required value cannot be null.\");");
        }

        switch (node.Kind)
        {
            case "primitive":
                string conversion = node.Primitive is "Int64" or "UInt64" ? "checked((" + node.Managed + ")reader.Read" + node.Primitive + "())" :
                    "reader.Read" + node.Primitive + "()";
                source.AppendLine("            return " + conversion + ";");
                break;
            case "nullable":
                source.AppendLine("            return Read_" + node.Element!.Value + "(ref reader);");
                break;
            case "enum":
                source.AppendLine("            return reader.ReadString() switch");
                source.AppendLine("            {");
                foreach ((string member, string name) in node.EnumMembers)
                {
                    source.AppendLine("                " + Literal(name) + " => " + node.Managed + ".@" + member + ",");
                }

                source.AppendLine("                _ => throw new global::System.FormatException(\"Unknown enum name.\"),");
                source.AppendLine("            };");
                break;
            case "array":
            case "list":
                source.AppendLine("            reader.ReadStartArray();");
                source.AppendLine("            var values = new global::System.Collections.Generic.List<" + model.Nodes[node.Element!.Value].Managed + ">();");
                source.AppendLine("            while (!reader.ReadEndArray()) values.Add(Read_" + node.Element!.Value + "(ref reader));");
                source.AppendLine("            return values" + (node.Kind == "array" ? ".ToArray()" : "") + ";");
                break;
            case "dictionary":
                source.AppendLine("            reader.ReadStartObject();");
                source.AppendLine("            var values = new global::System.Collections.Generic.Dictionary<string, " + model.Nodes[node.Element!.Value].Managed + ">(global::System.StringComparer.Ordinal);");
                source.AppendLine("            string? key;");
                source.AppendLine("            while ((key = reader.ReadPropertyName()) is not null)");
                source.AppendLine("            {");
                source.AppendLine("                if (!values.TryAdd(key, Read_" + node.Element!.Value + "(ref reader))) throw new global::System.FormatException(\"Duplicate dictionary key.\");");
                source.AppendLine("            }");
                source.AppendLine("            return values;");
                break;
            case "polymorphic":
                source.AppendLine("            return reader.PeekDiscriminator(" + Literal(node.DiscriminatorName) + ") switch");
                source.AppendLine("            {");
                foreach (SerializationModel.Variant variant in node.Variants)
                {
                    SerializationModel.Node shape = model.Nodes[variant.Shape];
                    object tag = variant.Text is { } text ? text : variant.Number!.Value;
                    source.AppendLine("                " + DiscriminatorLiteral(tag) + " => Read_" + shape.Index + "(ref reader),");
                }

                source.AppendLine(node.BaseShape is { } baseShape ? "                null => Read_" + baseShape + "(ref reader)," :
                    "                null => throw new global::System.FormatException(\"Missing type discriminator.\"),");
                source.AppendLine("                _ => throw new global::System.FormatException(\"Unknown type discriminator.\"),");
                source.AppendLine("            };");
                break;
            default:
                EmitReadObject(model, node, source);
                break;
        }

        source.AppendLine("        }");
    }

    /// <summary>
    /// Collects fields before invoking a selected constructor exactly once.
    /// </summary>
    private static void EmitReadObject(SerializationModel model, SerializationModel.Node node, StringBuilder source)
    {
        source.AppendLine("            reader.ReadStartObject();");
        for (int index = 0; index < node.Members.Count; index++)
        {
            source.AppendLine("            " + model.Nodes[node.Members[index].Value].Managed + " value" + index + " = default!;");
            source.AppendLine("            bool seen" + index + " = false;");
        }

        EmitMemberSwitch(node, source);
        for (int index = 0; index < node.Members.Count; index++)
        {
            SerializationModel.Member member = node.Members[index];
            if (member.Required || !model.Nodes[member.Value].CanBeNull)
            {
                source.AppendLine("            if (!seen" + index + ") throw new global::System.FormatException(" + Literal("Missing required member: " + member.SerializedName) + ");");
            }
        }

        EmitConstruction(node, source);
    }

    /// <summary>
    /// Reads each property into its member's slot, rejecting a repeated member and skipping unknown ones.
    /// </summary>
    private static void EmitMemberSwitch(SerializationModel.Node node, StringBuilder source)
    {
        source.AppendLine("            string? key;");
        source.AppendLine("            while ((key = reader.ReadPropertyName()) is not null)");
        source.AppendLine("            {");
        source.AppendLine("                switch (key)");
        source.AppendLine("                {");
        for (int index = 0; index < node.Members.Count; index++)
        {
            SerializationModel.Member member = node.Members[index];
            source.AppendLine("                    case " + Literal(member.SerializedName) + ":");
            source.AppendLine("                        if (seen" + index + ") throw new global::System.FormatException(" + Literal("Duplicate member: " + member.SerializedName) + ");");
            source.AppendLine("                        seen" + index + " = true;");
            source.AppendLine("                        value" + index + " = Read_" + member.Value + "(ref reader);");
            source.AppendLine("                        break;");
        }

        source.AppendLine("                    default: reader.Skip(); break;");
        source.AppendLine("                }");
        source.AppendLine("            }");
    }

    /// <summary>
    /// Calls the selected constructor once and sets the remaining writable members.
    /// </summary>
    private static void EmitConstruction(SerializationModel.Node node, StringBuilder source)
    {
        source.AppendLine("            return new " + node.ConstructionType + "(" +
            string.Join(", ", node.ConstructorMembers.Select(static index => "value" + index)) + ")");
        source.AppendLine("            {");
        for (int index = 0; index < node.Members.Count; index++)
        {
            SerializationModel.Member member = node.Members[index];
            if (member.Writable && !node.ConstructorMembers.Contains(index))
            {
                source.AppendLine("                @" + member.Name + " = value" + index + ",");
            }
        }

        source.AppendLine("            };");
    }

    /// <summary>
    /// Emits member reads without reflection, preserving the declared nullable graph.
    /// </summary>
    private static void EmitWrite(SerializationModel model, SerializationModel.Node node, StringBuilder source)
    {
        source.AppendLine("        private static void Write_" + node.Index + "(global::Ankus.CompilerServices.PgTypeWriter writer, " + node.Managed + " value)");
        source.AppendLine("        {");
        if (node.CanBeNull)
        {
            source.AppendLine("            if (value is null) { writer.WriteNull(); return; }");
        }
        else if (node.IsReferenceType)
        {
            source.AppendLine("            if (value is null) throw new global::System.InvalidOperationException(\"A required value cannot be null.\");");
        }

        if (node.IsReferenceType && node.Kind is not ("primitive" or "polymorphic"))
        {
            source.AppendLine("            if (value.GetType() != typeof(" + node.ConstructionType +
                ")) throw new global::System.InvalidOperationException(\"Runtime subtypes require an explicit codec.\");");
        }

        switch (node.Kind)
        {
            case "primitive":
                source.AppendLine("            writer.Write" + node.Primitive + "(value);");
                break;
            case "nullable":
                source.AppendLine("            Write_" + node.Element!.Value + "(writer, value.Value);");
                break;
            case "enum":
                source.AppendLine("            writer.WriteString(value switch");
                source.AppendLine("            {");
                foreach ((string member, string name) in node.EnumMembers)
                {
                    source.AppendLine("                " + node.Managed + ".@" + member + " => " + Literal(name) + ",");
                }

                source.AppendLine("                _ => throw new global::System.InvalidOperationException(\"Unnamed enum values cannot be serialized.\"),");
                source.AppendLine("            });");
                break;
            case "array":
            case "list":
                source.AppendLine("            writer.WriteStartArray(value." + (node.Kind == "array" ? "Length" : "Count") + ");");
                source.AppendLine("            foreach (" + model.Nodes[node.Element!.Value].Managed + " item in value) Write_" + node.Element!.Value + "(writer, item);");
                source.AppendLine("            writer.WriteEndArray();");
                break;
            case "dictionary":
                source.AppendLine("            global::System.Collections.Generic.KeyValuePair<string, " + model.Nodes[node.Element!.Value].Managed +
                    ">[] entries = global::Ankus.CompilerServices.PgTypeWriter.GetOrderedEntries(value);");
                source.AppendLine("            writer.WriteStartObject(entries.Length);");
                source.AppendLine("            foreach (global::System.Collections.Generic.KeyValuePair<string, " + model.Nodes[node.Element!.Value].Managed + "> item in entries)");
                source.AppendLine("            {");
                source.AppendLine("                writer.WritePropertyName(item.Key);");
                source.AppendLine("                Write_" + node.Element!.Value + "(writer, item.Value);");
                source.AppendLine("            }");
                source.AppendLine("            writer.WriteEndObject();");
                break;
            case "polymorphic":
                foreach (SerializationModel.Variant variant in node.Variants)
                {
                    SerializationModel.Node shape = model.Nodes[variant.Shape];
                    object tag = variant.Text is { } text ? text : variant.Number!.Value;
                    source.AppendLine("            if (value.GetType() == typeof(" + shape.Managed + "))");
                    source.AppendLine("            {");
                    source.AppendLine("                var variant = (" + shape.Managed + ")value;");
                    EmitWriteObject(shape, source, "variant", "                ", node.DiscriminatorName, tag);
                    source.AppendLine("                return;");
                    source.AppendLine("            }");
                }

                if (node.BaseShape is { } baseIndex && !node.Variants.Any(variant => variant.Shape == baseIndex))
                {
                    SerializationModel.Node baseShape = model.Nodes[baseIndex];
                    source.AppendLine("            if (value.GetType() == typeof(" + baseShape.Managed + "))");
                    source.AppendLine("            {");
                    EmitWriteObject(baseShape, source, "value", "                ");
                    source.AppendLine("                return;");
                    source.AppendLine("            }");
                }

                source.AppendLine("            throw new global::System.InvalidOperationException(\"Unregistered runtime subtype.\");");
                break;
            default:
                EmitWriteObject(node, source, "value", "            ");
                break;
        }

        source.AppendLine("        }");
    }

    /// <summary>
    /// Writes an exact object's state with an optional discriminator in the same map.
    /// </summary>
    private static void EmitWriteObject(SerializationModel.Node node, StringBuilder source, string value, string indent,
        string? discriminatorName = null, object? tag = null)
    {
        source.AppendLine(indent + "writer.WriteStartObject(" + (node.Members.Count + (tag is null ? 0 : 1)).ToString(CultureInfo.InvariantCulture) + ");");
        if (tag is not null)
        {
            source.AppendLine(indent + "writer.WritePropertyName(" + Literal(discriminatorName!) + ");");
            source.AppendLine(indent + "writer.Write" + (tag is string ? "String" : "Int64") + "(" + DiscriminatorLiteral(tag) + ");");
        }

        foreach (SerializationModel.Member member in node.Members)
        {
            source.AppendLine(indent + "writer.WritePropertyName(" + Literal(member.SerializedName) + ");");
            source.AppendLine(indent + "Write_" + member.Value + "(writer, " + value + ".@" + member.Name + ");");
        }

        source.AppendLine(indent + "writer.WriteEndObject();");
    }

    /// <summary>
    /// Emits a discriminator without conflating integer and string identities.
    /// </summary>
    private static string DiscriminatorLiteral(object tag) => tag is string text ? Literal(text) : ((int)tag).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Escapes an exact managed string constant without instantiating author metadata.
    /// </summary>
    private static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, true);
}
