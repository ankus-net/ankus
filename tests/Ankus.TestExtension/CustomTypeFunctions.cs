using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises generated base types through storage, collections, SPI, and aggregate callbacks.
/// </summary>
[PgSchema("custom_values")]
public static class CustomTypeFunctions
{
    /// <summary>
    /// Exercises a codec constructor that fails before parsing begins.
    /// </summary>
    [PgType(typeof(FaultCodec), Name = "fault")]
    public readonly record struct Fault;

    /// <summary>
    /// Reports construction failure through the normal PostgreSQL error boundary.
    /// </summary>
    public sealed class FaultCodec : PgTypeCodec<Fault>
    {
        /// <summary>
        /// Fails before any conversion can run.
        /// </summary>
        public FaultCodec() => throw new PgException("P7905", "codec construction failed");

        /// <inheritdoc />
        public override Fault Parse(string text) => throw new InvalidOperationException("Constructor did not fail.");

        /// <inheritdoc />
        public override string Format(Fault value) => throw new InvalidOperationException("Constructor did not fail.");

        /// <inheritdoc />
        public override Fault Read(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("Constructor did not fail.");

        /// <inheritdoc />
        public override void Write(Fault value, IBufferWriter<byte> destination) => throw new InvalidOperationException("Constructor did not fail.");
    }

    /// <summary>
    /// Stores a signed integer with a fixed, portable binary representation.
    /// </summary>
    /// <param name="Value">The exact integer.</param>
    [PgType(typeof(NumberCodec), Name = "number", BinaryProtocol = true, Id = "custom.number")]
    public readonly record struct Number(long Value);

    /// <summary>
    /// Converts text and eight-byte big-endian integer payloads.
    /// </summary>
    public sealed class NumberCodec : PgTypeCodec<Number>
    {
        /// <inheritdoc />
        public override Number Parse(string text) => new(long.Parse(text, CultureInfo.InvariantCulture));

        /// <inheritdoc />
        public override string Format(Number value) => value.Value.ToString(CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public override Number Read(ReadOnlySpan<byte> payload)
        {
            if (payload.Length != sizeof(long))
            {
                throw new PgException("22P03", "Invalid number payload.");
            }

            return new(BinaryPrimitives.ReadInt64BigEndian(payload));
        }

        /// <inheritdoc />
        public override void Write(Number value, IBufferWriter<byte> destination)
        {
            BinaryPrimitives.WriteInt64BigEndian(destination.GetSpan(sizeof(long)), value.Value);
            destination.Advance(sizeof(long));
        }
    }

    /// <summary>
    /// Carries an independently owned string, including large toasted values.
    /// </summary>
    /// <param name="Value">The exact text.</param>
    [PgType(typeof(MessageCodec), Name = "message", BinaryProtocol = true)]
    public sealed record Message(string Value);

    /// <summary>
    /// Stores strict UTF-8 while reserving explicit inputs to exercise each failing callback.
    /// </summary>
    public sealed class MessageCodec : PgTypeCodec<Message>
    {
        private readonly UTF8Encoding _encoding = new(false, true);

        /// <inheritdoc />
        public override Message Parse(string text) => text == "!parse" ? throw new PgException("P7901", "parse failed") : new(text);

        /// <inheritdoc />
        public override string Format(Message value) => value.Value == "!format" ? throw new PgException("P7902", "format failed") : value.Value;

        /// <inheritdoc />
        public override Message Read(ReadOnlySpan<byte> payload)
        {
            string text = _encoding.GetString(payload);
            return text == "!read" ? throw new PgException("P7903", "read failed") : new(text);
        }

        /// <inheritdoc />
        public override void Write(Message value, IBufferWriter<byte> destination)
        {
            if (value.Value == "!write")
            {
                throw new PgException("P7904", "write failed");
            }

            int length = _encoding.GetByteCount(value.Value);
            _encoding.GetBytes(value.Value, destination.GetSpan(length));
            destination.Advance(length);
        }
    }

    /// <summary>
    /// Exchanges nullable value types across each SPI ownership path.
    /// </summary>
    [PgFunction]
    public static Number? CustomNumber(Number? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Exchanges nullable reference types across each SPI ownership path.
    /// </summary>
    [PgFunction]
    public static Message? CustomMessage(Message? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Preserves dimensions, lower bounds, and NULL cells for custom value arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<Number?>? CustomNumbers(PgArray<Number?>? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Preserves reference-type arrays and nullable elements.
    /// </summary>
    [PgFunction]
    public static PgArray<Message?>? CustomMessages(PgArray<Message?>? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Rejects nullable cells and shape loss in required vectors.
    /// </summary>
    [PgFunction]
    public static Number[] CustomVector(Number[] value) => value;

    /// <summary>
    /// Reads custom values from an owned SPI result after another query reuses native buffers.
    /// </summary>
    [PgFunction]
    public static Number? CustomQuery(string sql)
    {
        Number? value = Spi.ExecuteScalar<Number?>(sql);
        Spi.Execute("SELECT repeat('replacement',20000)");
        return value;
    }

    /// <summary>
    /// Reads arrays of domains and enforces their element identity.
    /// </summary>
    [PgFunction]
    public static PgArray<Number?> CustomArrayQuery(string sql) => Spi.ExecuteScalar<PgArray<Number?>>(sql);

    /// <summary>
    /// Streams custom values and NULLs with early-exit cleanup.
    /// </summary>
    [PgFunction]
    public static IEnumerable<Number?> CustomRows(Number? value) => [value, null, new(9)];

    /// <summary>
    /// Materializes custom value and reference fields together.
    /// </summary>
    [PgFunction(SetMode = PgSetMode.Materialize)]
    public static IEnumerable<(Number? Number, Message? Message)> CustomTable(Number? value) => [(value, new("row")), (null, null)];

    /// <summary>
    /// Converts a custom value with a generated SQL cast.
    /// </summary>
    [PgFunction, PgCast]
    public static long CustomLong(Number value) => value.Value;

    /// <summary>
    /// Compares exact values through a generated PostgreSQL operator.
    /// </summary>
    [PgFunction, PgOperator("===")]
    public static bool CustomEqual(Number left, Number right) => left == right;

    /// <summary>
    /// Creates a required reference result whose codec can fail on output.
    /// </summary>
    [PgFunction]
    public static Message CustomMakeMessage(string value) => new(value);

    /// <summary>
    /// Uses the stored custom value as ordinary aggregate state, including partial workers.
    /// </summary>
    [PgAggregate(Name = "custom_sum", ParallelSafety = PgParallelSafety.Safe)]
    public sealed class Sum : IPgAggregate<Number?, Number?>, IPgCombinableAggregate<Number?>
    {
        /// <summary>
        /// Adds each present input to independent group state.
        /// </summary>
        public static Number? Transition(PgAggregateContext context, Number? state, Number? value)
            => value is null ? state : new(checked((state is { } current ? current.Value : 0) + value.Value.Value));

        /// <summary>
        /// Combines ordinary states without internal serialization callbacks.
        /// </summary>
        public static Number? Combine(PgAggregateContext context, Number? left, Number? right) => Transition(context, left, right);
    }
}
