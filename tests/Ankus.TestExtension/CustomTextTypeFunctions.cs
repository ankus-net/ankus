using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises custom SQL text with generated CBOR storage through Native AOT boundaries.
/// </summary>
[PgSchema("custom_text")]
public static class CustomTextTypeFunctions
{
    /// <summary>
    /// Carries scalar fields independently of its integer-bar-text SQL representation.
    /// </summary>
    /// <param name="Number">The exact number.</param>
    /// <param name="Text">The exact text.</param>
    [PgType(TextCodec = typeof(TextCodec), Name = "value", BinaryProtocol = true)]
    public sealed record TextValue(int Number, string Text);

    /// <summary>
    /// Exposes text conversion counts and deliberate user-code failures.
    /// </summary>
    public sealed class TextCodec : PgTypeTextCodec<TextValue>
    {
        /// <summary>
        /// Counts successful constructions in this backend runtime.
        /// </summary>
        internal static int s_constructions;

        /// <summary>
        /// Counts a codec construction for the counters SQL reads.
        /// </summary>
        private static void CountConstruction() => Interlocked.Increment(ref s_constructions);

        /// <summary>
        /// Counts custom parser calls.
        /// </summary>
        internal static int s_parses;

        /// <summary>
        /// Counts a parse for the counters SQL reads.
        /// </summary>
        private static void CountParse() => Interlocked.Increment(ref s_parses);

        /// <summary>
        /// Counts custom formatter calls.
        /// </summary>
        internal static int s_formats;

        /// <summary>
        /// Counts a format for the counters SQL reads.
        /// </summary>
        private static void CountFormat() => Interlocked.Increment(ref s_formats);

        /// <summary>
        /// Records deferred construction without retaining backend resources.
        /// </summary>
        public TextCodec() => CountConstruction();

        /// <inheritdoc />
        public override TextValue Parse(string text)
        {
            CountParse();
            switch (text)
            {
                case "!pgparse":
                    throw new PgException("P7911", "custom text parse failed");
                case "!managedparse":
                    throw new InvalidOperationException("ordinary text parse failed");
                case "!nullparse":
                    return null!;
            }

            int separator = text.IndexOf('|');
            if (separator < 0 || !int.TryParse(text.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                throw new PgException("22P02", "Expected integer|text.");
            }

            return new(number, text[(separator + 1)..]);
        }

        /// <inheritdoc />
        public override string Format(TextValue value)
        {
            CountFormat();
            return value.Text switch
            {
                "!pgformat" => throw new PgException("P7912", "custom text format failed"),
                "!managedformat" => throw new InvalidOperationException("ordinary text format failed"),
                "!nullformat" => null!,
                _ => value.Number.ToString(CultureInfo.InvariantCulture) + "|" + value.Text,
            };
        }
    }

    /// <summary>
    /// Uses domain text distinct from stored enum labels.
    /// </summary>
    [PgType(TextCodec = typeof(ModeCodec), BinaryProtocol = true)]
    public enum Mode
    {
        /// <summary>
        /// The inactive state.
        /// </summary>
        Off,

        /// <summary>
        /// The ready state.
        /// </summary>
        Ready,
    }

    /// <summary>
    /// Maps on/off text to exact enum values.
    /// </summary>
    public sealed class ModeCodec : PgTypeTextCodec<Mode>
    {
        /// <inheritdoc />
        public override Mode Parse(string text) => text switch
        {
            "off" => Mode.Off,
            "on" => Mode.Ready,
            _ => throw new PgException("22P02", "Expected on or off."),
        };

        /// <inheritdoc />
        public override string Format(Mode value) => value switch
        {
            Mode.Off => "off",
            Mode.Ready => "on",
            _ => throw new InvalidOperationException("Invalid custom text mode."),
        };
    }

    /// <summary>
    /// Uses a closed variant graph with a custom SQL text surface.
    /// </summary>
    [PgType(TextCodec = typeof(MessageCodec), BinaryProtocol = true)]
    [JsonDerivedType(typeof(NumberMessage), 7)]
    [JsonDerivedType(typeof(StringMessage), "text")]
    public abstract record Message;

    /// <summary>
    /// Carries a number variant.
    /// </summary>
    /// <param name="Number">The exact number.</param>
    public sealed record NumberMessage(int Number) : Message;

    /// <summary>
    /// Carries a text variant.
    /// </summary>
    /// <param name="Text">The exact text.</param>
    public sealed record StringMessage(string Text) : Message;

