using System.IO;
using System.Net;
using System.Net.Sockets;

namespace AppTunnel.Services;

internal sealed class LocalPortReservation : IDisposable
{
    private readonly TcpListener _listener;

    private LocalPortReservation(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public static LocalPortReservation ReservePreferredOrRandom(int preferredPort, params int[] excludedPorts)
    {
        if (preferredPort > 0 && !excludedPorts.Contains(preferredPort) &&
            TryReserve(preferredPort, out var preferred))
            return preferred;

        for (var i = 0; i < 20; i++)
        {
            var reservation = Reserve(0);
            if (!excludedPorts.Contains(reservation.Port))
                return reservation;

            reservation.Dispose();
        }

        throw new IOException("Could not reserve a free local port.");
    }

    public void Dispose()
    {
        _listener.Stop();
    }

    private static bool TryReserve(int port, out LocalPortReservation reservation)
    {
        try
        {
            reservation = Reserve(port);
            return true;
        }
        catch (SocketException)
        {
            reservation = null!;
            return false;
        }
    }

    private static LocalPortReservation Reserve(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return new LocalPortReservation(listener);
    }
}
