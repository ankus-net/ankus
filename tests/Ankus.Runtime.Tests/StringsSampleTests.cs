using System.Globalization;
using System.Text;
using Ankus.Examples.Strings;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks exact upstream greetings, append output, UTF-8 boundaries and Rust-emitted split results.
/// </summary>
[TestClass]
public sealed class StringsSampleTests
{
    /// <summary>
    /// The static sample returns the upstream literal without accidental whitespace or suffix changes.
    /// </summary>
    [TestMethod]
    public void StringsSampleReturnsUpstreamStaticText()
        => Assert.AreEqual("This is a static string xxx", StringsFunctions.ReturnStatic());

    /// <summary>
    /// Append preserves both inputs and adds the final x for empty and supplementary-scalar cases.
    /// </summary>
    /// <param name="input">The original string.</param>
    /// <param name="extra">The appended string.</param>
    /// <param name="expected">The independently specified result.</param>
    [TestMethod]
    [DataRow("", "", "x")]
    [DataRow("hi", " there", "hi therex")]
    [DataRow("", "😀", "😀x")]
    [DataRow("中文", " € 😀", "中文 € 😀x")]
    public void StringsSampleAppendsExactText(string input, string extra, string expected)
    {
        string original = input;
        Assert.AreEqual(expected, StringsFunctions.Append(input, extra));
        Assert.AreEqual(original, input);
    }

    /// <summary>
    /// Byte offsets select complete one-, two-, three- and four-byte UTF-8 scalars and valid empty slices.
    /// </summary>
    /// <param name="start">The inclusive byte offset.</param>
    /// <param name="end">The exclusive byte offset.</param>
    /// <param name="expected">The exact decoded slice.</param>
    [TestMethod]
    [DataRow(0, 1, "A")]
    [DataRow(1, 3, "é")]
    [DataRow(3, 6, "中")]
    [DataRow(6, 10, "😀")]
    [DataRow(10, 11, "Z")]
    [DataRow(0, 11, "Aé中😀Z")]
    [DataRow(0, 0, "")]
    [DataRow(3, 3, "")]
    [DataRow(11, 11, "")]
    public void StringsSampleSlicesUtf8Bytes(int start, int end, string expected)
    {
        byte[] input = "Aé中😀Z"u8.ToArray();
        byte[] original = [.. input];
        Assert.AreEqual(expected, StringsFunctions.SliceUtf8(input, start, end));
        Assert.AreSequenceEqual(original, input);
    }