    /// <summary>
    /// Converts domain prefixes without participating in variant storage.
    /// </summary>
    public sealed class MessageCodec : PgTypeTextCodec<Message>
    {
        /// <inheritdoc />
        public override Message Parse(string text) => text.StartsWith("N:", StringComparison.Ordinal)
            ? new NumberMessage(int.Parse(text.AsSpan(2), CultureInfo.InvariantCulture))
            : text.StartsWith("T:", StringComparison.Ordinal)
                ? new StringMessage(text[2..]) : throw new PgException("22P02", "Expected N: or T:.");

        /// <inheritdoc />
        public override string Format(Message value) => value switch
        {
            NumberMessage number => "N:" + number.Number.ToString(CultureInfo.InvariantCulture),
            StringMessage text => "T:" + text.Text,
            _ => throw new InvalidOperationException("Unknown custom text variant."),
        };
    }

    /// <summary>
    /// Contains attributed types whose own text codecs must not affect nested storage or JSON.
    /// </summary>
    /// <param name="Value">The nested object.</param>
    /// <param name="Mode">The nested enum.</param>
    /// <param name="Message">The nested tagged variant.</param>
    [PgType]
    public sealed record Envelope(TextValue? Value, Mode Mode, Message? Message);

    /// <summary>
    /// Exercises a failing text factory while preserving generated storage.
    /// </summary>
    /// <param name="Number">The exact number.</param>
    [PgType(TextCodec = typeof(FaultCodec), BinaryProtocol = true, NullInputErrorMessage = "fault text needs input")]
    public readonly record struct Fault(int Number);

    /// <summary>
    /// Fails once on first text conversion and leaves binary conversion usable.
    /// </summary>
    public sealed class FaultCodec : PgTypeTextCodec<Fault>
    {
        /// <summary>
        /// Counts attempted text-codec construction.
        /// </summary>
        internal static int s_attempts;

        /// <summary>
        /// Counts an attempt for the counters SQL reads.
        /// </summary>
        private static void CountAttempt() => Interlocked.Increment(ref s_attempts);

        /// <summary>
        /// Reports a deferred factory failure.
        /// </summary>
        public FaultCodec()
        {
            CountAttempt();
            throw new PgException("P7910", "custom text factory failed");
        }

        /// <inheritdoc />
        public override Fault Parse(string text) => throw new InvalidOperationException("Fault parser reached.");

        /// <inheritdoc />
        public override string Format(Fault value) => throw new InvalidOperationException("Fault formatter reached.");
    }

    /// <summary>
    /// Configures direct NULL input rejection for the generated JSON path.
    /// </summary>
    /// <param name="Number">The exact number.</param>
    [PgType(NullInputErrorMessage = "default value isn't optional 😀", BinaryProtocol = true)]
    public readonly record struct RequiredDefault(int Number);

    /// <summary>
    /// Preserves an explicitly empty null-input error message.
    /// </summary>
    /// <param name="Number">The exact number.</param>
    [PgType(NullInputErrorMessage = "", BinaryProtocol = true)]
    public readonly record struct RequiredEmpty(int Number);

    /// <summary>
    /// Configures direct NULL input rejection for a full explicit codec.
    /// </summary>
    /// <param name="Number">The exact number.</param>
    [PgType(typeof(RequiredFullCodec), NullInputErrorMessage = "full value is required", BinaryProtocol = true)]
    public readonly record struct RequiredFull(int Number);

    /// <summary>
    /// Supplies explicit storage while preserving text-codec compatibility and NULL configuration.
    /// </summary>
    public sealed class RequiredFullCodec : PgTypeCodec<RequiredFull>
    {
        /// <inheritdoc />
        public override RequiredFull Parse(string text) => new(int.Parse(text, CultureInfo.InvariantCulture));

        /// <inheritdoc />
        public override string Format(RequiredFull value) => value.Number.ToString(CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public override RequiredFull Read(ReadOnlySpan<byte> payload) => payload.Length == sizeof(int)
            ? new(BinaryPrimitives.ReadInt32BigEndian(payload)) : throw new PgException("22P03", "Invalid full payload.");

        /// <inheritdoc />
        public override void Write(RequiredFull value, IBufferWriter<byte> destination)
        {
            BinaryPrimitives.WriteInt32BigEndian(destination.GetSpan(sizeof(int)), value.Number);
            destination.Advance(sizeof(int));
        }
    }

    /// <summary>
    /// Observes text-codec construction and calls without performing a conversion.
    /// </summary>
    [PgFunction]
    public static string TextCounters() => $"{TextCodec.s_constructions}:{TextCodec.s_parses}:{TextCodec.s_formats}:{FaultCodec.s_attempts}";

