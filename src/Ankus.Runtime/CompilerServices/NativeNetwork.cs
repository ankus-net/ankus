using System;
using System.Net;
using System.Net.Sockets;

namespace Ankus.CompilerServices;

public partial struct NativeValue
{
    /// <summary>
    /// Reads a length-delimited, family-normalized PostgreSQL inet binary value.
    /// </summary>
    /// <returns>The detached address and prefix.</returns>
    public readonly PgInet ReadInet() => ReadNetwork(cidr: false);

    /// <summary>
    /// Reads a cidr binary value and verifies that it has no host bits.
    /// </summary>
    /// <returns>The detached network.</returns>
    public readonly PgCidr ReadCidr() => new(ReadNetwork(cidr: true));

    /// <summary>
    /// Copies an inet value to an allocator-matched binary transport buffer.
    /// </summary>
    /// <param name="value">The address and prefix.</param>
    /// <returns>The owned transport; the caller must release it.</returns>
    public static NativeValue FromInet(PgInet value) => FromNetwork(value, cidr: false);

    /// <summary>
    /// Copies a cidr value to an allocator-matched binary transport buffer.
    /// </summary>
    /// <param name="value">The network.</param>
    /// <returns>The owned transport; the caller must release it.</returns>
    public static NativeValue FromCidr(PgCidr value) => FromNetwork(value.Inet, cidr: true);

    private readonly PgInet ReadNetwork(bool cidr)
    {
        ReadOnlySpan<byte> bytes = ReadBytes();
        if (bytes.Length is not (8 or 20) || bytes[0] != (bytes.Length == 8 ? 4 : 6) ||
            bytes[1] > (bytes.Length == 8 ? 32 : 128) || bytes[2] != (cidr ? 1 : 0) || bytes[3] != bytes.Length - 4)
        {
            throw new InvalidOperationException("Invalid network transport header or length.");
        }

        return new PgInet(new IPAddress(bytes[4..]), bytes[1]);
    }

    private static NativeValue FromNetwork(PgInet value, bool cidr)
    {
        Span<byte> bytes = stackalloc byte[20];
        bool ipv6 = value.AddressFamily == AddressFamily.InterNetworkV6;
        bytes[0] = (byte)(ipv6 ? 6 : 4);
        bytes[1] = (byte)value.PrefixLength;
        bytes[2] = (byte)(cidr ? 1 : 0);
        bytes[3] = (byte)(ipv6 ? 16 : 4);
        value.Address.TryWriteBytes(bytes[4..], out int length);
        return FromBytes(bytes[..(length + 4)]);
    }
}
