using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies diagnostic transport ownership, fallback formatting, and preservation of optional values.
/// </summary>
[TestClass]
public sealed class NativeDiagnosticTests
{
    /// <summary>
    /// Verifies long Unicode messages are complete while the emergency buffer remains UTF-8 bounded.
    /// </summary>
    [TestMethod]
    public unsafe void FullMessageOutlivesEmergencyBufferCapacity()
    {
        string message = string.Concat(Enumerable.Repeat("é🐘", 2000));
        NativeCallError transport = default;
        try
        {
            NativeError.Write(new PgException("22023", message), &transport);
            PgException error = transport.ToException();
            Assert.AreEqual(message, error.Message);
            Assert.IsFalse(error.DiagnosticsIncomplete);
            ReadOnlySpan<byte> fallback = new(transport.Message, 2048);
            int length = fallback.IndexOf((byte)0);
            Assert.AreEqual(2046, length);
            Assert.AreEqual(string.Concat(Enumerable.Repeat("é🐘", 341)), new UTF8Encoding(false, true).GetString(fallback[..length]));
        }
        finally
        {
            transport.Release();
        }

        transport.Release();
        Assert.IsNull(transport._fields[(int)NativeDiagnosticField.Message].ReadOptionalString());
    }

    /// <summary>
    /// Verifies absent and empty diagnostics remain distinguishable across the managed transport.
    /// </summary>
    /// <param name="detail">The optional detail, including an explicitly empty value.</param>
    [TestMethod]
    [DataRow(default(string))]
    [DataRow("")]
    [DataRow("detail café")]
    public unsafe void OptionalDiagnosticsRetainNullAndEmpty(string? detail)
    {
        NativeCallError transport = default;
        try
        {
            NativeError.Write(new PgException("22023", "message", detail), &transport);
            PgException error = transport.ToException();
            Assert.AreEqual(detail, error.Detail);
            Assert.IsNull(error.Hint);
            Assert.IsNull(error.SchemaName);
            Assert.IsFalse(error.DiagnosticsIncomplete);
        }
        finally
        {
            transport.Release();
        }
    }

    /// <summary>
    /// Verifies invalid secondary diagnostics cannot escape the unmanaged callback or discard the original SQLSTATE/message.
    /// </summary>
    /// <param name="zeroCharacter">Whether to use an embedded zero instead of malformed UTF-16.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public unsafe void InvalidSecondaryDiagnosticUsesPartialFallback(bool zeroCharacter)
    {
        string context = zeroCharacter ? "bad\0context" : "bad\ud800";
        NativeCallError transport = default;
        try
        {
            NativeError.Write(new PgException("22023", "original", "copied first") { Context = context }, &transport);
            PgException error = transport.ToException();
            Assert.AreEqual("22023", error.SqlState);
            Assert.AreEqual("original", error.Message);
            Assert.AreEqual("copied first", error.Detail);
            Assert.IsNull(error.Context);
            Assert.IsTrue(error.DiagnosticsIncomplete);
        }
        finally
        {
            transport.Release();
        }

        transport.Release();
        Assert.IsNull(transport._fields[(int)NativeDiagnosticField.Detail].ReadOptionalString());
    }

    /// <summary>
    /// Verifies an exception whose message getter throws still produces a usable callback error.
    /// </summary>
    [TestMethod]
    public unsafe void BrokenMessageProducesEmergencyDiagnostic()
    {
        NativeCallError transport = default;
        try
        {
            NativeError.Write(new ThrowingMessageException(), &transport);
            PgException error = transport.ToException();
            Assert.AreEqual("38000", error.SqlState);
            Assert.AreEqual("Managed extension function failed.", error.Message);
            Assert.IsTrue(error.DiagnosticsIncomplete);
        }
        finally
        {
            transport.Release();
        }
    }
}
