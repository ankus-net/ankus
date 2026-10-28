using System;
using System.Net;
using System.Net.Sockets;

namespace Ankus.Testing;

/// <summary>
/// Holds a requested or operating-system-assigned loopback TCP port during cluster initialization.
/// The required release before PostgreSQL binds can race other processes; startup handles that collision.
/// </summary>
internal sealed class PortReservation : IDisposable
{
    private TcpListener? _listener;

    private PortReservation(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    /// <summary>
    /// Gets the reserved TCP port.
    /// </summary>
    internal int Port { get; }

    /// <summary>
    /// Reserves an IPv4 loopback port on the current operating system.
    /// </summary>
    /// <param name="port">The requested port, or zero for an operating-system-assigned port.</param>
    /// <returns>A reservation that owns the listening socket.</returns>
    internal static PortReservation Create(int port = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return new PortReservation(listener, ((IPEndPoint)listener.LocalEndpoint).Port);
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    /// <summary>
    /// Transfers the bound listener to a contention test without reopening a port-allocation race.
    /// </summary>
    /// <returns>The listener, which the caller must stop after the competing startup completes.</returns>
    internal TcpListener TakeListener()
    {
        ObjectDisposedException.ThrowIf(_listener is null, this);
        TcpListener listener = _listener;
        _listener = null;
        return listener;
    }

    /// <summary>
    /// Releases the owned listening socket so PostgreSQL can bind the reserved port.
    /// A transferred listener remains owned by its recipient.
    /// </summary>
    public void Dispose()
    {
        _listener?.Stop();
        _listener = null;
    }
}
