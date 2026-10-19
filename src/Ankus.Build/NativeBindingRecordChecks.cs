using System.Globalization;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Reconstructs measured native types and compares them with actual header declarations in the compiling toolchain.
/// </summary>
internal static partial class NativeBindingRecordChecks
{
    /// <summary>
    /// Reports the failed physical field without narrowing its declaration index to an operating-system exit code.
    /// </summary>
    internal const string ExecutableEntryPoint = """

        #include <stdio.h>
        int main(void)
        {
            const char *error = ankus_native_record_check();
            if (error == NULL) return 0;
            fputs(error, stderr);
            fputc('\n', stderr);
            return 1;
        }
        """;

    /// <summary>
    /// Emits compiler assertions for the complete declaration graph, independently of selected call bodies.
    /// </summary>
    /// <param name="records">The independently collected signatures and complete transitive layout graph.</param>
    /// <param name="headers">The original headers for the selected native target.</param>
    /// <returns>A translation unit which rejects incompatible native members, aliases and storage.</returns>
    internal static string Generate(NativeHeaderRecords records, string headers)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(headers);
        NativeBindingSignatureValidation.Validate(records);
        return new Writer(records).Generate(headers);
    }

    /// <summary>
    /// Reuses actual native declaration anchors when defining measured argument and result value aliases.
    /// </summary>
    /// <param name="records">The complete independently collected signature and storage contract.</param>
    /// <param name="values">Measured type identities and distinct generated C alias names.</param>
    /// <returns>Native typedefs and storage checks for inclusion after the original headers.</returns>
    internal static string DeclareValues(NativeHeaderRecords records, IReadOnlyList<(int Type, string Name)> values)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(values);
        NativeBindingSignatureValidation.Validate(records);
        return new Writer(records).DeclareValues(values);
    }

    /// <summary>
    /// Uses actual C tags, typedefs and member expressions to preserve declaration identity.
    /// </summary>
    private sealed partial class Writer(NativeHeaderRecords records)
    {
        private readonly NativeRecordGraph _graph = records.Graph;
        private readonly NativeBindingRecordMembers _members = new(records.Graph);
        private readonly Dictionary<int, string> _anchors = [];
        private readonly HashSet<int> _functionResults = [];
        private readonly StringBuilder _source = new();
        private int _functionComparison;

        /// <summary>
        /// Emits only requested value typedefs while retaining the complete graph's actual record identities.
        /// </summary>
        internal string DeclareValues(IReadOnlyList<(int Type, string Name)> values)
        {
            if (values.Count == 0)
            {
                return "";
            }

            _source.AppendLine("#include <stdarg.h>");
            WriteTypeNormalization();
            AnchorDeclarations();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach ((int index, string name) in values)
            {
                NativeBindingCDeclaration.ValidateName(name);
                if ((uint)index >= (uint)_graph.Types.Count || !names.Add(name))
                {
                    throw new FormatException("A native value alias requires a valid type and unique name.");
                }

                NativeRecordType type = _graph.Types[index];
                if (type.Size is null || type.Alignment is not > 0)
                {
                    throw new FormatException("A native value alias requires complete object storage.");
                }

                string declaration = Declare(index, name);
                _source.Append("typedef ").Append(declaration).AppendLine(";");
                Storage(name, type.Size, type.Alignment, "value " + name);
            }

            return _source.ToString().ReplaceLineEndings("\n");
        }

        /// <summary>
        /// Constructs native anchors before comparing every measured object representation and field.
        /// </summary>
        internal string Generate(string headers)
        {
            _source.AppendLine(NativeProbeHeaders.Wrap(headers));
            _source.AppendLine("#include <stddef.h>\n#include <stdint.h>\n#include <limits.h>\n#include <stdlib.h>\n#include <string.h>\n#include <stdarg.h>");
            NativeBindingTarget.WriteChecks(_source, _graph.Target, "Native record");
            NativeBindingCompilerShims.Write(_source, records.Headers.Symbols.Values);
            WriteTypeNormalization();
            AnchorDeclarations();
            KeyValuePair<int, string>[] anchoredRecords = [.. _anchors.Where(pair => _graph.Declarations[pair.Key].Kind != "enum").OrderBy(static pair => pair.Key)];
            if (anchoredRecords.Length != 0)
            {
                // C requires all generic association types to be mutually incompatible, even unselected associations.
                // Enum compatibility differs between toolchains; enum representation and constants are checked separately.
                string associations = string.Join(", ", anchoredRecords.Select(static pair => pair.Value + " *: 0"));
                Check("_Generic((void *)0, " + associations + ", default: 1)", "distinct record identities");
            }

            for (int index = 0; index < _graph.Types.Count; index++)
            {
                if (_members.IsInternalType(index))
                {
                    continue;
                }

                string alias = TypeName(index);
                string declaration = Declare(index, alias);
                _source.Append("typedef ").Append(declaration).AppendLine(";");
                NativeRecordType type = _graph.Types[index];
                bool incompleteArray = type.Size is null && Canonical(index).Kind == "array";
                Storage(alias, type.Size, type.Alignment, "type " + Number(index), incompleteArray);
                if (type.Kind == "alias")
                {
                    string underlying = alias + "_underlying";
                    string underlyingDeclaration = Declare(type.Element!.Value, underlying, type.Qualifiers);
                    _source.Append("typedef ").Append(underlyingDeclaration).AppendLine(";");
                    Compatible(alias, underlying, "typedef " + type.Name, Canonical(index).Kind == "function");
                }
            }

            for (int index = 0; index < _graph.Types.Count; index++)
            {
                if (_members.IsInternalType(index))
                {
                    continue;
                }

                bool function = Canonical(index).Kind == "function";
                string actual = TypeName(index);
                string expected = TypeName(_graph.Types[index].Canonical);
                if (function)
                {
                    actual = NormalizeFunction(index, actual + "_parameters");
                    expected = NormalizeFunction(_graph.Types[index].Canonical, actual + "_canonical");
                }

                Compatible(actual, expected, "canonical type " + Number(index), function);
            }

            foreach ((string name, NativeHeaderSymbol symbol) in records.Headers.Symbols.OrderBy(static value => value.Key, StringComparer.Ordinal))
            {
                Check("_Generic(&(" + NativeBindingCompilerShims.Reference(symbol) + "), " + TypeName(_graph.Roots[name]) + " *: 1, default: 0)", "root type " + name);
            }

            for (int index = 0; index < _graph.Declarations.Count; index++)
            {
                if (!_members.IsInternalDeclaration(index))
                {
                    Declaration(index);
                }
            }

            Bitfields();
            return _source.ToString().ReplaceLineEndings("\n");
        }

        private void AnchorDeclarations()
        {
            // Prefer real typedefs to compiler-internal tags such as __va_list_tag, which C cannot name directly.
            foreach (NativeRecordType type in _graph.Types.Where(static value => value.Kind == "alias").OrderBy(static value => value.Name, StringComparer.Ordinal))
            {
                NativeBindingCDeclaration.ValidateName(type.Name);
                Anchor(type.Canonical, "*(" + AliasName(type) + " *)0");
            }

            for (int index = 0; index < _graph.Declarations.Count; index++)
            {
                NativeRecordDeclaration declaration = _graph.Declarations[index];
                if (declaration.Name.Length != 0)
                {
                    AddAnchor(index, declaration.Kind + " " + declaration.Name);
                }
            }

            // Collection-only roots need not exist during invocation compilation. Prefer actual header types and their fields.
            AnchorFields();
            foreach ((string name, NativeHeaderSymbol symbol) in records.Headers.Symbols.OrderBy(static value => value.Key, StringComparer.Ordinal))
            {
                if (!symbol.IsFunction)
                {
                    Anchor(_graph.Roots[name], symbol.NativeName);
                }
            }

            AnchorFields();
            int[] missing = [.. Enumerable.Range(0, _graph.Declarations.Count)
                .Where(index => !_members.IsInternalDeclaration(index) && !_anchors.ContainsKey(index))];
            if (missing.Length != 0)
            {
                string descriptions = string.Join(", ", missing
                    .Select(index => Number(index) + ":" + _graph.Declarations[index].Kind + ":" + _graph.Declarations[index].Name +
                        "[" + string.Join("|", _graph.Declarations[index].Fields.Select(static field => field.Name)) + "]"));
                throw new FormatException("Native record verification requires a C type anchor for every exposed declaration: " + descriptions + ".");
            }
        }

        /// <summary>
        /// Resolves embedded anonymous declarations through already established real native parents.
        /// </summary>
        private void AnchorFields()
        {
            bool added;
            do
            {
                int count = _anchors.Count;
                foreach ((int index, string parent) in _anchors.ToArray())
                {
                    foreach (NativeRecordField field in _members.Get(index).Select(static member => member.Field)
                        .Where(static field => field.Name.Length != 0 && field.BitWidth is null))
                    {
                        Anchor(field.Type, "((" + parent + " *)0)->" + field.Name);
                    }
                }

                added = count != _anchors.Count;
            } while (added);
        }

        private void Anchor(int index, string expression)
        {
            var lvalue = new StringBuilder(expression);
            var seen = new HashSet<int>();
            while (seen.Add(index))
            {
                NativeRecordType type = Canonical(index);
                if (type.Declaration is int declaration)
                {
                    if (!_graph.Declarations[declaration].IsComplete)
                    {
                        // Opaque declarations use their native tag in AnchorDeclarations; lvalue conversion requires a complete type.
                        return;
                    }

                    AddAnchor(declaration, "ANKUS_RECORD_UNQUAL(" + lvalue + ")");
                    return;
                }

                if (type.Kind == "pointer")
                {
                    lvalue.Insert(0, "*(").Append(')');
                }
                else if (type.Kind == "array")
                {
                    lvalue.Insert(0, '(').Append(")[0]");
                }
                else
                {
                    return;
                }

                index = type.Element!.Value;
            }
        }

        /// <summary>
        /// Removes top-level qualifiers without enum promotion on MSVC and pre-C23 GCC alike.
        /// </summary>
        private void WriteTypeNormalization()
        {
            // MSVC preserves qualifiers on comma expressions; older GCC lacks __typeof_unqual__.
            _source.AppendLine("""
                #ifndef ANKUS_RECORD_UNQUAL
                #if defined(_MSC_VER) && !defined(__clang__)
                #define ANKUS_RECORD_UNQUAL(expression) __typeof_unqual__(expression)
                #else
                #define ANKUS_RECORD_UNQUAL(expression) __typeof__(((void)0, (expression)))
                #endif
                #endif
                """);
        }

        private void AddAnchor(int declaration, string type)
        {
            string name = "ankus_record_declaration_" + Number(declaration);
            if (_anchors.TryAdd(declaration, name))
            {
                _source.Append("typedef ").Append(type).Append(' ').Append(name).AppendLine(";");
            }
        }

        private void Declaration(int index)
        {
            NativeRecordDeclaration declaration = _graph.Declarations[index];
            string anchor = _anchors[index];
            string description = declaration.Name.Length == 0 ? "declaration " + Number(index) : declaration.Name;
            Storage(anchor, declaration.Size, declaration.Alignment, description);
            foreach (NativeRecordConstant constant in declaration.EnumValues)
            {
                string value = constant.Value.StartsWith('-')
                    ? constant.Value == "-9223372036854775808" ? "(-9223372036854775807LL - 1)" : constant.Value + "LL"
                    : constant.Value + "ULL";
                Check("((long double)(" + constant.Name + ") < 0.0L) == " + (constant.Value.StartsWith('-') ? "1" : "0") +
                    " && (" + constant.Name + ") == " + value, "constant " + constant.Name);
            }

            if (declaration.EnumUnderlying is int underlying)
            {
                string storage = TypeName(underlying);
                Check("sizeof(" + anchor + ") == sizeof(" + storage + ") && _Alignof(" + anchor + ") == _Alignof(" + storage + ")", "enum storage " + description);
                Check("(((long double)(" + anchor + ")-1 < 0.0L) ? 1 : 0) == " + (Signed(Canonical(underlying)) ? "1" : "0"), "enum signedness " + description);
            }

            foreach (NativeBindingRecordMember member in _members.Get(index))
            {
                NativeRecordField field = member.Field;
                if (field.BitWidth is not null)
                {
                    continue;
                }

                string path = "((" + anchor + " *)0)->" + field.Name;
                string name = description + "." + field.Name;
                Check("offsetof(" + anchor + ", " + field.Name + ") == " + Number(field.OffsetBits / 8), "offset " + name);
                Check("_Generic(&(" + path + "), " + NativeHeaderType.Qualifiers(member.ParentQualifiers) + TypeName(field.Type) +
                    " *: 1, default: 0)", "member type " + name);
            }
        }

        private void Storage(string type, long? size, long? alignment, string description, bool incompleteArray = false)
        {
            if (size is long count)
            {
                Check("sizeof(" + type + ") == " + Number(count), "size " + description);
            }

            if (alignment is long boundary)
            {
                if (incompleteArray)
                {
                    // GCC rejects _Alignof on an incomplete array. Completing an unevaluated
                    // extern declaration retains the original typedef's alignment attributes.
                    // Measuring only its element would lose an over-aligned array typedef.
                    string witness = type + "_alignment";
                    _source.AppendLine("#if defined(__GNUC__) && !defined(__clang__)");
                    _source.Append("extern ").Append(type).Append(' ').Append(witness).AppendLine(";");
                    _source.Append("extern __typeof__((*(").Append(type).Append(" *)0)[0]) ").Append(witness).AppendLine("[1];");
                    Check("__alignof__(" + witness + ") == " + Number(boundary), "alignment " + description);
                    _source.AppendLine("#else");
                }

                Check("_Alignof(" + type + ") == " + Number(boundary), "alignment " + description);
                if (incompleteArray)
                {
                    _source.AppendLine("#endif");
                }
            }
        }

        /// <summary>
        /// Requires compatible declarations; function redeclarations preserve C parameter adjustment on MSVC too.
        /// </summary>
        private void Compatible(string actual, string expected, string description, bool function = false)
        {
            if (function)
            {
                string name = "ankus_record_function_contract_" + Number(_functionComparison++);
                _source.Append("extern ").Append(actual).Append(' ').Append(name).AppendLine(";");
                _source.Append("extern ").Append(expected).Append(' ').Append(name).AppendLine(";");
            }
            else
            {
                Check("_Generic((" + actual + " *)0, " + expected + " *: 1, default: 0)", description);
            }
        }

        private void Check(string expression, string description)
            => _source.Append("_Static_assert(").Append(expression).Append(", \"Native record contract changed: ").Append(description).AppendLine("\");");

        private NativeRecordType Canonical(int index) => _graph.Types[_graph.Types[index].Canonical];

        private static string TypeName(int index) => "ankus_record_type_" + Number(index);

        /// <summary>
        /// Names the standard variadic argument type, keeping Clang's private typedef out of other compilers' input.
        /// </summary>
        private static string AliasName(NativeRecordType type) => type.Name == "__builtin_va_list" ? "va_list" : type.Name;

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
