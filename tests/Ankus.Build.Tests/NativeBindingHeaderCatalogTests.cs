using System.Globalization;
using System.Text;

namespace Ankus.Build.Tests;

/// <summary>
/// Derives type catalogs from C headers with Clang and checks the representations bindgen gives the same declarations
/// under pgrx's configuration.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed class NativeBindingHeaderCatalogTests(TestContext context)
{
    private const string Header = """
        #include <stdarg.h>
        #include <stddef.h>
        #include "outside.h"

        #define PG_WAIT_TEST 0x05000000U
        #define TEST_ANSWER 42
        #define TEST_NEGATIVE (-5)
        #define TEST_WIDE 0x100000000
        #define TEST_CHAR 'r'
        #define TEST_RATIO 0.25
        #define TEST_NAME "ank" "us"
        #define TEST_BOOLOID 16
        #define TEST_SHIFTED (TEST_ANSWER << 2 | 1)
        #define TEST_FUNCTION(value) ((value) + 1)
        #define M_PI 3.14
        #define TEST_FROM_PI M_PI
        #define TEST_ANSWER_AGAIN TEST_ANSWER
        #undef TEST_ANSWER
        #define TEST_ANSWER 43
        static const int test_limit = 7;

        typedef enum NodeTag
        {
            T_Invalid = 0,
            T_Leaf = 7,
        } NodeTag;

        typedef struct Node
        {
            NodeTag type;
        } Node;

        typedef enum WaitEventTest
        {
            WAIT_EVENT_FIRST = PG_WAIT_TEST,
            WAIT_EVENT_SECOND,
        } WaitEventTest;

        typedef struct Opaque Opaque;
        union ForwardUnion;
        extern void use_union(union ForwardUnion *value);

        typedef struct Holder
        {
            struct OnlyInField *field;
            struct Later *later;
            unsigned int kind:8;
            unsigned int on:1;
        } Holder;

        struct Later;

        typedef struct Padded
        {
            int head[3];
            int a;
            int b;
            unsigned int kind:8;
            unsigned int on:1;
            unsigned int off:1;
        } Padded;

        typedef struct Varlena
        {
            int length;
            char data[];
        } Varlena;

        typedef union Copied
        {
            Varlena *pointer;
            long number;
        } Copied;

        typedef union Wrapped
        {
            Varlena inline_value;
            long number;
        } Wrapped;

        typedef struct WithAnonymous
        {
            int tag;
            union
            {
                int integer;
                double real;
            };
            struct
            {
                int x;
                int y;
            } point;
        } WithAnonymous;

        typedef void (*Callback)(int value);
        typedef void (*Reporter)(const char *, int type) __attribute__((noreturn));

        typedef struct Sized
        {
            size_t length;
            Callback callback;
            struct OutsideUsed outside;
        } Sized;

        /** Formats values. */
        extern int format_values(const char *format, va_list arguments);
        extern Opaque *opaque_value(void);
        static inline int doubled(int value) { return value * 2; }
        static inline int counted(int count, ...) { return count; }
        extern void stop(int code) __attribute__((noreturn));
        extern int hidden_function(void) __attribute__((visibility("hidden")));
        extern int counter;
        extern const int limit;
        extern const char *const names[];
        """;

    private const string Outside = """
        enum
        {
            SIGTEST_ALPHA = 3,
            SIGTEST_BETA = 4,
        };

        enum
        {
            OTHER_ALPHA = 1,
        };

        struct OutsideUnused
        {
            int value;
        };

        struct OutsideUsed
        {
            int value;
        };
        """;

    /// <summary>
    /// The catalog keeps the allowlisted closure, bindgen's names and padding, its forward-declaration forms and the
    /// aliases a parameter's written type references, while excluding unreferenced system declarations. The raw
    /// inventory follows bindgen's function and global codegen, including pgrx's C shim link names.
    /// </summary>
    [TestMethod]
    public async Task HeaderCatalogFollowsBindgenRepresentations()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-header-catalog-").FullName;
        try
        {
            string include = Directory.CreateDirectory(Path.Join(directory, "server")).FullName;
            string system = Directory.CreateDirectory(Path.Join(directory, "system")).FullName;
            await File.WriteAllTextAsync(Path.Join(include, "test.h"), Header, context.CancellationToken);
            await File.WriteAllTextAsync(Path.Join(system, "outside.h"), Outside, context.CancellationToken);
            string compiler = await HostCompilers.ClangAsync();
            string library = await NativeBindingRecordCommand.FindLibraryAsync(compiler,
                await NativeBindingHeaderCatalogCommand.ClangMajorAsync(compiler, context.CancellationToken), context.CancellationToken);
            string[] options = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/WX", "/imsvc", include, "/imsvc", system, "/Zs"]
                : ["-std=gnu11", "-Wall", "-Wextra", "-Werror", "-isystem", include, "-isystem", system, "-fsyntax-only"];
            NativeHeaderCatalogs catalogs = await NativeBindingHeaderCatalogCommand.BuildAsync(compiler, options, "#include \"test.h\"\n", 18,
                include, library, directory, context.CancellationToken);
            NativeBindingCatalog catalog = catalogs.Types;

            Assert.AreEqual(0u, catalog.Tags["T_Invalid"]);
            Assert.AreEqual(7u, catalog.Tags["T_Leaf"]);
            AssertFields(catalog, "Node", ("type_", "type", "NodeTag"));
            NativeBindingEnum wait = catalog.Enums["WaitEventTest"];
            // MSVC gives every C enum an int underlying type.
            Assert.AreEqual(OperatingSystem.IsWindows() ? "::core::ffi::c_int" : "::core::ffi::c_uint", wait.Storage);
            Assert.AreEqual("83886080", wait.Values["WAIT_EVENT_FIRST"]);
            Assert.AreEqual("83886081", wait.Values["WAIT_EVENT_SECOND"]);
            _ = Assert.ContainsSingle(catalog.Enums.Values.Where(static value => value.Values.ContainsKey("SIGTEST_ALPHA")));
            Assert.DoesNotContain(static value => value.Values.ContainsKey("OTHER_ALPHA"), catalog.Enums.Values);

            // A file-scope declaration is a forward declaration; a tag only named inside a record gets an address byte.
            AssertFields(catalog, "Opaque");
            AssertFields(catalog, "Later");
            AssertFields(catalog, "OnlyInField", ("_address", "_address", "u8"));
            Assert.IsFalse(catalog.Types["ForwardUnion"].IsUnion);
            AssertFields(catalog, "ForwardUnion");

            AssertFields(catalog, "Holder", ("field", "field", "*mut OnlyInField"), ("later", "later", "*mut Later"),
                ("_bitfield_align_1", "_bitfield_align_1", "[u8; 0]"), ("_bitfield_1", "_bitfield_1", "__BindgenBitfieldUnit<[u8; 2usize]>"),
                ("__bindgen_padding_0", "__bindgen_padding_0", "[u16; 3usize]"));
            AssertFields(catalog, "Padded", ("head", "head", "[::core::ffi::c_int; 3usize]"), ("a", "a", "::core::ffi::c_int"),
                ("b", "b", "::core::ffi::c_int"), ("_bitfield_align_1", "_bitfield_align_1", "[u8; 0]"),
                ("_bitfield_1", "_bitfield_1", "__BindgenBitfieldUnit<[u8; 2usize]>"), ("__bindgen_padding_0", "__bindgen_padding_0", "u16"));
            AssertFields(catalog, "Varlena", ("length", "length", "::core::ffi::c_int"),
                ("data", "data", "__IncompleteArrayField<::core::ffi::c_char>"));
            AssertFields(catalog, "Copied", ("pointer", "pointer", "*mut Varlena"), ("number", "number", "::core::ffi::c_long"));
            AssertFields(catalog, "Wrapped", ("inline_value", "inline_value", "::core::mem::ManuallyDrop<Varlena>"),
                ("number", "number", "::core::mem::ManuallyDrop<::core::ffi::c_long>"));
            AssertFields(catalog, "WithAnonymous", ("tag", "tag", "::core::ffi::c_int"),
                ("__bindgen_anon_1", "__bindgen_anon_1", "WithAnonymous__bindgen_ty_1"), ("point", "point", "WithAnonymous__bindgen_ty_2"));
            Assert.IsTrue(catalog.Types["WithAnonymous__bindgen_ty_1"].IsUnion);
            AssertFields(catalog, "WithAnonymous__bindgen_ty_2", ("x", "x", "::core::ffi::c_int"), ("y", "y", "::core::ffi::c_int"));
            AssertFields(catalog, "Sized", ("length", "length", "usize"), ("callback", "callback", "Callback"),
                ("outside", "outside", "OutsideUsed"));
            Assert.AreEqual("::core::option::Option<unsafe extern \"C-unwind\" fn(value: ::core::ffi::c_int)>", catalog.Aliases["Callback"]);
            Assert.AreEqual("::core::option::Option<unsafe extern \"C-unwind\" fn(arg1: *const ::core::ffi::c_char, type_: ::core::ffi::c_int) -> !>",
                catalog.Aliases["Reporter"]);
            AssertFields(catalog, "OutsideUsed", ("value", "value", "::core::ffi::c_int"));
            Assert.IsFalse(catalog.Types.ContainsKey("OutsideUnused"));
            Assert.IsFalse(catalog.Aliases.ContainsKey("size_t"));

            NativeBindingRawCatalog raw = catalogs.Raw;
            NativeBindingFunction format = raw.Functions["format_values"];
            Assert.AreEqual("format_values", format.NativeSymbol);
            Assert.AreEqual("C-unwind", format.Abi);
            Assert.AreSequenceEqual(["format", "arguments"], [.. format.Parameters.Select(static parameter => parameter.Name)]);
            Assert.AreEqual("*const ::core::ffi::c_char", format.Parameters[0].Representation);
            Assert.AreEqual("::core::ffi::c_int", format.ReturnType);
            Assert.AreSequenceEqual(["#[doc = \" Formats values.\"]"], [.. format.Attributes]);
            Assert.AreEqual("*mut Opaque", raw.Functions["opaque_value"].ReturnType);
            NativeBindingFunction doubled = raw.Functions["doubled"];
            Assert.AreEqual("doubled__pgrx_cshim", doubled.NativeSymbol);
            Assert.AreSequenceEqual(["#[link_name = \"doubled__pgrx_cshim\"]"], [.. doubled.Attributes]);
            Assert.AreEqual("!", raw.Functions["stop"].ReturnType);
            Assert.IsFalse(raw.Functions.ContainsKey("counted"), "Bindgen cannot wrap a variadic static function.");
            Assert.IsFalse(raw.Functions.ContainsKey("hidden_function"));
            NativeBindingGlobal counter = raw.Globals["counter"];
            Assert.AreEqual("::core::ffi::c_int", counter.Representation);
            Assert.IsTrue(counter.IsMutable);
            Assert.IsEmpty(counter.Attributes);
            Assert.IsFalse(raw.Globals["limit"].IsMutable);
            Assert.IsFalse(raw.Globals["names"].IsMutable);
            Assert.AreEqual("[*const ::core::ffi::c_char; 0usize]", raw.Globals["names"].Representation);

            IReadOnlyDictionary<string, NativeBindingConstant> constants = raw.ReferenceConstants;
            Assert.AreEqual(new NativeBindingConstant("u32", "42"), constants["TEST_ANSWER"], "A redefined macro keeps its first value.");
            Assert.AreEqual(new NativeBindingConstant("u32", "42"), constants["TEST_ANSWER_AGAIN"]);
            Assert.AreEqual(new NativeBindingConstant("i32", "-5"), constants["TEST_NEGATIVE"]);
            Assert.AreEqual(new NativeBindingConstant("u64", "4294967296"), constants["TEST_WIDE"]);
            Assert.AreEqual(new NativeBindingConstant("u8", "114u8"), constants["TEST_CHAR"]);
            Assert.AreEqual(new NativeBindingConstant("f64", "0.25"), constants["TEST_RATIO"]);
            Assert.AreEqual(new NativeBindingConstant("&::core::ffi::CStr", "c\"ankus\""), constants["TEST_NAME"]);
            Assert.AreEqual(new NativeBindingConstant("Oid", "Oid(16)"), constants["TEST_BOOLOID"]);
            Assert.AreEqual(new NativeBindingConstant("u32", "169"), constants["TEST_SHIFTED"]);
            Assert.AreEqual(new NativeBindingConstant("::core::ffi::c_int", "7"), constants["test_limit"]);
            Assert.IsFalse(constants.ContainsKey("TEST_FUNCTION"));
            Assert.IsFalse(constants.ContainsKey("M_PI"), "pgrx tells bindgen to ignore M_PI.");
            Assert.IsFalse(constants.ContainsKey("TEST_FROM_PI"), "An ignored macro defines nothing later macros can use.");
            Assert.IsFalse(raw.Globals.ContainsKey("test_limit"));

            // The va_list parameter is adjusted to a pointer in Clang's AST, but bindgen reads the written typedef.
            Assert.AreEqual("__builtin_va_list", catalog.Aliases["va_list"]);
            if (catalog.Aliases["__builtin_va_list"] == "[__va_list_tag; 1usize]")
            {
                AssertFields(catalog, "__va_list_tag", ("gp_offset", "gp_offset", "::core::ffi::c_uint"),
                    ("fp_offset", "fp_offset", "::core::ffi::c_uint"), ("overflow_arg_area", "overflow_arg_area", "*mut ::core::ffi::c_void"),
                    ("reg_save_area", "reg_save_area", "*mut ::core::ffi::c_void"));
            }
        }
        finally
        {
            await NativeBuildDirectory.DeleteAsync(directory);
        }
    }

    /// <summary>
    /// The tracker places bindgen's padding: after a trailing bitfield unit, and before a field aligned beyond what Rust
    /// guarantees, using bindgen's widest blob element.
    /// </summary>
    [TestMethod]
    public void LayoutTrackerFollowsBindgenPadding()
    {
        var bitfields = new LayoutTracker((24, 4), packed: false, union: false, pointerSize: 8);
        Assert.IsNull(bitfields.SawField((12, 4), 0));
        Assert.IsNull(bitfields.SawField((4, 4), 96));
        Assert.IsNull(bitfields.SawField((4, 4), 128));
        bitfields.SawBitfieldUnit((2, 1));
        (long Size, long Alignment) tail = bitfields.PadStruct() ?? throw new AssertFailedException("Expected tail padding.");
        Assert.AreEqual((2L, 2L), tail);
        Assert.AreEqual(new NativeBindingField("__bindgen_padding_0", "__bindgen_padding_0", "u16"), bitfields.PaddingField(tail));

        var aligned = new LayoutTracker((32, 16), packed: false, union: false, pointerSize: 8);
        Assert.IsNull(aligned.SawField((1, 1), 0));
        (long Size, long Alignment) gap = aligned.SawField((16, 16), 128) ?? throw new AssertFailedException("Expected field padding.");
        Assert.AreEqual((15L, 8L), gap);
        Assert.AreEqual("u64", aligned.PaddingField(gap).Representation);
        Assert.IsNull(aligned.PadStruct());

        var union = new LayoutTracker((8, 8), packed: false, union: true, pointerSize: 8);
        Assert.IsNull(union.SawField((1, 1), 0));
        Assert.IsNull(union.SawField((8, 8), 0));
    }

    /// <summary>
    /// Macro evaluation follows cexpr, including the literal forms it rejects and its first-alternative parsing.
    /// </summary>
    /// <param name="body">The macro body after its name.</param>
    /// <param name="expected">The expected value as kind and text, or null when cexpr cannot evaluate it.</param>
    [TestMethod]
    [DataRow("0x1F", "Integer:31")]
    [DataRow("010", "Integer:8")]
    [DataRow("08", "Integer:8")]
    [DataRow("0b101", "Integer:5")]
    [DataRow("10UL", "Integer:10")]
    [DataRow("0xFFFFFFFFFFFFFFFF", "Integer:-1")]
    [DataRow("( KNOWN << 4 ) | 2", "Integer:114")]
    [DataRow("- 1", "Integer:-1")]
    [DataRow("~ 0", "Integer:-1")]
    [DataRow("1 << 65", "Integer:2")]
    [DataRow("7 / 2 * 2", "Integer:6")]
    [DataRow("1.0 / 4", "Float:0.25")]
    [DataRow("1e3", "Float:1000")]
    [DataRow("10f", "Float:10")]
    [DataRow("1.5e3", null)]
    [DataRow("'a'", "Character:97")]
    [DataRow("'\\n'", "Character:10")]
    [DataRow("'\\377'", "Character:255")]
    [DataRow("\"a\\tb\" TEXT", "String:a\tb!")]
    [DataRow("UNKNOWN + 1", null)]
    [DataRow("( 1 + 2 ) 3", null)]
    [DataRow("1 +", null)]
    [DataRow("", null)]
    public void MacroEvaluationFollowsCexpr(string body, string? expected)
    {
        var identifiers = new Dictionary<string, NativeCConstant>(StringComparer.Ordinal)
        {
            ["KNOWN"] = NativeCConstant.Integer(7),
            ["TEXT"] = NativeCConstant.String("!"u8.ToArray()),
        };
        List<NativeCToken> tokens = [new(NativeCTokenKind.Identifier, "NAME"u8.ToArray()), .. Tokenize(body)];
        (string Name, NativeCConstant Value)? result = new NativeCExpression(identifiers).MacroDefinition(tokens);
        if (expected is null)
        {
            Assert.IsNull(result);
            return;
        }

        (string name, NativeCConstant value) = result ?? throw new AssertFailedException($"Expected {expected}.");
        Assert.AreEqual("NAME", name);
        string actual = value.Kind switch
        {
            NativeCConstantKind.Integer => value.IntegerValue.ToString(CultureInfo.InvariantCulture),
            NativeCConstantKind.Float => value.FloatValue.ToString(CultureInfo.InvariantCulture),
            NativeCConstantKind.Character => value.CharacterValue.ToString(CultureInfo.InvariantCulture),
            _ => Encoding.UTF8.GetString(value.Bytes!),
        };
        Assert.AreEqual(expected, value.Kind + ":" + actual);
    }

    /// <summary>
    /// Floating-point constants print as Rust's shortest round-trip display, which never uses an exponent.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="expected">The literal.</param>
    [TestMethod]
    [DataRow(2.0, "2.0")]
    [DataRow(0.1, "0.1")]
    [DataRow(365.25, "365.25")]
    [DataRow(1e21, "1000000000000000000000.0")]
    [DataRow(1.5e-7, "0.00000015")]
    [DataRow(-0.5, "-0.5")]
    public void FloatConstantsPrintAsRustDisplay(double value, string expected)
        => Assert.AreEqual(expected, NativeBindingHeaderCatalog.FloatLiteral(value));

    /// <summary>
    /// Splits a test macro body on spaces into cexpr tokens, classifying each as Clang would.
    /// </summary>
    private static IEnumerable<NativeCToken> Tokenize(string body)
    {
        foreach (string token in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            NativeCTokenKind kind = char.IsDigit(token[0]) || token[0] is '\'' or '"' ? NativeCTokenKind.Literal
                : char.IsLetter(token[0]) || token[0] == '_' ? NativeCTokenKind.Identifier
                : NativeCTokenKind.Punctuation;
            yield return new NativeCToken(kind, Encoding.UTF8.GetBytes(token));
        }
    }

    /// <summary>
    /// A function type is divergent only when <c>noreturn</c> qualifies the function type itself; a <c>noreturn</c>
    /// parameter does not make it divergent.
    /// </summary>
    [TestMethod]
    public void NoReturnOutsideParenthesesMarksDivergence()
    {
        Assert.IsTrue(NativeBindingHeaderReader.Divergent("void (int) __attribute__((noreturn))"));
        Assert.IsFalse(NativeBindingHeaderReader.Divergent("void (void (*)(int) __attribute__((noreturn)))"));
        Assert.IsFalse(NativeBindingHeaderReader.Divergent("void (int)"));
    }

    private static void AssertFields(NativeBindingCatalog catalog, string name, params (string Name, string NativeName, string Representation)[] fields)
    {
        Assert.IsTrue(catalog.Types.TryGetValue(name, out NativeBindingType? type), $"Missing type {name}.");
        Assert.AreSequenceEqual([.. fields.Select(static field => new NativeBindingField(field.Name, field.NativeName, field.Representation))],
            type.Fields, $"Fields of {name}.");
    }
}
