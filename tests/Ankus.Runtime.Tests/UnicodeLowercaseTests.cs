using System.Globalization;
using System.Text;
using Ankus.Examples.Strings;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Compares the portable lowercase implementation with facts emitted by the installed Rust standard library.
/// </summary>
[TestClass]
public sealed class UnicodeLowercaseTests
{
    /// <summary>
    /// Retains uncompressed scalar mappings and properties independently of the production intervals.
    /// </summary>
    private static readonly ReferenceFacts s_reference = ReadReference();

    /// <summary>
    /// Every valid Unicode scalar preserves Rust's complete lowercase mapping, including expansion and identity gaps.
    /// </summary>
    [TestMethod]
    public void EveryUnicodeScalarMatchesRustLowercase()
    {
        int checkedScalars = 0;
        for (int value = 0; value <= 0x10ffff; value++)
        {
            if (!Rune.IsValid(value))
            {
                continue;
            }

            string input = new Rune(value).ToString();
            string expected = s_reference.Mappings.GetValueOrDefault(value, input);
            string actual = UnicodeLowercase.Convert(input);
            Assert.AreEqual(expected, actual, $"Scalar U+{value:X}");
            checkedScalars++;
        }

        Assert.AreEqual(1112064, checkedScalars);
    }

    /// <summary>
    /// Every scalar's independent Rust Cased and Case_Ignorable facts determine final sigma on both sides.
    /// </summary>
    [TestMethod]
    public void EveryUnicodeScalarMatchesRustSigmaContext()
    {
        for (int value = 0; value <= 0x10ffff; value++)
        {
            if (!Rune.IsValid(value))
            {
                continue;
            }

            string scalar = new Rune(value).ToString();
            bool cased = s_reference.Cased.Contains(value);
            bool ignorable = s_reference.Ignorable.Contains(value);
            string isolatedPrefix = UnicodeLowercase.Convert(scalar + "Σ");
            Assert.EndsWith(cased && !ignorable ? "ς" : "σ", isolatedPrefix, $"Preceding U+{value:X}");

            string casedPrefix = UnicodeLowercase.Convert("A" + scalar + "Σ");
            Assert.EndsWith(cased || ignorable ? "ς" : "σ", casedPrefix, $"Cased prefix U+{value:X}");

            string suffix = UnicodeLowercase.Convert("AΣ" + scalar);
            Assert.AreEqual(cased && !ignorable ? 'σ' : 'ς', suffix[1], $"Following U+{value:X}");
        }
    }

    /// <summary>
    /// Whole strings match actual Rust outputs for titlecase, supplementary scripts and ignorable context.
    /// </summary>
    [TestMethod]
    public void WholeStringsMatchRustOracle()
    {
        int checkedStrings = 0;
        foreach ((string input, string expected) in s_reference.Strings)
        {
            Assert.AreEqual(expected, UnicodeLowercase.Convert(input), $"UTF-8 {System.Convert.ToHexString(Encoding.UTF8.GetBytes(input))}");
            checkedStrings++;
        }

        Assert.AreEqual(12, checkedStrings);
    }

    /// <summary>
    /// Casing stays independent of the executing thread's culture and platform globalization provider.
    /// </summary>
    /// <param name="cultureName">The thread culture, including Turkish dotted-I rules.</param>
    [TestMethod]
    [DataRow("tr-TR")]
    [DataRow("en-US")]
    [DataRow("el-GR")]
    public void LowercaseIsLocaleIndependent(string cultureName)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            Assert.AreEqual("ii\u0307 ος οσα", UnicodeLowercase.Convert("Iİ ΟΣ ΟΣΑ"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// Invalid UTF-16 cannot silently become replacement characters in the UTF-8 extension boundary.
    /// </summary>
    /// <param name="input">An unpaired high or low surrogate.</param>
    [TestMethod]
    [DataRow("\ud800")]
    [DataRow("\udc00")]
    public void LowercaseRejectsUnpairedSurrogates(string input)
        => Assert.ThrowsExactly<EncoderFallbackException>(() => UnicodeLowercase.Convert(input));

    /// <summary>
    /// Loads actual uncompressed oracle output without consulting the production casing tables.
    /// </summary>
    /// <returns>The complete Rust scalar facts and contextual outputs.</returns>
    private static ReferenceFacts ReadReference()
    {
        string[] lines = File.ReadAllLines(Path.Join(AppContext.BaseDirectory, "string-semantics-reference.txt"));
        Assert.AreEqual("V;17;0;0", lines[0]);
        var mappings = new Dictionary<int, string>();
        var cased = new HashSet<int>();
        var ignorable = new HashSet<int>();
        var strings = new List<(string Input, string Expected)>();
        foreach (string[] fields in lines.Skip(1).Select(static line => line.Split(';')))
        {
            if (fields[0] == "M")
            {
                var mapped = new StringBuilder();
                foreach (string value in fields[2].Split(','))
                {
                    mapped.Append(new Rune(int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                }

                mappings.Add(int.Parse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture), mapped.ToString());
            }
            else if (fields[0] is "C" or "I")
            {
                int first = int.Parse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                int last = int.Parse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                HashSet<int> values = fields[0] == "C" ? cased : ignorable;
                for (int value = first; value <= last; value++)
                {
                    values.Add(value);
                }
            }
            else if (fields[0] == "L")
            {
                strings.Add((Encoding.UTF8.GetString(System.Convert.FromHexString(fields[1])),
                    Encoding.UTF8.GetString(System.Convert.FromHexString(fields[2]))));
            }
        }

        Assert.HasCount(1488, mappings);
        return new ReferenceFacts(mappings, cased, ignorable, strings);
    }

    /// <summary>
    /// Keeps the independently emitted scalar facts separate from the implementation under test.
    /// </summary>
    /// <param name="Mappings">All nonidentity full lowercase mappings.</param>
    /// <param name="Cased">Every scalar with Rust's Cased property.</param>
    /// <param name="Ignorable">Every scalar with Rust's Case_Ignorable property.</param>
    /// <param name="Strings">Actual whole-string lowercase outputs.</param>
    private sealed record ReferenceFacts(Dictionary<int, string> Mappings, HashSet<int> Cased,
        HashSet<int> Ignorable, List<(string Input, string Expected)> Strings);
}
