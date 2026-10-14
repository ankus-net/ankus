using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Ankus.TestExtension;

/// <summary>
/// Supplies independent value semantics for generated comparison and index support functions.
/// </summary>
[PgSchema("derived_ops")]
public static class CustomOperatorFunctions
{
    private static int s_calls;

    /// <summary>
    /// Compares names without case while preserving independently stored metadata.
    /// </summary>
    /// <param name="name">The equality key.</param>
    /// <param name="metadata">Data deliberately excluded from equality.</param>
    [PgType(TextCodec = typeof(KeyText), BinaryProtocol = true)]
    [PgEquality]
    [PgOrdering]
    [PgHashing]
    public sealed class Key(string name, int metadata) : IEquatable<Key>, IComparable<Key>, IPgHashable
    {
        /// <summary>
        /// Gets the original spelling.
        /// </summary>
        public string Name { get; } = name;

        /// <summary>
        /// Gets non-key payload data.
        /// </summary>
        public int Metadata { get; } = metadata;

        /// <inheritdoc />
        bool IEquatable<Key>.Equals(Key? other) => other is not null && StringComparer.OrdinalIgnoreCase.Equals(Name, other.Name);

        /// <inheritdoc />
        int IComparable<Key>.CompareTo(Key? other)
        {
            int compared = StringComparer.OrdinalIgnoreCase.Compare(Name, other?.Name);
            return compared < 0 ? int.MinValue : compared > 0 ? int.MaxValue : 0;
        }

        /// <inheritdoc />
        public int GetPostgresHashCode() => Name.StartsWith("collision-", StringComparison.OrdinalIgnoreCase)
            ? 7 : PgHash.Compute(Name.ToUpperInvariant());

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Key other && ((IEquatable<Key>)this).Equals(other);
        /// <inheritdoc />
        public override int GetHashCode() => GetPostgresHashCode();
        /// <summary>
        /// Compares logical keys.
        /// </summary>
        public static bool operator ==(Key? left, Key? right)
        {
            return EqualityComparer<Key>.Default.Equals(left, right);
        }

        /// <summary>
        /// Compares distinct logical keys.
        /// </summary>
        public static bool operator !=(Key? left, Key? right)
        {
            return !(left == right);
        }

        /// <summary>
        /// Compares ordered keys.
        /// </summary>
        public static bool operator <(Key? left, Key? right)
        {
            return Comparer<Key>.Default.Compare(left, right) < 0;
        }

        /// <summary>
        /// Compares ordered keys.
        /// </summary>
        public static bool operator <=(Key? left, Key? right)
        {
            return Comparer<Key>.Default.Compare(left, right) <= 0;
        }

        /// <summary>
        /// Compares ordered keys.
        /// </summary>
        public static bool operator >(Key? left, Key? right)
        {
            return Comparer<Key>.Default.Compare(left, right) > 0;
        }

        /// <summary>
        /// Compares ordered keys.
        /// </summary>
        public static bool operator >=(Key? left, Key? right)
        {
            return Comparer<Key>.Default.Compare(left, right) >= 0;
        }
    }

    /// <summary>
    /// Stores a readable key and an exact metadata number in custom SQL text.
    /// </summary>
    public sealed class KeyText : PgTypeTextCodec<Key>
    {
        /// <inheritdoc />
        public override Key Parse(string text)
        {
            int separator = text.LastIndexOf('#');
            return separator < 0 ? new(text, 0) : new(text[..separator], int.Parse(text[(separator + 1)..], CultureInfo.InvariantCulture));
        }

