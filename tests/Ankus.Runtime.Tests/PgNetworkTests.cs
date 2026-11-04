using System.Net;
using System.Net.Sockets;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies detached network invariants, exact .NET conversions, and binary transport validation.
/// </summary>
[TestClass]
public sealed class PgNetworkTests
{
    /// <summary>
    /// Prefix boundaries and non-byte-aligned masks preserve the host while deriving the correct subnet.
    /// </summary>
    [TestMethod]
    [DataRow("192.0.2.129", 0, "0.0.0.0/0", "255.255.255.255/0")]
    [DataRow("192.0.2.129", 25, "192.0.2.128/25", "192.0.2.255/25")]
    [DataRow("192.0.2.129", 32, "192.0.2.129/32", "192.0.2.129")]
    [DataRow("2001:db8::8001", 113, "2001:db8::8000/113", "2001:db8::ffff/113")]
    [DataRow("2001:db8::1", 0, "::/0", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff/0")]
    [DataRow("2001:db8::1", 128, "2001:db8::1/128", "2001:db8::1")]
    public void NetworkMasksPreservePrefixAndFamily(string text, int prefix, string network, string broadcast)
    {
        var value = new PgInet(IPAddress.Parse(text), prefix);
        Assert.AreEqual(network, value.Network.ToString());
        Assert.AreEqual(broadcast, value.Broadcast.ToString());
        Assert.AreEqual(IPAddress.Parse(text), value.Address);
        Assert.AreEqual(prefix, value.PrefixLength);
        Assert.AreEqual(value.Network, new PgCidr(value.Network.ToIPNetwork()));
    }

    /// <summary>
    /// Construction and returned address objects cannot mutate the owned value; mapped IPv6 remains distinct.
    /// </summary>
    [TestMethod]
    public void AddressesAreCopiedAndFamilyIsPreserved()
    {
        IPAddress original = IPAddress.Parse("::ffff:192.0.2.1");
        var value = new PgInet(original);
        original.ScopeId = 7;
        value.Address.ScopeId = 9;
        Assert.AreEqual(AddressFamily.InterNetworkV6, value.AddressFamily);
        Assert.AreEqual(128, value.PrefixLength);
        Assert.AreEqual(0L, value.ToIPAddress().ScopeId);
        Assert.AreNotEqual(new PgInet(IPAddress.Parse("192.0.2.1")), value);
        Assert.AreEqual(new PgInet(IPAddress.Parse("::ffff:192.0.2.1")), value);
        Assert.AreEqual(new PgInet(IPAddress.Parse("::ffff:192.0.2.1")).GetHashCode(), value.GetHashCode());
        Assert.AreEqual("0.0.0.0/0", default(PgInet).ToString());
        Assert.AreEqual("0.0.0.0/0", default(PgCidr).ToString());
        Assert.AreEqual(default, new PgInet(IPAddress.Any, 0));
        Assert.AreEqual(default, new PgCidr(default(IPNetwork)));
    }

    /// <summary>
    /// Invalid prefixes, scoped IPv6, nonzero cidr host bits and lossy host-only casts fail explicitly.
    /// </summary>
    [TestMethod]
    public void ConstructionRejectsInformationLoss()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new PgInet(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgInet(IPAddress.Loopback, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgInet(IPAddress.Loopback, 33));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgInet(IPAddress.IPv6Loopback, 129));
        Assert.ThrowsExactly<ArgumentException>(() => new PgInet(IPAddress.Parse("fe80::1%3")));
        var value = new PgInet(IPAddress.Parse("192.0.2.129"), 25);
        Assert.ThrowsExactly<ArgumentException>(() => new PgCidr(value));
        Assert.ThrowsExactly<InvalidCastException>(value.ToIPAddress);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => value.WithPrefixLength(33));
        Assert.AreEqual("192.0.2.129/24", value.WithPrefixLength(24).ToString());
        Assert.AreEqual("192.0.2.0/24", value.Network.WithPrefixLength(24).ToString());
        Assert.AreEqual(IPAddress.Parse("192.0.2.129"), value.WithPrefixLength(32).ToIPAddress());
    }

    /// <summary>
    /// Network buffers preserve zero bytes, family and prefix, including default and mapped addresses.
    /// </summary>
    [TestMethod]
    [DataRow("0.0.0.0", 0)]
    [DataRow("192.0.2.1", 24)]
    [DataRow("::", 0)]
    [DataRow("::ffff:192.0.2.1", 128)]
    [DataRow("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", 127)]
    public void BinaryTransportRetainsNetworkBits(string text, int prefix)
    {
        var value = new PgInet(IPAddress.Parse(text), prefix);
        NativeValue transport = NativeValue.FromInet(value);
        NativeValue cidr = NativeValue.FromCidr(value.Network);
        try
        {
            Assert.AreEqual(value, transport.ReadInet());
            Assert.AreEqual(value.Network, cidr.ReadCidr());
            Assert.AreEqual(prefix, transport.ReadBytes()[1]);
            Assert.ThrowsExactly<InvalidOperationException>(() => transport.ReadCidr());
            Assert.ThrowsExactly<InvalidOperationException>(() => cidr.ReadInet());
        }
        finally
        {
            transport.Release();
            cidr.Release();
        }
    }

    /// <summary>
    /// Corrupt network headers and host bits cannot be accepted as valid cidr values.
    /// </summary>
    [TestMethod]
    [DataRow(new byte[] { })]
    [DataRow(new byte[] { 4, 0, 1, 4, 0, 0, 0 })]
    [DataRow(new byte[] { 5, 0, 1, 4, 0, 0, 0, 0 })]
    [DataRow(new byte[] { 4, 33, 1, 4, 0, 0, 0, 0 })]
    [DataRow(new byte[] { 4, 0, 0, 4, 0, 0, 0, 0 })]
    [DataRow(new byte[] { 4, 0, 1, 3, 0, 0, 0, 0 })]
    public void InvalidBinaryHeadersAreRejected(byte[] bytes)
    {
        NativeValue transport = NativeValue.FromBytes(bytes);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => transport.ReadCidr());
        }
        finally
        {
            transport.Release();
        }
    }

    /// <summary>
    /// A correctly framed cidr payload still requires zero host bits.
    /// </summary>
    [TestMethod]
    public void BinaryCidrRejectsHostBits()
    {
        NativeValue transport = NativeValue.FromBytes([4, 24, 1, 4, 192, 0, 2, 1]);
        try
        {
            Assert.ThrowsExactly<ArgumentException>(() => transport.ReadCidr());
        }
        finally
        {
            transport.Release();
        }
    }

    /// <summary>
    /// TryParse rejects missing input but must not misreport missing backend access as malformed input.
    /// </summary>
    [TestMethod]
    public void ParsingRequiresBackendAccess()
    {
        Assert.IsFalse(PgInet.TryParse(null, out PgInet value));
        Assert.AreEqual(default, value);
        Assert.IsFalse(PgCidr.TryParse("bad\0input", out PgCidr network));
        Assert.AreEqual(default, network);
        Assert.ThrowsExactly<InvalidOperationException>(() => PgInet.TryParse("127.0.0.1", out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgCidr.TryParse("127.0.0.0/8", out _));
    }
}
