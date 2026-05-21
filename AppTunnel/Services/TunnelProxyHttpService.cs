using System.Buffers.Binary;
using System.IO;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace AppTunnel.Services;

/// <summary>
/// Performs HTTP GET requests through the local mixed/SOCKS proxy so traffic exits via the VPN tunnel.
/// </summary>
public static class TunnelProxyHttpService
{
    public static async Task<string?> GetAsync(
        int proxyPort,
        string host,
        int port,
        string path,
        bool useTls,
        CancellationToken ct = default)
    {
        if (proxyPort <= 0 || string.IsNullOrWhiteSpace(host))
            return null;

        path = string.IsNullOrEmpty(path) ? "/" : path.StartsWith('/') ? path : "/" + path;

        try
        {
            return await GetViaHttpConnectAsync(proxyPort, host, port, path, useTls, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException)
        {
            try
            {
                return await GetViaSocks5Async(proxyPort, host, port, path, useTls, ct);
            }
            catch
            {
                return null;
            }
        }
    }

    public static async Task<byte[]?> GetBytesAsync(
        int proxyPort,
        string host,
        int port,
        string path,
        bool useTls,
        CancellationToken ct = default)
    {
        if (proxyPort <= 0 || string.IsNullOrWhiteSpace(host))
            return null;

        path = string.IsNullOrEmpty(path) ? "/" : path.StartsWith('/') ? path : "/" + path;

        try
        {
            return await GetBytesViaHttpConnectAsync(proxyPort, host, port, path, useTls, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException)
        {
            try
            {
                return await GetBytesViaSocks5Async(proxyPort, host, port, path, useTls, ct);
            }
            catch
            {
                return null;
            }
        }
    }

    private static async Task<byte[]> GetBytesViaHttpConnectAsync(
        int proxyPort, string host, int port, string path, bool useTls, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        tcp.NoDelay = true;
        await tcp.ConnectAsync("127.0.0.1", proxyPort, ct);

        await using var stream = tcp.GetStream();
        var connectRequest = Encoding.ASCII.GetBytes(
            $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n");
        await stream.WriteAsync(connectRequest, ct);

        var connectHeader = await ReadHttpHeaderAsync(stream, ct);
        if (!connectHeader.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
            !connectHeader.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
            throw new IOException("proxy CONNECT failed");

        Stream payload = stream;
        if (useTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(
                host, null, SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false);
            payload = ssl;
        }

        await using (payload)
        {
            var request = Encoding.ASCII.GetBytes(
                $"GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\nUser-Agent: TunnelX\r\n\r\n");
            await payload.WriteAsync(request, ct);

            using var ms = new MemoryStream();
            var buffer = new byte[2048];
            int read;
            while ((read = await payload.ReadAsync(buffer, ct)) > 0)
                ms.Write(buffer, 0, read);

            return ExtractHttpBodyBytes(ms.ToArray());
        }
    }

    private static async Task<byte[]> GetBytesViaSocks5Async(
        int proxyPort, string host, int port, string path, bool useTls, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        tcp.NoDelay = true;
        await tcp.ConnectAsync("127.0.0.1", proxyPort, ct);

        await using var stream = tcp.GetStream();
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
        var greet = new byte[2];
        await ReadExactlyAsync(stream, greet, ct);
        if (greet[0] != 0x05 || greet[1] != 0x00)
            throw new IOException("SOCKS5 handshake rejected");

        var hostBytes = Encoding.ASCII.GetBytes(host);
        var req = new byte[7 + hostBytes.Length];
        req[0] = 0x05;
        req[1] = 0x01;
        req[2] = 0x00;
        req[3] = 0x03;
        req[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(req, 5);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(5 + hostBytes.Length), (ushort)port);
        await stream.WriteAsync(req, ct);

        var resp = new byte[4];
        await ReadExactlyAsync(stream, resp, ct);
        if (resp[1] != 0x00)
            throw new IOException($"SOCKS5 connect failed (code {resp[1]})");

        switch (resp[3])
        {
            case 0x01: await ReadExactlyAsync(stream, new byte[6], ct); break;
            case 0x03:
                var lenBuf = new byte[1];
                await ReadExactlyAsync(stream, lenBuf, ct);
                await ReadExactlyAsync(stream, new byte[lenBuf[0] + 2], ct);
                break;
            case 0x04: await ReadExactlyAsync(stream, new byte[18], ct); break;
        }

        Stream payload = stream;
        if (useTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(
                host, null, SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false);
            payload = ssl;
        }

        await using (payload)
        {
            var request = Encoding.ASCII.GetBytes(
                $"GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\nUser-Agent: TunnelX\r\n\r\n");
            await payload.WriteAsync(request, ct);

            using var ms = new MemoryStream();
            var buffer = new byte[2048];
            int read;
            while ((read = await payload.ReadAsync(buffer, ct)) > 0)
                ms.Write(buffer, 0, read);

            return ExtractHttpBodyBytes(ms.ToArray());
        }
    }

    private static byte[] ExtractHttpBodyBytes(byte[] responseBytes)
    {
        if (responseBytes.Length == 0)
            return Array.Empty<byte>();

        var headerEnd = IndexOfHeaderSeparator(responseBytes);
        if (headerEnd < 0)
            return responseBytes;

        var headerText = Encoding.ASCII.GetString(responseBytes, 0, headerEnd);
        var bodyOffset = headerEnd + 4;
        var bodyLength = responseBytes.Length - bodyOffset;
        if (bodyLength <= 0)
            return Array.Empty<byte>();

        var body = new byte[bodyLength];
        Buffer.BlockCopy(responseBytes, bodyOffset, body, 0, bodyLength);

        if (headerText.IndexOf("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            return DecodeChunkedBody(body);

        return body;
    }

    private static int IndexOfHeaderSeparator(byte[] bytes)
    {
        for (var i = 0; i <= bytes.Length - 4; i++)
        {
            if (bytes[i] == '\r' &&
                bytes[i + 1] == '\n' &&
                bytes[i + 2] == '\r' &&
                bytes[i + 3] == '\n')
                return i;
        }

        return -1;
    }

    private static int IndexOfCrlf(byte[] bytes, int start)
    {
        for (var i = start; i <= bytes.Length - 2; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n')
                return i;
        }

        return -1;
    }

    private static byte[] DecodeChunkedBody(byte[] chunked)
    {
        using var ms = new MemoryStream();
        var offset = 0;

        while (offset < chunked.Length)
        {
            var lineEnd = IndexOfCrlf(chunked, offset);
            if (lineEnd < 0)
                break;

            var sizeLine = Encoding.ASCII.GetString(chunked, offset, lineEnd - offset);
            var semicolon = sizeLine.IndexOf(';');
            if (semicolon >= 0)
                sizeLine = sizeLine[..semicolon];

            if (!int.TryParse(sizeLine.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) ||
                size < 0)
                break;

            offset = lineEnd + 2;
            if (size == 0)
                break;

            if (offset + size > chunked.Length)
                break;

            ms.Write(chunked, offset, size);
            offset += size;

            if (offset + 2 <= chunked.Length &&
                chunked[offset] == '\r' &&
                chunked[offset + 1] == '\n')
                offset += 2;
        }

        return ms.ToArray();
    }

    private static async Task<string> GetViaHttpConnectAsync(
        int proxyPort, string host, int port, string path, bool useTls, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        tcp.NoDelay = true;
        await tcp.ConnectAsync("127.0.0.1", proxyPort, ct);

        await using var stream = tcp.GetStream();
        var connectRequest = Encoding.ASCII.GetBytes(
            $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n");
        await stream.WriteAsync(connectRequest, ct);

        var connectHeader = await ReadHttpHeaderAsync(stream, ct);
        if (!connectHeader.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
            !connectHeader.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
            throw new IOException("proxy CONNECT failed");

        Stream payload = stream;
        if (useTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(
                host, null, SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false);
            payload = ssl;
        }

        await using (payload)
        {
            var request = Encoding.ASCII.GetBytes(
                $"GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\nUser-Agent: TunnelX\r\n\r\n");
            await payload.WriteAsync(request, ct);

            using var ms = new MemoryStream();
            var buffer = new byte[2048];
            int read;
            while ((read = await payload.ReadAsync(buffer, ct)) > 0)
                ms.Write(buffer, 0, read);

            var response = Encoding.UTF8.GetString(ms.ToArray());
            var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return split >= 0 ? response[(split + 4)..] : response;
        }
    }

    private static async Task<string> GetViaSocks5Async(
        int proxyPort, string host, int port, string path, bool useTls, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        tcp.NoDelay = true;
        await tcp.ConnectAsync("127.0.0.1", proxyPort, ct);

        await using var stream = tcp.GetStream();
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
        var greet = new byte[2];
        await ReadExactlyAsync(stream, greet, ct);
        if (greet[0] != 0x05 || greet[1] != 0x00)
            throw new IOException("SOCKS5 handshake rejected");

        var hostBytes = Encoding.ASCII.GetBytes(host);
        var req = new byte[7 + hostBytes.Length];
        req[0] = 0x05;
        req[1] = 0x01;
        req[2] = 0x00;
        req[3] = 0x03;
        req[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(req, 5);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(5 + hostBytes.Length), (ushort)port);
        await stream.WriteAsync(req, ct);

        var resp = new byte[4];
        await ReadExactlyAsync(stream, resp, ct);
        if (resp[1] != 0x00)
            throw new IOException($"SOCKS5 connect failed (code {resp[1]})");

        switch (resp[3])
        {
            case 0x01: await ReadExactlyAsync(stream, new byte[6], ct); break;
            case 0x03:
                var lenBuf = new byte[1];
                await ReadExactlyAsync(stream, lenBuf, ct);
                await ReadExactlyAsync(stream, new byte[lenBuf[0] + 2], ct);
                break;
            case 0x04: await ReadExactlyAsync(stream, new byte[18], ct); break;
        }

        Stream payload = stream;
        if (useTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(
                host, null, SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false);
            payload = ssl;
        }

        await using (payload)
        {
            var request = Encoding.ASCII.GetBytes(
                $"GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\nUser-Agent: TunnelX\r\n\r\n");
            await payload.WriteAsync(request, ct);

            using var ms = new MemoryStream();
            var buffer = new byte[2048];
            int read;
            while ((read = await payload.ReadAsync(buffer, ct)) > 0)
                ms.Write(buffer, 0, read);

            var response = Encoding.UTF8.GetString(ms.ToArray());
            var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return split >= 0 ? response[(split + 4)..] : response;
        }
    }

    private static async Task<string> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[1];
        while (ms.Length < 8192)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            ms.WriteByte(buffer[0]);
            var bytes = ms.ToArray();
            if (bytes.Length >= 4 &&
                bytes[^4] == '\r' &&
                bytes[^3] == '\n' &&
                bytes[^2] == '\r' &&
                bytes[^1] == '\n')
                break;
        }

        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
                throw new IOException("unexpected end of stream");
            offset += read;
        }
    }
}