        /// <inheritdoc />
        public override string Format(Key value) => value.Name + "#" + value.Metadata.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Sorts nonlowercase strings backwards before normally sorted lowercase strings.
    /// </summary>
    /// <param name="Text">The exact comparison value.</param>
    [PgType]
    [PgEquality]
    [PgOrdering]
    [PgHashing]
    public readonly record struct Ordered(string Text) : IComparable<Ordered>, IPgHashable
    {
        /// <inheritdoc />
        public int CompareTo(Ordered other)
        {
            bool lower = Text.Length != 0 && char.IsLower(Text[0]);
            bool otherLower = other.Text.Length != 0 && char.IsLower(other.Text[0]);
            return lower != otherLower ? lower ? 1 : -1 : lower
                ? StringComparer.Ordinal.Compare(Text, other.Text) : StringComparer.Ordinal.Compare(other.Text, Text);
        }

        /// <inheritdoc />
        public int GetPostgresHashCode() => PgHash.Compute(Text);

        /// <summary>
        /// Compares the declared custom order.
        /// </summary>
        public static bool operator <(Ordered left, Ordered right)
        {
            return left.CompareTo(right) < 0;
        }

        /// <summary>
        /// Compares the declared custom order.
        /// </summary>
        public static bool operator <=(Ordered left, Ordered right)
        {
            return left.CompareTo(right) <= 0;
        }

        /// <summary>
        /// Compares the declared custom order.
        /// </summary>
        public static bool operator >(Ordered left, Ordered right)
        {
            return left.CompareTo(right) > 0;
        }

        /// <summary>
        /// Compares the declared custom order.
        /// </summary>
        public static bool operator >=(Ordered left, Ordered right)
        {
            return left.CompareTo(right) >= 0;
        }
    }

    /// <summary>
    /// Creates a default-serialized ordered value without coupling SQL tests to JSON spelling.
    /// </summary>
    [PgFunction]
    public static Ordered MakeOrdered(string value) => new(value);

    /// <summary>
    /// Exposes the original text after SQL sorting.
    /// </summary>
    [PgFunction]
    public static string OrderedText(Ordered value) => value.Text;

