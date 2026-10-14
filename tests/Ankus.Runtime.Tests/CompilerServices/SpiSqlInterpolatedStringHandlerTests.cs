namespace Ankus.Runtime.Tests.CompilerServices;

/// <summary>
/// Verifies interpolation builder failures and explicit type envelopes without native execution.
/// </summary>
[TestClass]
public sealed class SpiSqlInterpolatedStringHandlerTests
{
    /// <summary>
    /// Default handlers reject all builder operations instead of creating incomplete commands.
    /// </summary>
    [TestMethod]
    public void DefaultHandlerRejectsEveryOperation()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Sql(default));
        Assert.ThrowsExactly<InvalidOperationException>(AppendDefaultLiteral);
        Assert.ThrowsExactly<InvalidOperationException>(AppendDefaultValue);
    }

    /// <summary>
    /// Rejected bindings and null literals do not advance SQL placeholders or mutate the command.
    /// </summary>
    [TestMethod]
    public void FailedAppendLeavesBuilderUsable()
    {
        var handler = new SpiSqlInterpolatedStringHandler(7, 1);
        handler.AppendLiteral("SELECT ");
        ArgumentException? bindingError = null;
        try
        {
            handler.AppendFormatted(default(SpiParameter));
        }
        catch (ArgumentException error)
        {
            bindingError = error;
        }

        ArgumentException rejected = Assert.IsInstanceOfType<ArgumentException>(bindingError);
        Assert.AreEqual(typeof(ArgumentException), rejected.GetType());
        Assert.AreEqual("value", rejected.ParamName);
        ArgumentNullException? literalError = null;
        try
        {
            handler.AppendLiteral(null!);
        }
        catch (ArgumentNullException error)
        {
            literalError = error;
        }

        Assert.AreEqual("value", Assert.IsInstanceOfType<ArgumentNullException>(literalError).ParamName);
        handler.AppendFormatted(42);
        SpiCommand command = Spi.Sql(handler);
        Assert.AreEqual("SELECT ($1)", command.CommandText);
        Assert.HasCount(1, command.Parameters);
        Assert.AreEqual(42, Assert.IsInstanceOfType<int>(command.Parameters[0].Value));
    }

    /// <summary>
    /// Explicit composite-domain NULL envelopes keep their descriptor identity.
    /// </summary>
    [TestMethod]
    public void ExplicitCompositeNullPreservesDomainIdentity()
    {
        var descriptor = new PgTupleDescriptor(9200, -1, [], baseTypeOid: 9100);
        SpiParameter parameter = SpiParameter.Create(null, descriptor);
        // codeql[cs/call-to-object-tostring] Spi.Sql binds each interpolated value as a parameter; nothing is formatted as text.
        SpiCommand command = Spi.Sql($"SELECT {parameter}");
        Assert.AreEqual("SELECT ($1)", command.CommandText);
        Assert.HasCount(1, command.Parameters);
        Assert.AreEqual(9200U, command.Parameters[0].TypeOid);
        Assert.IsNull(command.Parameters[0].Value);
    }

    /// <summary>
    /// Raw parameters preserve the exact owner, bits, NULL flag and domain OID.
    /// </summary>
    /// <param name="isNull">Whether the raw value is SQL NULL.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RawInterpolationPreservesDatumEnvelope(bool isNull)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgDatum value = PgDatum.DangerousCreate(nuint.MaxValue, 98765, PgMemoryContext.Current, isNull);
        // codeql[cs/call-to-object-tostring] Spi.Sql binds each interpolated value as a parameter; nothing is formatted as text.
        SpiCommand command = Spi.Sql($"SELECT {value}, {SpiParameter.Create(value)}");
        Assert.AreEqual("SELECT ($1), ($2)", command.CommandText);
        Assert.HasCount(2, command.Parameters);
        foreach (SpiParameter parameter in command.Parameters)
        {
            Assert.AreEqual(98765U, parameter.TypeOid);
            PgDatum actual = Assert.IsInstanceOfType<PgDatum>(parameter.Value);
            Assert.AreSame(value, actual);
            Assert.AreEqual(nuint.MaxValue, actual.DangerousGetBits());
            Assert.AreEqual(isNull, actual.IsNull);
        }
    }

    /// <summary>
    /// Attempts a literal append without capturing a ref struct in a delegate.
    /// </summary>
    private static void AppendDefaultLiteral()
    {
        SpiSqlInterpolatedStringHandler handler = default;
        handler.AppendLiteral("SELECT ");
    }

    /// <summary>
    /// Attempts a value append without capturing a ref struct in a delegate.
    /// </summary>
    private static void AppendDefaultValue()
    {
        SpiSqlInterpolatedStringHandler handler = default;
        handler.AppendFormatted(42);
    }
}
