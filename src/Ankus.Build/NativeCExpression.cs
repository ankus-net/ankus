using System.Globalization;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Evaluates object-like macro definitions as the cexpr 0.6 crate does for bindgen: integer, floating-point, character
/// and string literals, previously evaluated macros, parentheses, unary <c>+ - ~</c>, the arithmetic, shift and bitwise
/// operators, and adjacent string concatenation, with 64-bit wrapping arithmetic.
/// </summary>
/// <param name="identifiers">The values of the macros evaluated so far.</param>
internal sealed class NativeCExpression(IReadOnlyDictionary<string, NativeCConstant> identifiers)
{
    /// <summary>
    /// Evaluates a macro definition's tokens, its name followed by its body, which must be consumed completely.
    /// </summary>
    /// <param name="tokens">The definition's tokens, without comments.</param>
    /// <returns>The macro's name and value, or null when cexpr cannot evaluate it.</returns>
    internal (string Name, NativeCConstant Value)? MacroDefinition(IReadOnlyList<NativeCToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0 || tokens[0].Kind != NativeCTokenKind.Identifier)
        {
            return null;
        }

        Result result = Expression(tokens, 1);
        return result.Status == Status.Success && result.Next == tokens.Count
            ? (Encoding.UTF8.GetString(tokens[0].Raw), result.Value!)
            : null;
    }

    private Result Expression(IReadOnlyList<NativeCToken> tokens, int position)
    {
        Result numeric = Or(tokens, position);
        if (numeric.Status != Status.Error)
        {
            return numeric;
        }

        Result parenthesized = Delimited(tokens, position, Expression);
        if (parenthesized.Status != Status.Error)
        {
            return parenthesized;
        }

        Result concatenated = ConcatenatedString(tokens, position);
        if (concatenated.Status != Status.Error)
        {
            return concatenated;
        }

        Result literal = Literal(tokens, position);
        return literal.Status != Status.Error ? literal : Identifier(tokens, position);
    }

    private Result ConcatenatedString(IReadOnlyList<NativeCToken> tokens, int position)
    {
        Result first = String(tokens, position);
        if (first.Status != Status.Success)
        {
            return first;
        }

        var bytes = new List<byte>(first.Value!.Bytes!);
        int next = first.Next;
        while (true)
        {
            Result more = String(tokens, next);
            if (more.Status != Status.Success)
            {
                break;
            }

            bytes.AddRange(more.Value!.Bytes!);
            next = more.Next;
        }

        return Result.Success(next, NativeCConstant.String([.. bytes]));
    }

    private Result String(IReadOnlyList<NativeCToken> tokens, int position)
    {
        Result literal = Literal(tokens, position);
        if (literal.Status == Status.Success)
        {
            return literal.Value!.Kind == NativeCConstantKind.String ? literal : Result.Error;
        }

        if (literal.Status == Status.Incomplete)
        {
            return literal;
        }

        Result identifier = Identifier(tokens, position);
        return identifier.Status == Status.Success && identifier.Value!.Kind != NativeCConstantKind.String ? Result.Error : identifier;
    }

    private Result Or(IReadOnlyList<NativeCToken> tokens, int position)
        => Numeric(Fold(tokens, position, Xor, ["|"], static (left, _, right) => left.BitwiseOr(right)));

    private Result Xor(IReadOnlyList<NativeCToken> tokens, int position)
        => Numeric(Fold(tokens, position, And, ["^"], static (left, _, right) => left.BitwiseXor(right)));

    private Result And(IReadOnlyList<NativeCToken> tokens, int position)
        => Numeric(Fold(tokens, position, Shift, ["&"], static (left, _, right) => left.BitwiseAnd(right)));

    private Result Shift(IReadOnlyList<NativeCToken> tokens, int position)
        => Numeric(Fold(tokens, position, AddSubtract, ["<<", ">>"], static (left, op, right) => left.Shift(op == "<<", right)));

    private Result AddSubtract(IReadOnlyList<NativeCToken> tokens, int position)
        => Fold(tokens, position, MultiplyDivide, ["+", "-"], static (left, op, right) => left.Arithmetic(op[0], right));

    private Result MultiplyDivide(IReadOnlyList<NativeCToken> tokens, int position)
        => Fold(tokens, position, Unary, ["*", "/", "%"], static (left, op, right) => left.Arithmetic(op[0], right));

    /// <summary>
    /// Applies nom's <c>fold_many0</c> over operator and operand pairs: an operand that is not there ends the fold before
    /// its operator, and a missing token after an operator fails the whole expression.
    /// </summary>
    private static Result Fold(IReadOnlyList<NativeCToken> tokens, int position, Func<IReadOnlyList<NativeCToken>, int, Result> operand,
        string[] operators, Func<NativeCConstant, string, NativeCConstant, NativeCConstant> combine)
    {
        Result first = operand(tokens, position);
        if (first.Status != Status.Success)
        {
            return first;
        }

        NativeCConstant accumulated = first.Value!;
        int next = first.Next;
        while (next < tokens.Count && tokens[next].Kind == NativeCTokenKind.Punctuation &&
            operators.Contains(Encoding.UTF8.GetString(tokens[next].Raw)))
        {
            Result right = operand(tokens, next + 1);
            if (right.Status == Status.Error)
            {
                break;
            }

            if (right.Status == Status.Incomplete)
            {
                return right;
            }

            accumulated = combine(accumulated, Encoding.UTF8.GetString(tokens[next].Raw), right.Value!);
            next = right.Next;
        }

        return Result.Success(next, accumulated);
    }

    private Result Unary(IReadOnlyList<NativeCToken> tokens, int position)
    {
        Result parenthesized = Delimited(tokens, position, Or);
        if (parenthesized.Status != Status.Error)
        {
            return parenthesized;
        }

        Result literal = Numeric(Literal(tokens, position));
        if (literal.Status != Status.Error)
        {
            return literal;
        }

        Result identifier = Numeric(Identifier(tokens, position));
        if (identifier.Status != Status.Error)
        {
            return identifier;
        }

        if (position >= tokens.Count)
        {
            return Result.Incomplete;
        }

        string op = Encoding.UTF8.GetString(tokens[position].Raw);
        if (tokens[position].Kind != NativeCTokenKind.Punctuation || op is not ("+" or "-" or "~"))
        {
            return Result.Error;
        }

        Result operand = Unary(tokens, position + 1);
        if (operand.Status != Status.Success)
        {
            return operand;
        }

        NativeCConstant? value = operand.Value!.Unary(op[0]);
        return value is null ? Result.Error : Result.Success(operand.Next, value);
    }

    private static Result Numeric(Result result)
        => result.Status == Status.Success && result.Value!.Kind is not (NativeCConstantKind.Integer or NativeCConstantKind.Float)
            ? Result.Error : result;

    private static Result Delimited(IReadOnlyList<NativeCToken> tokens, int position, Func<IReadOnlyList<NativeCToken>, int, Result> inner)
    {
        if (position >= tokens.Count)
        {
            return Result.Incomplete;
        }

        if (!Punctuation(tokens[position], "("))
        {
            return Result.Error;
        }

        Result body = inner(tokens, position + 1);
        if (body.Status != Status.Success)
        {
            return body;
        }

        if (body.Next >= tokens.Count)
        {
            return Result.Incomplete;
        }

        return Punctuation(tokens[body.Next], ")") ? Result.Success(body.Next + 1, body.Value!) : Result.Error;
    }

    private Result Identifier(IReadOnlyList<NativeCToken> tokens, int position)
    {
        if (position >= tokens.Count)
        {
            return Result.Incomplete;
        }

        return tokens[position].Kind == NativeCTokenKind.Identifier &&
            identifiers.TryGetValue(Encoding.UTF8.GetString(tokens[position].Raw), out NativeCConstant? value)
            ? Result.Success(position + 1, value) : Result.Error;
    }

    private static Result Literal(IReadOnlyList<NativeCToken> tokens, int position)
    {
        if (position >= tokens.Count)
        {
            return Result.Incomplete;
        }

        return tokens[position].Kind == NativeCTokenKind.Literal && NativeCLiteral.Parse(tokens[position].Raw) is NativeCConstant value
            ? Result.Success(position + 1, value) : Result.Error;
    }

    private static bool Punctuation(NativeCToken token, string text)
        => token.Kind == NativeCTokenKind.Punctuation && token.Raw.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(text));

    private enum Status
    {
        Success,
        Error,
        Incomplete,
    }

    private readonly record struct Result(Status Status, int Next, NativeCConstant? Value)
    {
        internal static Result Error => new(Status.Error, 0, null);

        internal static Result Incomplete => new(Status.Incomplete, 0, null);

        internal static Result Success(int next, NativeCConstant value) => new(Status.Success, next, value);
    }
}