    /// <summary>
    /// Exchanges nullable custom-text records through native and SPI ownership paths.
    /// </summary>
    [PgFunction]
    public static TextValue? TextValueEcho(TextValue? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Formats a nullable custom-text value, or reports that SQL NULL reached the method, as pgrx's
    /// <c>fn_takes_option</c> does.
    /// </summary>
    [PgFunction]
    public static string TextValueOrNothing(TextValue? value)
        => value is null ? "nothing" : string.Create(CultureInfo.InvariantCulture, $"{value.Number}|{value.Text}");

    /// <summary>
    /// Formats a nullable custom-text enum, or reports that SQL NULL reached the method, as pgrx's
    /// <c>fn_takes_option_enum</c> does.
    /// </summary>
    [PgFunction]
    public static string TextModeOrNothing(Mode? value) => value?.ToString() ?? "nothing";

    /// <summary>
    /// Exchanges enum values through the same typed paths.
    /// </summary>
    [PgFunction]
    public static Mode? TextModeEcho(Mode? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Exchanges concrete tagged values while retaining the declared SQL type.
    /// </summary>
    [PgFunction]
    public static Message? TextMessageEcho(Message? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Observes actual decoded variant types independently of their text formatter.
    /// </summary>
    [PgFunction]
    public static string TextMessageKind(Message value) => value switch
    {
        NumberMessage number => "number:" + number.Number.ToString(CultureInfo.InvariantCulture),
        StringMessage text => "text:" + text.Text,
        _ => throw new InvalidOperationException("Unknown variant reached callback."),
    };

    /// <summary>
    /// Exchanges nullable arrays without losing bounds or NULL elements.
    /// </summary>
    [PgFunction]
    public static PgArray<TextValue?>? TextValuesEcho(PgArray<TextValue?>? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Exchanges nullable enum arrays with exact custom-type identity.
    /// </summary>
    [PgFunction]
    public static PgArray<Mode?>? TextModesEcho(PgArray<Mode?>? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Exchanges nested types through the enclosing structural serializer.
    /// </summary>
    [PgFunction]
    public static Envelope TextEnvelopeEcho(Envelope value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Creates values without invoking custom text input.
    /// </summary>
    [PgFunction]
    public static TextValue TextMakeValue(int number, string text) => new(number, text);

    /// <summary>
    /// Creates generated storage despite an unusable text codec.
    /// </summary>
    [PgFunction]
    public static Fault TextMakeFault(int number) => new(number);

    /// <summary>
    /// Reads fault values through binary storage without invoking the text factory.
    /// </summary>
    [PgFunction]
    public static int TextFaultNumber(Fault value) => value.Number;

    /// <summary>
    /// Proves ordinary SQL NULL bypasses custom input and output conversion.
    /// </summary>
    [PgFunction]
    public static RequiredDefault? TextDefaultEcho(RequiredDefault? value) => value;

    /// <summary>
    /// Proves ordinary SQL NULL bypasses a full explicit codec.
    /// </summary>
    [PgFunction]
    public static RequiredFull? TextFullEcho(RequiredFull? value) => value;

    /// <summary>
    /// Preserves typed NULL for an empty-message input policy.
    /// </summary>
    [PgFunction]
    public static RequiredEmpty? TextEmptyEcho(RequiredEmpty? value) => value;

    /// <summary>
    /// Preserves typed NULL without creating the failing text codec.
    /// </summary>
    [PgFunction]
    public static Fault? TextFaultEcho(Fault? value) => value;

    /// <summary>
    /// Supplies a typed SQL NULL without coercing an unknown SQL literal.
    /// </summary>
    [PgFunction]
    public static RequiredDefault? TextNullRequiredDefault() => null;

    /// <summary>
    /// Supplies a typed SQL NULL for the explicit full codec.
    /// </summary>
    [PgFunction]
    public static RequiredFull? TextNullRequiredFull() => null;

    /// <summary>
    /// Supplies a typed SQL NULL for an empty-message input policy.
    /// </summary>
    [PgFunction]
    public static RequiredEmpty? TextNullRequiredEmpty() => null;

    /// <summary>
    /// Supplies a typed SQL NULL without constructing a text codec.
    /// </summary>
    [PgFunction]
    public static Fault? TextNullFault() => null;

    /// <summary>
    /// Returns custom values and NULL through set-returning conversion.
    /// </summary>
    [PgFunction]
    public static IEnumerable<TextValue?> TextRows(TextValue? value) => [value, null, new(9, "row")];

    /// <summary>
    /// Reassigns custom scalar and array tuple cells before typed SPI transport.
    /// </summary>
    [PgFunction]
    public static PgHeapTuple TextTuple(PgHeapTuple value, int mode)
    {
        PgHeapTuple copy = value.Clone();
        copy.Set(0, copy.Get<TextValue?>(0));
        copy.Set(1, copy.Get<Mode?>(1));
        copy.Set(2, copy.Get<Message?>(2));
        copy.Set(3, copy.Get<PgArray<TextValue?>?>(3));
        return ArrayFunctions.Exchange(copy, mode);
    }
}