    /// <summary>
    /// Uses native packed bytes while excluding the salt from logical equality.
    /// </summary>
    [PgType(NativeLayout = true, TextCodec = typeof(PackedText), BinaryProtocol = true)]
    [PgEquality]
    [PgOrdering]
    [PgHashing]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Packed : IEquatable<Packed>, IComparable<Packed>, IPgHashable
    {
        /// <summary>
        /// The signed equality key.
        /// </summary>
        public int Number;

        /// <summary>
        /// The preserved non-key byte.
        /// </summary>
        public byte Salt;

        /// <inheritdoc />
        public readonly bool Equals(Packed other) => Number == other.Number;

        /// <inheritdoc />
        public readonly int CompareTo(Packed other) => Number.CompareTo(other.Number);

        /// <inheritdoc />
        public readonly int GetPostgresHashCode() => PgHash.Compute(unchecked((ulong)Number));

        /// <inheritdoc />
        public override readonly bool Equals(object? obj) => obj is Packed other && Equals(other);
        /// <inheritdoc />
        public override readonly int GetHashCode() => GetPostgresHashCode();
        /// <summary>
        /// Compares keys independently of salt bytes.
        /// </summary>
        public static bool operator ==(Packed left, Packed right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Compares unequal keys.
        /// </summary>
        public static bool operator !=(Packed left, Packed right)
        {
            return !left.Equals(right);
        }

        /// <summary>
        /// Compares numeric keys.
        /// </summary>
        public static bool operator <(Packed left, Packed right)
        {
            return left.CompareTo(right) < 0;
        }

        /// <summary>
        /// Compares numeric keys.
        /// </summary>
        public static bool operator <=(Packed left, Packed right)
        {
            return left.CompareTo(right) <= 0;
        }

        /// <summary>
        /// Compares numeric keys.
        /// </summary>
        public static bool operator >(Packed left, Packed right)
        {
            return left.CompareTo(right) > 0;
        }

        /// <summary>
        /// Compares numeric keys.
        /// </summary>
        public static bool operator >=(Packed left, Packed right)
        {
            return left.CompareTo(right) >= 0;
        }
    }

    /// <summary>
    /// Reads the packed numeric key and its independent salt.
    /// </summary>
    public sealed class PackedText : PgTypeTextCodec<Packed>
    {
        /// <inheritdoc />
        public override Packed Parse(string text)
        {
            string[] parts = text.Split(':');
            return new()
            {
                Number = int.Parse(parts[0], CultureInfo.InvariantCulture),
                Salt = byte.Parse(parts[1], CultureInfo.InvariantCulture)
            };
        }

        /// <inheritdoc />
        public override string Format(Packed value) => string.Create(CultureInfo.InvariantCulture, $"{value.Number}:{value.Salt}");
    }

    /// <summary>
    /// Exercises generated callbacks without consuming or changing a wrapper operand.
    /// </summary>
    [PgFunction]
    public static unsafe string PackedAlias(PgVarlena<Packed> value)
    {
        nuint pointer = (nuint)value.DangerousGetPointer();
        Packed before = value.Value;
        bool equal = Spi.ExecuteScalar<bool>("SELECT $1 OPERATOR(derived_ops.=) '7:99'::derived_ops.packed", SpiParameter.Create(value));
        int hash = Spi.ExecuteScalar<int>("SELECT derived_ops.packed_hash($1)", SpiParameter.Create(value));
        Packed after = value.Value;
        return $"{equal}|{hash == before.GetPostgresHashCode()}|{pointer == (nuint)value.DangerousGetPointer()}|{after.Number}|{after.Salt}";
    }

    /// <summary>
    /// Uses a separately implemented full storage codec with derived operators.
    /// </summary>
    /// <param name="Number">The exact signed value.</param>
    [PgType(typeof(FullCodec), BinaryProtocol = true)]
    [PgEquality]
    [PgOrdering]
    [PgHashing]
    public readonly record struct Full(long Number) : IComparable<Full>, IPgHashable
    {
        /// <inheritdoc />
        public int CompareTo(Full other) => Number.CompareTo(other.Number);

        /// <inheritdoc />
        public int GetPostgresHashCode() => PgHash.Compute(unchecked((ulong)Number));

        /// <summary>
        /// Compares exact signed values.
        /// </summary>
        public static bool operator <(Full left, Full right)
        {
            return left.CompareTo(right) < 0;
        }

        /// <summary>
        /// Compares exact signed values.
        /// </summary>
        public static bool operator <=(Full left, Full right)
        {
            return left.CompareTo(right) <= 0;
        }

        /// <summary>
        /// Compares exact signed values.
        /// </summary>
        public static bool operator >(Full left, Full right)
        {
            return left.CompareTo(right) > 0;
        }

        /// <summary>
        /// Compares exact signed values.
        /// </summary>
        public static bool operator >=(Full left, Full right)
        {
            return left.CompareTo(right) >= 0;
        }
    }

    /// <summary>
    /// Encodes the full-codec value independently as a big-endian integer.
    /// </summary>
    public sealed class FullCodec : PgTypeCodec<Full>
    {
        /// <inheritdoc />
        public override Full Parse(string text) => new(long.Parse(text, CultureInfo.InvariantCulture));

        /// <inheritdoc />
        public override string Format(Full value) => value.Number.ToString(CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public override Full Read(ReadOnlySpan<byte> payload) => payload.Length == 8
            ? new(BinaryPrimitives.ReadInt64BigEndian(payload)) : throw new PgException("22P03", "Invalid full operator payload.");

        /// <inheritdoc />
        public override void Write(Full value, IBufferWriter<byte> destination)
        {
            BinaryPrimitives.WriteInt64BigEndian(destination.GetSpan(8), value.Number);
            destination.Advance(8);
        }
    }

    /// <summary>
    /// Shares one key contract across explicitly tagged concrete variants.
    /// </summary>
    /// <param name="key">The logical comparison key.</param>
    [PgType(BinaryProtocol = true)]
    [PgEquality]
    [PgOrdering]
    [PgHashing]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(TextTag), "text")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(CountTag), 2)]
    public abstract class Tagged(int key) : IEquatable<Tagged>, IComparable<Tagged>, IPgHashable
    {
        /// <summary>
        /// Gets the common comparison key.
        /// </summary>
        public int Key { get; } = key;

        /// <inheritdoc />
        public bool Equals(Tagged? other) => other?.Key == Key;
        /// <inheritdoc />
        public int CompareTo(Tagged? other) => other is null ? 1 : Key.CompareTo(other.Key);
        /// <inheritdoc />
        public int GetPostgresHashCode() => PgHash.Compute(unchecked((ulong)Key));
        /// <inheritdoc />
        // codeql[cs/equals-uses-is] Variants compare by their shared key on purpose, and no variant can override this.
        public sealed override bool Equals(object? obj) => obj is Tagged other && Equals(other);
        /// <inheritdoc />
        public sealed override int GetHashCode() => GetPostgresHashCode();
        /// <summary>
        /// Compares keys across variant kinds.
        /// </summary>
        public static bool operator ==(Tagged? left, Tagged? right)
        {
            return EqualityComparer<Tagged>.Default.Equals(left, right);
        }

