using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using Ankus.Serialization;

namespace Ankus;

/// <summary>
/// An immutable PostgreSQL cidr network. Host bits must be zero; the default is 0.0.0.0/0.
/// </summary>
[JsonConverter(typeof(PgCidrConverter))]
public readonly partial record struct PgCidr : IComparable<PgCidr>
{
    /// <summary>
    /// Creates a network without silently discarding host bits.
    /// </summary>
    /// <param name="address">The network address.</param>
    /// <param name="prefixLength">The network prefix length.</param>
    public PgCidr(IPAddress address, int prefixLength) : this(new PgInet(address, prefixLength))
    {
    }

    /// <summary>
    /// Copies a .NET IPNetwork, rejecting scoped IPv6 addresses.
    /// </summary>
    /// <param name="network">The network to copy.</param>
    public PgCidr(IPNetwork network) : this(network.BaseAddress, network.PrefixLength)
    {
    }

    /// <summary>
    /// Creates a network from an inet value whose host bits are already zero.
    /// Use PgInet.Network to explicitly clear host bits first.
    /// </summary>
    /// <param name="value">The network value.</param>
    public PgCidr(PgInet value)
    {
        if (!value.IsNetwork)
        {
            throw new ArgumentException("A cidr value cannot have nonzero host bits.", nameof(value));
        }

        Inet = value;
    }

    /// <summary>
    /// Gets this network as an inet value without information loss.
    /// </summary>
    public PgInet Inet { get; }

    /// <summary>
    /// Gets a detached copy of the network address.
    /// </summary>
    public IPAddress Address => Inet.Address;

    /// <summary>
    /// Gets the number of network bits.
    /// </summary>
    public int PrefixLength => Inet.PrefixLength;

    /// <summary>
    /// Converts to a .NET IPNetwork without loss of address family or prefix.
    /// </summary>
    /// <returns>The equivalent .NET network.</returns>
    public IPNetwork ToIPNetwork() => new(Address, PrefixLength);

    /// <summary>
    /// Changes the prefix and clears any host bits exposed by the change, like set_masklen(cidr, integer).
    /// </summary>
    /// <param name="prefixLength">The new prefix length.</param>
    /// <returns>The normalized network.</returns>
    public PgCidr WithPrefixLength(int prefixLength) => Inet.WithPrefixLength(prefixLength).Network;

    /// <summary>
    /// Compares using PostgreSQL network ordering.
    /// </summary>
    /// <param name="other">The network to compare.</param>
    /// <returns>The comparison result.</returns>
    public int CompareTo(PgCidr other) => Inet.CompareTo(other.Inet);

    /// <summary>
    /// Compares two networks in PostgreSQL order.
    /// </summary>
    public static bool operator <(PgCidr left, PgCidr right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Compares two networks in PostgreSQL order, including equality.
    /// </summary>
    public static bool operator <=(PgCidr left, PgCidr right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Compares two networks in reverse PostgreSQL order.
    /// </summary>
    public static bool operator >(PgCidr left, PgCidr right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Compares two networks in reverse PostgreSQL order, including equality.
    /// </summary>
    public static bool operator >=(PgCidr left, PgCidr right)
    {
        return left.CompareTo(right) >= 0;
    }

    /// <summary>
    /// Parses PostgreSQL cidr text on the active backend, enforcing zero host bits.
    /// </summary>
    /// <param name="text">The network text.</param>
    /// <returns>The parsed network.</returns>
    public static PgCidr Parse(string text) => NativeBackend.Network<PgCidr>([PgTemporal.Text(text)]);

    /// <summary>
    /// Tries backend parsing without swallowing backend-access or operational failures.
    /// </summary>
    /// <param name="text">The network text.</param>
    /// <param name="value">The parsed network, or default.</param>
    /// <returns>Whether the text is valid.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgCidr value) => PgNetwork.TryParse(text, out value);

    /// <summary>
    /// Formats a detached network with an explicit prefix, without backend access.
    /// </summary>
    /// <returns>The round-trippable network text.</returns>
    public override string ToString() => Address + "/" + PrefixLength.ToString(CultureInfo.InvariantCulture);
}