    /// <summary>
    /// Neither offset may land inside any multibyte scalar, including an otherwise empty slice.
    /// </summary>
    /// <param name="start">The inclusive byte offset.</param>
    /// <param name="end">The exclusive byte offset.</param>
    /// <param name="parameter">The first invalid boundary's parameter name.</param>
    [TestMethod]
    [DataRow(2, 3, "start")]
    [DataRow(4, 6, "start")]
    [DataRow(5, 6, "start")]
    [DataRow(7, 10, "start")]
    [DataRow(8, 10, "start")]
    [DataRow(9, 10, "start")]
    [DataRow(1, 2, "end")]
    [DataRow(3, 4, "end")]
    [DataRow(3, 5, "end")]
    [DataRow(6, 7, "end")]
    [DataRow(6, 8, "end")]
    [DataRow(6, 9, "end")]
    [DataRow(2, 2, "start")]
    [DataRow(4, 4, "start")]
    [DataRow(7, 7, "start")]
    public void StringsSampleRejectsSplitScalars(int start, int end, string parameter)
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() =>
            StringsFunctions.SliceUtf8("Aé中😀Z"u8, start, end));
        Assert.AreEqual(parameter, error.ParamName);
    }

    /// <summary>
    /// Negative, reversed and out-of-bounds offsets fail without partially decoding the source.
    /// </summary>
    /// <param name="start">The inclusive byte offset.</param>
    /// <param name="end">The exclusive byte offset.</param>
    /// <param name="parameter">The first invalid bounds parameter.</param>
    [TestMethod]
    [DataRow(-1, 0, "start")]
    [DataRow(0, -1, "end")]
    [DataRow(3, 1, "end")]
    [DataRow(12, 12, "start")]
    [DataRow(0, 12, "end")]
    public void StringsSampleRejectsInvalidByteBounds(int start, int end, string parameter)
    {
        ArgumentOutOfRangeException error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            StringsFunctions.SliceUtf8("Aé中😀Z"u8, start, end));
        Assert.AreEqual(parameter, error.ParamName);
    }

    /// <summary>
    /// All three split forms match the independent Rust terminator oracle, including empty pattern and supplementary scalars.
    /// </summary>
    [TestMethod]
    public void StringsSampleSplitFormsMatchRustOracle()
    {
        int checkedCases = 0;
        foreach (string[] fields in File.ReadLines(Path.Join(AppContext.BaseDirectory, "string-semantics-reference.txt")).Select(static line => line.Split(';')))
        {
            if (fields[0] != "S")
            {
                continue;
            }

            string input = Encoding.UTF8.GetString(Convert.FromHexString(fields[1]));
            string pattern = Encoding.UTF8.GetString(Convert.FromHexString(fields[2]));
            int count = int.Parse(fields[3], CultureInfo.InvariantCulture);
            string[] expected = count == 0 ? [] : [.. fields[4].Split(',').Select(static hex =>
                Encoding.UTF8.GetString(Convert.FromHexString(hex)))];
            Assert.HasCount(count, expected);
            Assert.AreSequenceEqual(expected, StringsFunctions.Split(input, pattern));
            Assert.AreSequenceEqual(expected, StringsFunctions.SplitSet(input, pattern));
            (int i, string s)[] table = [.. StringsFunctions.SplitTable(input, pattern)];
            Assert.AreSequenceEqual(expected.Select(static (piece, index) => (index, piece)), table);
            checkedCases++;
        }

        Assert.AreEqual(9, checkedCases);
    }

    /// <summary>
    /// Ordinal pattern matching preserves combining sequences rather than applying linguistic normalization.
    /// </summary>
    [TestMethod]
    public void StringsSampleSplitIsOrdinal()
    {
        Assert.AreSequenceEqual<string>(["é", "e\u0301"], StringsFunctions.Split("é,e\u0301", ","));
        Assert.AreSequenceEqual<string>(["ée"], StringsFunctions.Split("ée\u0301", "\u0301"));
        Assert.AreSequenceEqual<string>(["", "e\u0301"], StringsFunctions.Split("ée\u0301", "é"));
        Assert.AreSequenceEqual<string>(["é,e\u0301"], StringsFunctions.Split("é,e\u0301", "É"));
    }

    /// <summary>
    /// Set and table results can be enumerated again after early disposal without sharing mutable iterator state.
    /// </summary>
    [TestMethod]
    public void StringsSampleSetSupportsEarlyDisposalAndReuse()
    {
        IEnumerable<string> pieces = StringsFunctions.SplitSet(",a,,b,", ",");
        using (IEnumerator<string> first = pieces.GetEnumerator())
        {
            Assert.IsTrue(first.MoveNext());
            Assert.AreEqual(string.Empty, first.Current);
        }

        Assert.AreSequenceEqual<string>(["", "a", "", "b"], pieces);
        Assert.AreSequenceEqual<(int, string)>([(0, ""), (1, "a"), (2, ""), (3, "b")],
            StringsFunctions.SplitTable(",a,,b,", ","));
    }

    /// <summary>
    /// Managed inputs that Rust strings cannot represent fail instead of losing unpaired surrogate values.
    /// </summary>
    [TestMethod]
    public void StringsSampleRejectsInvalidUtf16()
    {
        Assert.ThrowsExactly<EncoderFallbackException>(() => StringsFunctions.Append("\ud800", ""));
        Assert.ThrowsExactly<EncoderFallbackException>(() => StringsFunctions.Append("", "\udc00"));
        Assert.ThrowsExactly<EncoderFallbackException>(() => StringsFunctions.Split("\ud800", ""));
        Assert.ThrowsExactly<EncoderFallbackException>(() => StringsFunctions.Split("valid", "\udc00"));
    }
}
