using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies exact generated binding transport and callback scope before a native body can be selected.
/// </summary>
[TestClass]
public sealed unsafe class NativeBindingContractTests
{
    /// <summary>
    /// Binding bytes and server majors reach the active native validator unchanged, including malformed values it must reject.
    /// </summary>
    /// <param name="identity">The exact input identity, without managed normalization.</param>
    /// <param name="postgresMajor">The complete signed major-version value.</param>
    [TestMethod]
    [DataRow("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", 18)]
    [DataRow("", 0)]
    [DataRow("A\0é🐘Z", int.MinValue)]
    [DataRow("short", int.MaxValue)]
    public void GeneratedCallBindingPreservesContract(string identity, int postgresMajor)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(identity);
        byte[] original = [.. encoded];
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter(29);
        fixture.Handler = request =>
        {
            Assert.AreEqual(NativeMemoryOperation.NativeBinding, request._operation);
            Assert.AreEqual(postgresMajor, request._value);
            Assert.AreEqual((nuint)original.Length, request._length);
            Assert.AreSequenceEqual(original, new ReadOnlySpan<byte>((void*)request._data, original.Length).ToArray());
            Assert.AreEqual(29, NativeMemoryContext.Provider);
            Assert.AreEqual(0, request._pointer);
            return default;
        };
        NativeRawCall.ValidateBinding(encoded, postgresMajor);
        NativeRawCall.ValidateBinding(encoded, postgresMajor);
        Assert.HasCount(2, fixture.Requests);
        Assert.AreSequenceEqual(original, encoded);
        Assert.AreEqual(0, fixture.ErrorReleases);
    }

    /// <summary>
    /// Inactive, masked, ended and worker scopes reject; nested providers are checked independently and restore the parent.
    /// </summary>
    [TestMethod]
    public void GeneratedCallBindingRequiresActiveScope()
    {
        using var fixture = new MemoryContextTestFixture();
        var providers = new List<nint>();
        fixture.Handler = request =>
        {
            Assert.AreEqual(NativeMemoryOperation.NativeBinding, request._operation);
            providers.Add(NativeMemoryContext.Provider);
            return default;
        };
        Assert.ThrowsExactly<InvalidOperationException>(Validate);
        using (MemoryContextTestFixture.Enter())
        {
            Validate();
            nint previous = NativeMemoryContext.Enter(0);
            try
            {
                Assert.ThrowsExactly<InvalidOperationException>(Validate);
            }
            finally
            {
                NativeMemoryContext.Exit(previous);
            }

            DedicatedThread.Run(() => Assert.ThrowsExactly<InvalidOperationException>(Validate));

            using (MemoryContextTestFixture.Enter(29))
            {
                Validate();
            }

            Validate();
        }

        Assert.ThrowsExactly<InvalidOperationException>(Validate);
        Assert.AreSequenceEqual<nint>([17, 29, 17], providers);
        Assert.HasCount(3, fixture.Requests);

        static void Validate() => NativeRawCall.ValidateBinding("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"u8, 18);
    }

    /// <summary>
    /// An owned native contract error releases every diagnostic buffer and a later validation reaches the same capability again.
    /// </summary>
    [TestMethod]
    public void GeneratedCallBindingErrorsReleaseDiagnosticsAndRecover()
    {
        using var fixture = new MemoryContextTestFixture
        {
            Handler = static _ => throw new PgException("0A000", "binding café", "different header graph", "select the matching companion"),
        };
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgException error = Assert.ThrowsExactly<PgException>(() => NativeRawCall.ValidateBinding("wrong"u8, 18));
        Assert.AreEqual("0A000", error.SqlState);
        Assert.AreEqual("binding café", error.Message);
        Assert.AreEqual("different header graph", error.Detail);
        Assert.AreEqual("select the matching companion", error.Hint);
        Assert.AreEqual(3, fixture.ErrorReleases);
        fixture.Handler = null;
        NativeRawCall.ValidateBinding("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"u8, 18);
        Assert.HasCount(2, fixture.Requests);
        Assert.AreEqual(3, fixture.ErrorReleases);
        Assert.AreEqual("different header graph", error.Detail);
    }
}
