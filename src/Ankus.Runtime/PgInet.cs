using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Ankus.Serialization;

namespace Ankus;

/// <summary>
/// An immutable PostgreSQL inet address and prefix, preserving IPv4 and IPv6 identity.
/// The default value is 0.0.0.0/0. Constructors and address operations work outside PostgreSQL.
/// </summary>
[JsonConverter(typeof(PgInetConverter))]
public readonly partial record struct PgInet : IComparable<PgInet>
{
    private readonly UInt128 _bits;
    private readonly bool _ipv6;

    /// <summary>
    /// Copies an IP address, using its full width when no prefix is supplied. Scoped IPv6 addresses are rejected.
    /// </summary>
    /// <param name="address">The IPv4 or IPv6 host address.</param>
    /// <param name="prefixLength">The prefix length, or null for a single host.</param>
    public PgInet(IPAddress address, int? prefixLength = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        _ipv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        if (address.AddressFamily != AddressFamily.InterNetwork && !_ipv6 || _ipv6 && address.ScopeId != 0)
        {
            throw new ArgumentException("PostgreSQL accepts unscoped IPv4 and IPv6 addresses.", nameof(address));
        }

        PrefixLength = prefixLength ?? (_ipv6 ? 128 : 32);
        ValidatePrefix(PrefixLength, _ipv6);
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        _bits = _ipv6 ? BinaryPrimitives.ReadUInt128BigEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private PgInet(UInt128 bits, bool ipv6, int prefixLength)
    {
        _bits = bits;
        _ipv6 = ipv6;
        PrefixLength = prefixLength;
    }

    /// <summary>
    /// Gets the number of significant network bits.
    /// </summary>
    public int PrefixLength { get; }

    /// <summary>
    /// Gets the preserved IPv4 or IPv6 address family; mapped IPv6 remains IPv6.
    /// </summary>
    public AddressFamily AddressFamily => _ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;

    /// <summary>
    /// Gets a detached IPAddress copy of the host, without its network prefix.
    /// </summary>
    public IPAddress Address
    {
        get
        {
            Span<byte> bytes = stackalloc byte[16];
            if (_ipv6)
            {
                BinaryPrimitives.WriteUInt128BigEndian(bytes, _bits);
                return new IPAddress(bytes);
            }

            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)_bits);
            return new IPAddress(bytes[..4]);
        }
    }

    /// <summary>
    /// Gets the network with host bits cleared, like PostgreSQL network(inet).
    /// </summary>
    public PgCidr Network => new(new PgInet(_bits & Mask(PrefixLength), _ipv6, PrefixLength));

    /// <summary>
    /// Gets the address with every host bit set, retaining its prefix.
    /// </summary>
    public PgInet Broadcast => new(_bits | (Mask(Width) ^ Mask(PrefixLength)), _ipv6, PrefixLength);

    /// <summary>
    /// Gets the full-width network mask address.
    /// </summary>
    public PgInet Netmask => new(Mask(PrefixLength), _ipv6, Width);

    /// <summary>
    /// Gets the full-width host mask address.
    /// </summary>
    public PgInet Hostmask => new(Mask(Width) ^ Mask(PrefixLength), _ipv6, Width);

    /// <summary>
    /// Returns an IPAddress only when discarding the prefix would lose no network information.
    /// Use Address to deliberately extract the host from a subnet value.
    /// </summary>
    /// <returns>The detached, full-width host address.</returns>
    public IPAddress ToIPAddress() => PrefixLength == Width ? Address
        : throw new InvalidCastException("The inet prefix would be lost. Use Address to explicitly extract the host.");

    /// <summary>
    /// Changes the prefix without clearing host bits, like set_masklen(inet, integer).
    /// </summary>
    /// <param name="prefixLength">The new prefix length.</param>
    /// <returns>The address with its new prefix.</returns>
    public PgInet WithPrefixLength(int prefixLength)
    {
        ValidatePrefix(prefixLength, _ipv6);
        return new(_bits, _ipv6, prefixLength);
    }

    /// <summary>
    /// Tests subnet containment, including equal prefixes by default, like PostgreSQL >>=.
    /// </summary>
    /// <param name="other">The address and subnet to compare.</param>
    /// <param name="includeEqual">Whether equal prefix lengths count as contained.</param>
    /// <returns>Whether the other subnet is contained within this one.</returns>
    public bool Contains(PgInet other, bool includeEqual = true) => _ipv6 == other._ipv6 &&
        (includeEqual ? PrefixLength <= other.PrefixLength : PrefixLength < other.PrefixLength) &&
        (_bits & Mask(PrefixLength)) == (other._bits & Mask(PrefixLength));

    /// <summary>
    /// Compares family, common network bits, prefix length, then host bits, matching PostgreSQL inet ordering.
    /// </summary>
    /// <param name="other">The value to compare.</param>
    /// <returns>A negative, zero or positive comparison result.</returns>
    public int CompareTo(PgInet other)
    {
        int result = _ipv6.CompareTo(other._ipv6);
        if (result == 0)
        {
            UInt128 mask = Mask(Math.Min(PrefixLength, other.PrefixLength));
            result = (_bits & mask).CompareTo(other._bits & mask);
            if (result == 0)
            {
                result = PrefixLength.CompareTo(other.PrefixLength);
                if (result == 0)
                {
                    result = _bits.CompareTo(other._bits);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Parses PostgreSQL inet input on the active backend, including PostgreSQL's abbreviated IPv4 forms.
    /// </summary>
    /// <param name="text">The PostgreSQL address text.</param>
    /// <returns>The parsed address and prefix.</returns>
    public static PgInet Parse(string text) => NativeBackend.Network<PgInet>([PgTemporal.Text(text)]);

    /// <summary>
    /// Tries PostgreSQL parsing; input errors return false while backend-access and operational errors propagate.
    /// </summary>
    /// <param name="text">The address text.</param>
    /// <param name="value">The parsed address, or default.</param>
    /// <returns>Whether the input is valid.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgInet value) => PgNetwork.TryParse(text, out value);

    /// <summary>
    /// Formats a detached address, omitting a full-width prefix. No backend access is required.
    /// </summary>
    /// <returns>Round-trippable IPv4 or IPv6 text.</returns>
    public override string ToString() => PrefixLength == Width
        ? Address.ToString()
        : string.Create(CultureInfo.InvariantCulture, $"{Address}/{PrefixLength}");

    /// <summary>
    /// Compares two inet values in PostgreSQL order.
    /// </summary>
    public static bool operator <(PgInet left, PgInet right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Compares two inet values in PostgreSQL order, including equality.
    /// </summary>
    public static bool operator <=(PgInet left, PgInet right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Compares two inet values in reverse PostgreSQL order.
    /// </summary>
    public static bool operator >(PgInet left, PgInet right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Compares two inet values in reverse PostgreSQL order, including equality.
    /// </summary>
    public static bool operator >=(PgInet left, PgInet right)
    {
        return left.CompareTo(right) >= 0;
    }

    /// <summary>
    /// Gets whether host bits are zero, as required by cidr.
    /// </summary>
    internal bool IsNetwork => (_bits & Mask(PrefixLength)) == _bits;

    private int Width => _ipv6 ? 128 : 32;
    private UInt128 Mask(int prefix) => prefix == 0 ? 0 : (UInt128.MaxValue >> (128 - prefix)) << (Width - prefix);

    private static void ValidatePrefix(int prefix, bool ipv6)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(prefix);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefix, ipv6 ? 128 : 32);
    }
}
