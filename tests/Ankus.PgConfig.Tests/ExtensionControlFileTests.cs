namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies PostgreSQL control syntax and lossless deterministic formatting.
/// </summary>
[TestClass]
public sealed class ExtensionControlFileTests
{
    /// <summary>
    /// Parsing preserves decoded values, case, empty values and last-assignment precedence.
    /// </summary>
    [TestMethod]
    public void ParsesAssignmentsWithoutLosingValues()
    {
        IReadOnlyDictionary<string, string> values = ExtensionControlFile.Parse("""
            # Entire line
            comment = 'old'
            comment 'it''s # an = exact value' # Trailing comment
            module_pathname = relative/path-name.so
            empty = ''
            Case = 0xAB
            case = -12ms
            number = +.5E-2
            custom.name = yes
            """);
        Assert.HasCount(7, values);
        Assert.AreEqual("it's # an = exact value", values["comment"]);
        Assert.AreEqual("relative/path-name.so", values["module_pathname"]);
        Assert.AreEqual("", values["empty"]);
        Assert.AreEqual("0xAB", values["Case"]);
        Assert.AreEqual("-12ms", values["case"]);
        Assert.AreEqual("+.5E-2", values["number"]);
        Assert.AreEqual("yes", values["custom.name"]);
        IDictionary<string, string> dictionary = Assert.IsInstanceOfType<IDictionary<string, string>>(values);
        Assert.ThrowsExactly<NotSupportedException>(() => dictionary["comment"] = "changed");
        Assert.AreEqual("it's # an = exact value", values["comment"]);
    }

    /// <summary>
    /// Backslash escapes follow PostgreSQL's byte and quote semantics.
    /// </summary>
    [TestMethod]
    public void DecodesPostgresEscapes()
    {
        IReadOnlyDictionary<string, string> values = ExtensionControlFile.Parse("""comment = '\b\f\n\r\t\\\'\101\12x\1z\501\q'""");
        Assert.AreEqual("\b\f\n\r\t\\'A\nx\u0001zAq", values["comment"]);
    }

    /// <summary>
    /// Empty input, comments and CRLF contain no assignments.
    /// </summary>
    [TestMethod]
    public void EmptyAndCommentInputRemainEmpty()
    {
        Assert.IsEmpty(ExtensionControlFile.Parse(""));
        Assert.IsEmpty(ExtensionControlFile.Parse(" # comment\r\n\t\r\n"));
        Assert.AreEqual("", ExtensionControlFile.Format(new Dictionary<string, string>()));
    }

    /// <summary>
    /// Invalid syntax and values fail without returning a partial dictionary.
    /// </summary>
    /// <param name="text">The invalid input partition.</param>
    [TestMethod]
    [DataRow("value =")]
    [DataRow("value = 'unterminated")]
    [DataRow("value = 'one' 'two'")]
    [DataRow("value = one two")]
    [DataRow("value = 'new\nline'")]
    [DataRow("value = one\rother = two")]
    [DataRow("= value")]
    [DataRow("1name = value")]
    [DataRow("value = 'café'")]
    [DataRow("value = '\0'")]
    [DataRow("value = '\\000'")]
    [DataRow("value = '\\200'")]
    [DataRow("value = '\\400'")]
    [DataRow("include = 'other.control'")]
    [DataRow("include_dir = 'directory'")]
    [DataRow("include_if_exists = 'optional.control'")]
    public void RejectsInvalidControlText(string text)
        => Assert.ThrowsExactly<FormatException>(() => ExtensionControlFile.Parse(text));

    /// <summary>
    /// Formatting emits independently expected escaped text and preserves exact decoded values.
    /// </summary>
    [TestMethod]
    public void FormatsDeterministicEscapedText()
    {
        var values = new Dictionary<string, string>
        {
            ["z"] = "",
            ["comment"] = "it's \\ # =\n\r\t\b\f",
            ["A"] = "42",
        };
        string text = ExtensionControlFile.Format(values);
        Assert.AreEqual("A = '42'\ncomment = 'it''s \\\\ # =\\n\\r\\t\\b\\f'\nz = ''\n", text);
        Assert.AreEqual("it's \\ # =\n\r\t\b\f", ExtensionControlFile.Parse(text)["comment"]);
        Assert.AreEqual("", ExtensionControlFile.Parse(text)["z"]);
    }

    /// <summary>
    /// Formatter validation prevents assignment injection and lossy text conversion.
    /// </summary>
    /// <param name="name">The invalid name partition.</param>
    /// <param name="value">The invalid value partition.</param>
    [TestMethod]
    [DataRow("", "value")]
    [DataRow("x=y", "value")]
    [DataRow("name\nother", "value")]
    [DataRow("include", "value")]
    [DataRow("comment", "café")]
    [DataRow("comment", "\0")]
    public void RejectsInvalidFormatting(string name, string value)
        => Assert.ThrowsExactly<FormatException>(() => ExtensionControlFile.Format(new Dictionary<string, string> { [name] = value }));

    /// <summary>
    /// Null input is rejected at the public boundary.
    /// </summary>
    [TestMethod]
    public void RejectsNullInputs()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ExtensionControlFile.Parse(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => ExtensionControlFile.Format(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => ExtensionControlFile.Format(new Dictionary<string, string> { ["comment"] = null! }));
    }

    /// <summary>
    /// File reading returns decoded values and preserves missing-file errors.
    /// </summary>
    [TestMethod]
    public void ReadsControlFiles()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-control-").FullName;
        try
        {
            string path = Path.Join(directory, "probe.control");
            Assert.ThrowsExactly<FileNotFoundException>(() => ExtensionControlFile.Read(path));
            File.WriteAllText(path, "comment = 'file value'\r\n");
            Assert.AreEqual("file value", ExtensionControlFile.Read(path)["comment"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
