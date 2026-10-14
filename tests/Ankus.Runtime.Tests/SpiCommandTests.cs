using System.Globalization;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies parameterized SQL ownership, declared identities and the standalone backend boundary.
/// </summary>
[TestClass]
public sealed class SpiCommandTests
{
    /// <summary>
    /// Literal text is preserved exactly, including empty SQL and Unicode.
    /// </summary>
    /// <param name="text">The literal SQL.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("SELECT '雪', '$1', $$☃$$; -- literal")]
    public void LiteralSqlPreservesTextWithoutBindings(string text)
    {
        var handler = new SpiSqlInterpolatedStringHandler(text.Length, 0);
        handler.AppendLiteral(text);
        SpiCommand command = Spi.Sql(handler);
        Assert.AreEqual(text, command.CommandText);
        Assert.IsEmpty(command.Parameters);
    }

    /// <summary>
    /// Hostile text remains bound data and never changes SQL structure.
    /// </summary>
    [TestMethod]
    public void InterpolationBindsHostileTextAndPreservesOrderedTypes()
    {
        const string value = "雪'); DROP TABLE important; -- $2";
        SpiCommand command = Spi.Sql($"SELECT {value}, {42}, {true}, {4L}");
        Assert.AreEqual("SELECT ($1), ($2), ($3), ($4)", command.CommandText);
        Assert.HasCount(4, command.Parameters);
        Assert.AreEqual(25U, command.Parameters[0].TypeOid);
        Assert.AreEqual(value, Assert.IsInstanceOfType<string>(command.Parameters[0].Value));
        Assert.AreEqual(23U, command.Parameters[1].TypeOid);
        Assert.AreEqual(42, Assert.IsInstanceOfType<int>(command.Parameters[1].Value));
        Assert.AreEqual(16U, command.Parameters[2].TypeOid);
        Assert.IsTrue(Assert.IsInstanceOfType<bool>(command.Parameters[2].Value));
        Assert.AreEqual(20U, command.Parameters[3].TypeOid);
        Assert.AreEqual(4L, Assert.IsInstanceOfType<long>(command.Parameters[3].Value));
    }

    /// <summary>
    /// Nullable declarations and an untyped NULL retain concrete PostgreSQL identities.
    /// </summary>
    [TestMethod]
    public void NullBindingsRetainDeclaredIdentities()
    {
        int? number = null;
        string? text = null;
        SpiCommand command = Spi.Sql($"SELECT {number}, {text}, {null}, {(int?)7}");
        Assert.AreEqual("SELECT ($1), ($2), ($3), ($4)", command.CommandText);
        Assert.HasCount(4, command.Parameters);
        Assert.AreEqual(23U, command.Parameters[0].TypeOid);
        Assert.AreEqual(25U, command.Parameters[1].TypeOid);
        Assert.AreEqual(25U, command.Parameters[2].TypeOid);
        Assert.IsNull(command.Parameters[0].Value);
        Assert.IsNull(command.Parameters[1].Value);
        Assert.IsNull(command.Parameters[2].Value);
        Assert.AreEqual(23U, command.Parameters[3].TypeOid);
        Assert.AreEqual(7, Assert.IsInstanceOfType<int>(command.Parameters[3].Value));
    }

    /// <summary>
    /// A declared object does not acquire the type identity of its current boxed value.
    /// </summary>
    [TestMethod]
    public void UnsupportedDeclaredTypeIsRejected()
    {
        object value = 42;
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.Sql($"SELECT {value}"));
    }

    /// <summary>
    /// Compiler evaluation happens once in source order.
    /// </summary>
    [TestMethod]
    public void InterpolationsEvaluateOnceInOrder()
    {
        int next = 0;
        SpiCommand command = Spi.Sql($"SELECT {++next}, {++next}, {++next}");
        Assert.AreEqual(3, next);
        Assert.AreEqual("SELECT ($1), ($2), ($3)", command.CommandText);
        Assert.HasCount(3, command.Parameters);
        for (int index = 0; index < command.Parameters.Length; index++)
        {
            Assert.AreEqual(index + 1, Assert.IsInstanceOfType<int>(command.Parameters[index].Value));
        }
    }

    /// <summary>
    /// Parameter numbering stays invariant past nine under a different culture.
    /// </summary>
    [TestMethod]
    public void OrdinalsAreInvariantAndDoNotWrapAfterNine()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            SpiCommand command = Spi.Sql($"SELECT {1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12}");
            Assert.AreEqual("SELECT ($1),($2),($3),($4),($5),($6),($7),($8),($9),($10),($11),($12)", command.CommandText);
            Assert.HasCount(12, command.Parameters);
            for (int index = 0; index < command.Parameters.Length; index++)
            {
                Assert.AreEqual(index + 1, Assert.IsInstanceOfType<int>(command.Parameters[index].Value));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Commands copy the binding vector while retaining the parameter value's existing ownership contract.
    /// </summary>
    [TestMethod]
    public void CommandSnapshotsBuilderWithoutCopyingParameterValues()
    {
        int[] values = [3, 5];
        SpiParameter parameter = SpiParameter.Create(values);
        var handler = new SpiSqlInterpolatedStringHandler(7, 2);
        handler.AppendLiteral("SELECT ");
        handler.AppendFormatted(parameter);
        SpiCommand first = Spi.Sql(handler);
        handler.AppendLiteral(", ");
        handler.AppendFormatted(9);
        SpiCommand second = Spi.Sql(handler);
        Assert.AreEqual("SELECT ($1)", first.CommandText);
        Assert.HasCount(1, first.Parameters);
        Assert.AreEqual(1007U, first.Parameters[0].TypeOid);
        Assert.AreSame(values, first.Parameters[0].Value);
        Assert.AreEqual("SELECT ($1), ($2)", second.CommandText);
        Assert.HasCount(2, second.Parameters);
        Assert.AreSame(values, second.Parameters[0].Value);
        Assert.AreEqual(9, Assert.IsInstanceOfType<int>(second.Parameters[1].Value));
    }

    /// <summary>
    /// Default commands and parameters fail before any backend call.
    /// </summary>
    [TestMethod]
    public void UninitializedCommandsAndParametersAreRejected()
    {
        SpiCommand command = default;
        Assert.ThrowsExactly<InvalidOperationException>(() => command.CommandText);
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute(command));
        SpiParameter parameter = default;
        // codeql[cs/call-to-object-tostring] Spi.Sql binds each interpolated value as a parameter; nothing is formatted as text.
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => Spi.Sql($"SELECT {parameter}"));
        Assert.AreEqual("value", error.ParamName);
    }

    /// <summary>
    /// Every standalone command role retains the active-backend access check.
    /// </summary>
    [TestMethod]
    public void CommandRolesRequireAnActiveBackend()
    {
        SpiCommand command = Spi.Sql($"SELECT {42}");
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute(command));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Query(command));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Query(command, true, 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Select(command, 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.SelectRaw(command, 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.QueryRaw(command, true, 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.ExecuteScalar<int>(command));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.ExecuteScalars<int, int>(command));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.ExecuteScalars<int, int, int>(command));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Explain(command));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.OpenCursor(command));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.OpenCursor(command, true));
    }
}