        /// <summary>
        /// Compares distinct variant keys.
        /// </summary>
        public static bool operator !=(Tagged? left, Tagged? right)
        {
            return !(left == right);
        }

        /// <summary>
        /// Orders variant keys.
        /// </summary>
        public static bool operator <(Tagged? left, Tagged? right)
        {
            return Comparer<Tagged>.Default.Compare(left, right) < 0;
        }

        /// <summary>
        /// Orders variant keys.
        /// </summary>
        public static bool operator <=(Tagged? left, Tagged? right)
        {
            return Comparer<Tagged>.Default.Compare(left, right) <= 0;
        }

        /// <summary>
        /// Orders variant keys.
        /// </summary>
        public static bool operator >(Tagged? left, Tagged? right)
        {
            return Comparer<Tagged>.Default.Compare(left, right) > 0;
        }

        /// <summary>
        /// Orders variant keys.
        /// </summary>
        public static bool operator >=(Tagged? left, Tagged? right)
        {
            return Comparer<Tagged>.Default.Compare(left, right) >= 0;
        }
    }

    /// <summary>
    /// Preserves text state excluded from the inherited equality key.
    /// </summary>
    /// <param name="key">The inherited key.</param>
    /// <param name="note">The independent text.</param>
    public sealed class TextTag(int key, string note) : Tagged(key)
    {
        /// <summary>
        /// Gets the retained text.
        /// </summary>
        public string Note { get; } = note;
    }

    /// <summary>
    /// Preserves integer state excluded from the inherited equality key.
    /// </summary>
    /// <param name="key">The inherited key.</param>
    /// <param name="count">The independent integer.</param>
    public sealed class CountTag(int key, int count) : Tagged(key)
    {
        /// <summary>
        /// Gets the retained integer.
        /// </summary>
        public int Count { get; } = count;
    }

    /// <summary>
    /// Observes exact concrete identity after generated operator decoding.
    /// </summary>
    [PgFunction]
    public static string TaggedDescribe(Tagged value) => value switch
    {
        TextTag text => string.Create(CultureInfo.InvariantCulture, $"text:{text.Key}:{text.Note}"),
        CountTag count => string.Create(CultureInfo.InvariantCulture, $"count:{count.Key}:{count.Count}"),
        _ => throw new InvalidOperationException("Unknown tagged operator variant."),
    };

    /// <summary>
    /// Opts a native PostgreSQL enum into numeric CLR ordering.
    /// </summary>
    [PgEnum]
    [PgEquality]
    [PgOrdering]
    [PgHashing]
    public enum Priority : long
    {
        /// <summary>
        /// Declared first, numerically last.
        /// </summary>
        First = 30,

        /// <summary>
        /// Declared second, numerically first.
        /// </summary>
        Second = -7,

        /// <summary>
        /// Declared last, numerically between the others.
        /// </summary>
        Last = 5,
    }

    /// <summary>
    /// Retains the existing PostgreSQL declaration-order contract without operator opt-in.
    /// </summary>
    [PgEnum]
    public enum DeclaredPriority
    {
        /// <summary>
        /// The first declared label.
        /// </summary>
        First = 30,

        /// <summary>
        /// The second declared label.
        /// </summary>
        Second = -7,

        /// <summary>
        /// The last declared label.
        /// </summary>
        Last = 5,
    }

    /// <summary>
    /// Keeps a custom-base enum distinct from PostgreSQL native enum conversion.
    /// </summary>
    [PgType]
    [PgEquality]
    [PgOrdering]
    [PgHashing]
    public enum BasePriority : ulong
    {
        /// <summary>
        /// Exercises unsigned values beyond the signed range.
        /// </summary>
        High = ulong.MaxValue,

        /// <summary>
        /// The zero value remains present.
        /// </summary>
        Low = 0,

        /// <summary>
        /// An interior value.
        /// </summary>
        Middle = 7,
    }