/// <summary>
/// Parses one C literal token as cexpr does, which must consume the whole token: a character, then an integer, then a
/// floating-point number, then a string.
/// </summary>
internal static class NativeCLiteral
{
    /// <summary>
    /// Parses a literal token.
    /// </summary>
    /// <param name="raw">The token's bytes.</param>
    /// <returns>The value, or null when cexpr rejects the literal.</returns>
    internal static NativeCConstant? Parse(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (Character(raw) is (int next, NativeCConstant character) && next == raw.Length)
        {
            return character;
        }

        if (Integer(raw) is (int integerEnd, long integer) && integerEnd == raw.Length)
        {
            return NativeCConstant.Integer(integer);
        }

        if (Float(raw) is (int floatEnd, double number) && floatEnd == raw.Length)
        {
            return NativeCConstant.Float(number);
        }

        return String(raw) is (int stringEnd, byte[] bytes) && stringEnd == raw.Length ? NativeCConstant.String(bytes) : null;
    }

    private static int WidthPrefix(byte[] raw, int position)
    {
        if (raw.AsSpan(position).StartsWith("u8"u8))
        {
            return position + 2;
        }

        return position < raw.Length && raw[position] is (byte)'u' or (byte)'U' or (byte)'L' ? position + 1 : position;
    }

