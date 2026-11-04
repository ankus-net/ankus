namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies typed function value, NULL, default, and raw argument descriptors without PostgreSQL lookup.
/// </summary>
[TestClass]
public sealed class PgFunctionArgumentTests
{
    /// <summary>
    /// Typed NULL, zero, and a default request retain distinct argument states.
    /// </summary>
    [TestMethod]
    public void TypedNullZeroAndDefaultRemainDistinct()
    {
        PgFunctionArgument value = PgFunctionArgument.Create(0);
        PgFunctionArgument nullValue = PgFunctionArgument.Create<int?>(null);
        PgFunctionArgument defaultValue = PgFunctionArgument.Default<int>();
        Assert.AreEqual(23U, value.TypeOid);
        Assert.AreEqual(23U, nullValue.TypeOid);
        Assert.AreEqual(23U, defaultValue.TypeOid);
        Assert.AreEqual(0, value.Parameter.Value);
        Assert.IsNull(nullValue.Parameter.Value);
        Assert.IsNull(defaultValue.Parameter.Value);
        Assert.IsFalse(value.IsDefault);
        Assert.IsFalse(nullValue.IsDefault);
        Assert.IsTrue(defaultValue.IsDefault);
        Assert.AreEqual(20U, PgFunctionArgument.Default<long>().TypeOid);
        Assert.AreEqual(98765U, PgFunctionArgument.Default(98765).TypeOid);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgFunctionArgument.Default(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgFunctionArgument.Create(default));
    }

    /// <summary>
    /// Raw arguments retain their exact type identity and lifetime handle, including NULLs.
    /// </summary>
    /// <param name="isNull">Whether the raw value is SQL NULL.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RawArgumentsRetainCatalogIdentityAndLifetime(bool isNull)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgDatum datum = PgDatum.DangerousCreate(0, 98765, PgMemoryContext.Current, isNull);
        PgFunctionArgument direct = PgFunctionArgument.Create(datum);
        PgFunctionArgument parameter = PgFunctionArgument.Create(SpiParameter.Create(datum));
        Assert.AreSame(datum, direct.Parameter.Value);
        Assert.AreSame(datum, parameter.Parameter.Value);
        Assert.AreEqual(98765U, direct.TypeOid);
        Assert.AreEqual(98765U, parameter.TypeOid);
        Assert.IsFalse(direct.IsDefault);
        Assert.AreEqual(isNull, Assert.IsInstanceOfType<PgDatum>(direct.Parameter.Value).IsNull);
        fixture.Handler = static _ => new NativeMemoryResult { _value = 902 };
        Assert.ThrowsExactly<ObjectDisposedException>(() => SpiType.ToNative(direct.Parameter.Value));
    }

    /// <summary>
    /// Invalid explicit OIDs and pointers fail before entering a native callback.
    /// </summary>
    [TestMethod]
    public void InvalidIdentitiesFailWithoutNativeAccess()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgFunctions.Call<int>(0U));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgFunctions.Call(0U));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgFunctions.DangerousCall<int>(0, 0));
        Assert.ThrowsExactly<ArgumentNullException>(() => PgFunctions.Call<int>("abs", (PgFunctionCallOptions)null!));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgFunctions.Call<int>("abs", PgFunctionArgument.Create(-42)));
    }
}