    /// <summary>
    /// Supplies distinguishable managed errors for equality, sorting and hashing.
    /// </summary>
    /// <param name="number">The value and error selector.</param>
    [PgType(TextCodec = typeof(FaultText))]
    [PgEquality]
    [PgOrdering]
    [PgHashing]
    public sealed class Fault(int number) : IEquatable<Fault>, IComparable<Fault>, IPgHashable
    {
        /// <summary>
        /// Gets the exact input number.
        /// </summary>
        public int Number { get; } = number;

        /// <inheritdoc />
        public bool Equals(Fault? other)
        {
            s_calls++;
            if (Number == -1 || other?.Number == -1)
            {
                throw new PgException("P8101", "derived equality failed");
            }

            Ordinary(other);
            return other?.Number == Number;
        }

        /// <inheritdoc />
        public int CompareTo(Fault? other)
        {
            s_calls++;
            if (Number == -2 || other?.Number == -2)
            {
                throw new PgException("P8102", "derived ordering failed");
            }

            Ordinary(other);
            return other is null ? 1 : Number.CompareTo(other.Number);
        }

        /// <inheritdoc />
        public int GetPostgresHashCode()
        {
            s_calls++;
            if (Number == -3)
            {
                throw new PgException("P8103", "derived hashing failed");
            }

            Ordinary(null);
            return PgHash.Compute(unchecked((ulong)Number));
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Fault other && Equals(other);
        /// <inheritdoc />
        public override int GetHashCode() => GetPostgresHashCode();
        /// <summary>
        /// Compares nullable fault values.
        /// </summary>
        public static bool operator ==(Fault? left, Fault? right)
        {
            return EqualityComparer<Fault>.Default.Equals(left, right);
        }

        /// <summary>
        /// Compares nullable fault values.
        /// </summary>
        public static bool operator !=(Fault? left, Fault? right)
        {
            return !(left == right);
        }

        /// <summary>
        /// Compares fault values through their declared contract.
        /// </summary>
        public static bool operator <(Fault? left, Fault? right)
        {
            return Comparer<Fault>.Default.Compare(left, right) < 0;
        }

        /// <summary>
        /// Compares fault values through their declared contract.
        /// </summary>
        public static bool operator <=(Fault? left, Fault? right)
        {
            return Comparer<Fault>.Default.Compare(left, right) <= 0;
        }

        /// <summary>
        /// Compares fault values through their declared contract.
        /// </summary>
        public static bool operator >(Fault? left, Fault? right)
        {
            return Comparer<Fault>.Default.Compare(left, right) > 0;
        }

        /// <summary>
        /// Compares fault values through their declared contract.
        /// </summary>
        public static bool operator >=(Fault? left, Fault? right)
        {
            return Comparer<Fault>.Default.Compare(left, right) >= 0;
        }

        /// <summary>
        /// Preserves an ordinary managed error independently of custom PostgreSQL diagnostics.
        /// </summary>
        private void Ordinary(Fault? other)
        {
            if (Number == -4 || other?.Number == -4)
            {
                throw new InvalidOperationException("ordinary derived failure");
            }
        }
    }

    /// <summary>
    /// Leaves error selection in the operator rather than input conversion.
    /// </summary>
    public sealed class FaultText : PgTypeTextCodec<Fault>
    {
        /// <inheritdoc />
        public override Fault Parse(string text) => new(int.Parse(text, CultureInfo.InvariantCulture));

        /// <inheritdoc />
        public override string Format(Fault value) => value.Number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reports and optionally clears the operator invocation counter.
    /// </summary>
    [PgFunction]
    public static int OperatorCalls(bool reset)
    {
        int calls = s_calls;
        if (reset)
        {
            s_calls = 0;
        }

        return calls;
    }

    /// <summary>
    /// Runs failing derived support inside guarded SPI, then proves the next operation remains usable.
    /// </summary>
    [PgFunction]
    public static string OperatorGuarded(string sql)
    {
        string state;
        try
        {
            Spi.Execute(sql);
            state = "no error";
        }
        catch (PgException error)
        {
            state = error.SqlState;
        }

        return state + "|" + Spi.ExecuteScalar<bool>("SELECT 'ok#1'::derived_ops.key OPERATOR(derived_ops.=) 'OK#9'::derived_ops.key");
    }
}