    private static (int Next, NativeCConstant Value)? Character(byte[] raw)
    {
        int position = WidthPrefix(raw, 0);
        if (position >= raw.Length || raw[position] != '\'')
        {
            return null;
        }

        position++;
        NativeCConstant value;
        if (Escape(raw, position) is (int escaped, NativeCConstant escapedValue))
        {
            value = escapedValue;
            position = escaped;
        }
        else if (position < raw.Length && raw[position] != '\\')
        {
            value = NativeCConstant.Character(raw[position], raw[position] > 0x7f);
            position++;
        }
        else
        {
            return null;
        }

        return position < raw.Length && raw[position] == '\'' ? (position + 1, value) : null;
    }

    /// <summary>
    /// Reads an escape sequence as a character: a value up to 0x7f is a character and a larger octal or hexadecimal
    /// value a raw one.
    /// </summary>
    private static (int Next, NativeCConstant Value)? Escape(byte[] raw, int position)
    {
        if (position >= raw.Length || raw[position] != '\\' || position + 1 >= raw.Length)
        {
            return null;
        }

        byte next = raw[position + 1];
        switch (next)
        {
            case (byte)'\'' or (byte)'"' or (byte)'?' or (byte)'\\':
                return (position + 2, NativeCConstant.Character(next, false));
            case (byte)'a':
                return (position + 2, NativeCConstant.Character(7, false));
            case (byte)'b':
                return (position + 2, NativeCConstant.Character(8, false));
            case (byte)'f':
                return (position + 2, NativeCConstant.Character(12, false));
            case (byte)'n':
                return (position + 2, NativeCConstant.Character(10, false));
            case (byte)'r':
                return (position + 2, NativeCConstant.Character(13, false));
            case (byte)'t':
                return (position + 2, NativeCConstant.Character(9, false));
            case (byte)'v':
                return (position + 2, NativeCConstant.Character(11, false));
        }

        int end = position + 1;
        if (next is >= (byte)'0' and <= (byte)'7')
        {
            while (end < raw.Length && end < position + 4 && raw[end] is >= (byte)'0' and <= (byte)'7')
            {
                end++;
            }

            return RawEscape(raw, position + 1, end, 8);
        }

        if (next == 'x')
        {
            end = position + 2;
            while (end < raw.Length && Uri.IsHexDigit((char)raw[end]))
            {
                end++;
            }

            return end == position + 2 ? null : RawEscape(raw, position + 2, end, 16);
        }

        if (next is (byte)'u' or (byte)'U')
        {
            int digits = next == 'u' ? 4 : 8;
            end = position + 2 + digits;
            if (end > raw.Length || !raw.AsSpan(position + 2, digits).ToArray().All(static digit => Uri.IsHexDigit((char)digit)) ||
                !uint.TryParse(Encoding.ASCII.GetString(raw, position + 2, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                    out uint code) || code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
            {
                return null;
            }

            return (end, NativeCConstant.Character(code, false));
        }

        return null;
    }

    private static (int Next, NativeCConstant Value)? RawEscape(byte[] raw, int start, int end, int radix)
    {
        ulong value = 0;
        for (int index = start; index < end; index++)
        {
            ulong digit = (ulong)Convert.ToInt32(((char)raw[index]).ToString(), 16);
            if (value > (ulong.MaxValue - digit) / (ulong)radix)
            {
                return null;
            }

            value = value * (ulong)radix + digit;
        }

        return (end, NativeCConstant.Character(value, value > 0x7f));
    }

    private static (int Next, long Value)? Integer(byte[] raw)
    {
        (int End, ulong Value)? digits = null;
        if (raw.AsSpan().StartsWith("0x"u8) || raw.AsSpan().StartsWith("0X"u8))
        {
            digits = Digits(raw, 2, 16);
        }

        if (digits is null && (raw.AsSpan().StartsWith("0b"u8) || raw.AsSpan().StartsWith("0B"u8)))
        {
            digits = Digits(raw, 2, 2);
        }

        if (digits is null && raw.Length > 0 && raw[0] == '0')
        {
            digits = Digits(raw, 1, 8);
        }

        digits ??= Digits(raw, 0, 10);
        if (digits is not (int end, ulong value))
        {
            return null;
        }

        while (end < raw.Length && raw[end] is (byte)'u' or (byte)'U' or (byte)'l' or (byte)'L')
        {
            end++;
        }

        return (end, unchecked((long)value));
    }

    /// <summary>
    /// Reads one or more digits of a radix into an unsigned 64-bit value, failing on overflow as Rust's parser does.
    /// </summary>
    private static (int End, ulong Value)? Digits(byte[] raw, int start, int radix)
    {
        int end = start;
        while (end < raw.Length && Digit(raw[end], radix) >= 0)
        {
            end++;
        }

        if (end == start)
        {
            return null;
        }

        ulong value = 0;
        for (int index = start; index < end; index++)
        {
            ulong digit = (ulong)Digit(raw[index], radix);
            if (value > (ulong.MaxValue - digit) / (ulong)radix)
            {
                return null;
            }

            value = value * (ulong)radix + digit;
        }

        return (end, value);
    }

    private static int Digit(byte value, int radix)
    {
        int digit = value switch
        {
            >= (byte)'0' and <= (byte)'9' => value - '0',
            >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
            _ => -1,
        };
        return digit < radix ? digit : -1;
    }

    /// <summary>
    /// Reads a floating-point literal through cexpr's alternatives, the first of which that matches wins even when it
    /// leaves an exponent unread.
    /// </summary>
    private static (int Next, double Value)? Float(byte[] raw)
    {
        int Decimals(int position)
        {
            while (position < raw.Length && raw[position] is >= (byte)'0' and <= (byte)'9')
            {
                position++;
            }

            return position;
        }

        int Exponent(int position)
        {
            if (position >= raw.Length || raw[position] is not ((byte)'e' or (byte)'E'))
            {
                return -1;
            }

            position++;
            if (position < raw.Length && raw[position] is (byte)'+' or (byte)'-')
            {
                position++;
            }

            int end = Decimals(position);
            return end == position ? -1 : end;
        }

        (int, double)? Finish(int recognized, bool widthRequired)
        {
            int end = recognized;
            if (end < raw.Length && raw[end] is (byte)'f' or (byte)'l' or (byte)'F' or (byte)'L')
            {
                end++;
            }
            else if (widthRequired)
            {
                return null;
            }

            return double.TryParse(Encoding.ASCII.GetString(raw, 0, recognized), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? (end, value) : null;
        }

        int whole = Decimals(0);
        if (whole > 0 && whole < raw.Length && raw[whole] == '.')
        {
            return Finish(Decimals(whole + 1), false);
        }

        if (whole < raw.Length && raw[whole] == '.' && Decimals(whole + 1) > whole + 1)
        {
            return Finish(Decimals(whole + 1), false);
        }

        // The two exponent forms can only follow digits without a dot here, since a dot with digits matched above.
        if (whole > 0)
        {
            int exponent = Exponent(whole);
            return exponent > 0 ? Finish(exponent, false) : Finish(whole, true);
        }

        return null;
    }

    private static (int Next, byte[] Value)? String(byte[] raw)
    {
        int position = WidthPrefix(raw, 0);
        if (position >= raw.Length || raw[position] != '"')
        {
            position = 0;
            if (raw.Length == 0 || raw[0] != '"')
            {
                return null;
            }
        }

        position++;
        var bytes = new List<byte>();
        while (position < raw.Length && raw[position] != '"')
        {
            if (raw[position] == '\\')
            {
                if (Escape(raw, position) is not (int next, NativeCConstant character))
                {
                    return null;
                }

                bytes.AddRange(character.CharacterBytes());
                position = next;
            }
            else
            {
                bytes.Add(raw[position]);
                position++;
            }
        }

        return position < raw.Length ? (position + 1, [.. bytes]) : null;
    }
}

/// <summary>
/// A token of a macro definition, in cexpr's kinds.
/// </summary>
/// <param name="Kind">The token kind.</param>
/// <param name="Raw">The token's spelling.</param>
internal sealed record NativeCToken(NativeCTokenKind Kind, byte[] Raw);

/// <summary>
/// The kinds of macro token cexpr distinguishes.
/// </summary>
internal enum NativeCTokenKind
{
    /// <summary>
    /// Punctuation.
    /// </summary>
    Punctuation,

    /// <summary>
    /// A keyword.
    /// </summary>
    Keyword,

    /// <summary>
    /// An identifier.
    /// </summary>
    Identifier,

    /// <summary>
    /// A literal.
    /// </summary>
    Literal,
}

/// <summary>
/// A value cexpr evaluates: an integer with 64-bit wrapping arithmetic, a floating-point number, a character, a
/// string's bytes, or an invalid combination.
/// </summary>
internal sealed class NativeCConstant
{
    private NativeCConstant(NativeCConstantKind kind)
    {
        Kind = kind;
    }

    /// <summary>
    /// Gets the kind of value.
    /// </summary>
    internal NativeCConstantKind Kind { get; }

    /// <summary>
    /// Gets an integer's value.
    /// </summary>
    internal long IntegerValue { get; private init; }

    /// <summary>
    /// Gets a floating-point number's value.
    /// </summary>
    internal double FloatValue { get; private init; }

    /// <summary>
    /// Gets a character's code point, or its raw value when <see cref="IsRawCharacter"/>.
    /// </summary>
    internal ulong CharacterValue { get; private init; }

    /// <summary>
    /// Gets a value indicating whether a character is a raw value above 0x7f; otherwise it is a code point.
    /// </summary>
    internal bool IsRawCharacter { get; private init; }

    /// <summary>
    /// Gets a string's bytes.
    /// </summary>
    internal byte[]? Bytes { get; private init; }

    internal static NativeCConstant Integer(long value) => new(NativeCConstantKind.Integer) { IntegerValue = value };

    internal static NativeCConstant Float(double value) => new(NativeCConstantKind.Float) { FloatValue = value };

    internal static NativeCConstant Character(ulong value, bool raw) => new(NativeCConstantKind.Character) { CharacterValue = value, IsRawCharacter = raw };

    internal static NativeCConstant String(byte[] bytes) => new(NativeCConstantKind.String) { Bytes = bytes };

    private static NativeCConstant Invalid { get; } = new(NativeCConstantKind.Invalid);

    /// <summary>
    /// Gets a character's bytes in a string: a code point's UTF-8 encoding, or a raw value's low byte.
    /// </summary>
    internal byte[] CharacterBytes()
        => IsRawCharacter ? [unchecked((byte)CharacterValue)] : Encoding.UTF8.GetBytes(char.ConvertFromUtf32((int)CharacterValue));

    internal NativeCConstant? Unary(char op) => (op, Kind) switch
    {
        ('+', _) => this,
        ('-', NativeCConstantKind.Integer) => Integer(unchecked(-IntegerValue)),
        ('-', NativeCConstantKind.Float) => Float(-FloatValue),
        ('~', NativeCConstantKind.Integer) => Integer(~IntegerValue),
        _ => null,
    };

    internal NativeCConstant Arithmetic(char op, NativeCConstant right)
    {
        if (Kind == NativeCConstantKind.Integer && right.Kind == NativeCConstantKind.Integer)
        {
            long a = IntegerValue;
            long b = right.IntegerValue;
            if (op is '/' or '%' && b == 0)
            {
                throw new FormatException("A macro divides by zero, which stops bindgen.");
            }

            return Integer(op switch
            {
                '+' => unchecked(a + b),
                '-' => unchecked(a - b),
                '*' => unchecked(a * b),
                '/' => a == long.MinValue && b == -1 ? long.MinValue : a / b,
                _ => a == long.MinValue && b == -1 ? 0 : a % b,
            });
        }

        if (Kind is NativeCConstantKind.Integer or NativeCConstantKind.Float && right.Kind is NativeCConstantKind.Integer or NativeCConstantKind.Float)
        {
            double a = Kind == NativeCConstantKind.Float ? FloatValue : IntegerValue;
            double b = right.Kind == NativeCConstantKind.Float ? right.FloatValue : right.IntegerValue;
            return Float(op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => a / b,
                _ => a % b,
            });
        }

        return Invalid;
    }

    internal NativeCConstant Shift(bool left, NativeCConstant right)
    {
        if (Kind != NativeCConstantKind.Integer || right.Kind != NativeCConstantKind.Integer)
        {
            return Invalid;
        }

        // Rust's Wrapping shift masks the amount to the integer's width.
        int amount = unchecked((int)(uint)right.IntegerValue) & 63;
        return Integer(left ? IntegerValue << amount : IntegerValue >> amount);
    }

    internal NativeCConstant BitwiseAnd(NativeCConstant right) => Bitwise(right, static (a, b) => a & b);

    internal NativeCConstant BitwiseOr(NativeCConstant right) => Bitwise(right, static (a, b) => a | b);

    internal NativeCConstant BitwiseXor(NativeCConstant right) => Bitwise(right, static (a, b) => a ^ b);

    private NativeCConstant Bitwise(NativeCConstant right, Func<long, long, long> operation)
        => Kind == NativeCConstantKind.Integer && right.Kind == NativeCConstantKind.Integer
            ? Integer(operation(IntegerValue, right.IntegerValue)) : Invalid;
}

/// <summary>
/// The kinds of cexpr value.
/// </summary>
internal enum NativeCConstantKind
{
    /// <summary>
    /// An integer.
    /// </summary>
    Integer,

    /// <summary>
    /// A floating-point number.
    /// </summary>
    Float,

    /// <summary>
    /// A character.
    /// </summary>
    Character,

    /// <summary>
    /// A string.
    /// </summary>
    String,

    /// <summary>
    /// An invalid combination, such as a string in arithmetic.
    /// </summary>
    Invalid,
}
