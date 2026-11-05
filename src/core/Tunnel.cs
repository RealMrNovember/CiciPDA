using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CiciPDA;

public sealed class AgentConnectException : Exception
{
    public AgentConnectException(string m, Exception? inner = null) : base(m, inner) { }
}

/// <summary>
/// Client side of protocol v2 (see src/agent/TunnelMain.java). Authenticated-encryption framing (AES-256-GCM) with
/// distinct keys per direction and per connection, derived from the session key and a random salt via HMAC-SHA256.
/// Exposes a plain Stream so callers just Read/Write.
/// </summary>
public sealed class AgentChannel : Stream
{
    public const int MaxPlain = 16000;
    readonly TcpClient _tcp;
    readonly NetworkStream _ns;
    readonly AesGcm _send, _recv;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    ulong _tx, _rx;
    byte[] _pending = Array.Empty<byte>();
    int _pendingPos;

    AgentChannel(TcpClient tcp, AesGcm send, AesGcm recv)
    {
        _tcp = tcp; _ns = tcp.GetStream(); _send = send; _recv = recv;
    }

    public static byte[] Derive(byte[] sessionKey, string label, byte[] salt)
    {
        var data = new byte[label.Length + salt.Length];
        Encoding.ASCII.GetBytes(label, data);
        salt.CopyTo(data, label.Length);
        return HMACSHA256.HashData(sessionKey, data);
    }

    static byte[] Nonce(ulong ctr)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), ctr);
        return n;
    }

    /// <summary>Opens a connection to the phone helper and asks it to connect to host:port. Throws AgentConnectException on failure.</summary>
    public static async Task<AgentChannel> ConnectAsync(int localPort, byte[] sessionKey, string host, int port, CancellationToken ct, TimeSpan? timeout = null)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
            await tcp.ConnectAsync(IPAddress.Loopback, localPort, to.Token);
            var salt = RandomNumberGenerator.GetBytes(16);
            await tcp.GetStream().WriteAsync(salt, to.Token);
            var ch = new AgentChannel(tcp, new AesGcm(Derive(sessionKey, "c2s", salt), 16), new AesGcm(Derive(sessionKey, "s2c", salt), 16));
            await ch.WriteAsync(Encoding.UTF8.GetBytes($"{host}:{port}"), to.Token);
            var status = new byte[1];
            int n = await ch.ReadAsync(status, to.Token);
            if (n != 1) throw new AgentConnectException("Phone helper closed the connection (wrong key or helper stopped).");
            if (status[0] != 0) throw new AgentConnectException($"The phone could not reach {host}:{port}.");
            return ch;
        }
        catch (AgentConnectException) { tcp.Dispose(); throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { tcp.Dispose(); throw new AgentConnectException("Timed out talking to the phone helper."); }
        catch (Exception ex) when (ex is IOException or SocketException or CryptographicException or InvalidOperationException)
        { tcp.Dispose(); throw new AgentConnectException("Phone helper connection failed: " + ex.Message, ex); }
        catch { tcp.Dispose(); throw; }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            for (int off = 0; off < buffer.Length || (off == 0 && buffer.Length == 0); off += MaxPlain)
            {
                int len = Math.Min(MaxPlain, buffer.Length - off);
                var frame = new byte[2 + len + 16];
                BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)(len + 16));
                _send.Encrypt(Nonce(_tx++), buffer.Span.Slice(off, len), frame.AsSpan(2, len), frame.AsSpan(2 + len, 16));
                await _ns.WriteAsync(frame, ct);
                if (buffer.Length == 0) break;
            }
        }
        finally { _sendLock.Release(); }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pendingPos >= _pending.Length)
        {
            var hdr = new byte[2];
            if (!await ReadFullAsync(hdr, ct)) return 0;
            int l = BinaryPrimitives.ReadUInt16BigEndian(hdr);
            if (l < 16 || l > MaxPlain + 16) throw new IOException("Invalid frame length from phone helper.");
            var ct1 = new byte[l];
            if (!await ReadFullAsync(ct1, ct)) throw new IOException("Truncated frame.");
            var plain = new byte[l - 16];
            try { _recv.Decrypt(Nonce(_rx++), ct1.AsSpan(0, l - 16), ct1.AsSpan(l - 16, 16), plain); }
            catch (CryptographicException) { throw new IOException("Frame authentication failed."); }
            _pending = plain; _pendingPos = 0;
        }
        int n = Math.Min(buffer.Length, _pending.Length - _pendingPos);
        _pending.AsMemory(_pendingPos, n).CopyTo(buffer);
        _pendingPos += n;
        return n;
    }

    async Task<bool> ReadFullAsync(byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await _ns.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) { if (got == 0) return false; throw new IOException("Connection closed mid-frame."); }
            got += n;
        }
        return true;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _tcp.Dispose(); } catch { } _send.Dispose(); _recv.Dispose(); _sendLock.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Loopback SOCKS5 server consumed by tun2socks. TCP CONNECT is relayed through the phone helper.
/// UDP ASSOCIATE supports DNS only (UDP/53 is converted to DNS-over-TCP through the helper); other UDP is dropped and counted.
/// </summary>
public sealed class SocksRelay : IDisposable
{
    readonly int _agentPort;
    readonly byte[] _key;
    TcpListener? _listener;
    readonly CancellationTokenSource _cts = new();
    long _bytesDown, _bytesUp, _active, _total, _failed, _udpDropped, _dnsQueries, _dnsFailures;

    public int Port { get; private set; }
    public long BytesDown => Interlocked.Read(ref _bytesDown);
    public long BytesUp => Interlocked.Read(ref _bytesUp);
    public long ActiveConnections => Interlocked.Read(ref _active);
    public long TotalConnections => Interlocked.Read(ref _total);
    public long FailedConnections => Interlocked.Read(ref _failed);
    public long UdpDropped => Interlocked.Read(ref _udpDropped);
    public long DnsQueries => Interlocked.Read(ref _dnsQueries);
    public long DnsFailures => Interlocked.Read(ref _dnsFailures);

    public SocksRelay(int agentLocalPort, byte[] sessionKey) { _agentPort = agentLocalPort; _key = sessionKey; }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(128);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Log.Warn("socks", "accept: " + ex.Message); await Task.Delay(100, ct).ContinueWith(_ => { }); continue; }
            _ = Task.Run(() => HandleAsync(c, ct), ct);
        }
    }

    static async Task ReadExact(Stream s, byte[] b, CancellationToken ct) => await s.ReadExactlyAsync(b, ct);

    static async Task<(string host, int port)> ReadAddr(Stream s, byte atyp, CancellationToken ct)
    {
        string host;
        if (atyp == 1) { var a = new byte[4]; await ReadExact(s, a, ct); host = new IPAddress(a).ToString(); }
        else if (atyp == 4) { var a = new byte[16]; await ReadExact(s, a, ct); host = new IPAddress(a).ToString(); }
        else if (atyp == 3)
        {
            var l = new byte[1]; await ReadExact(s, l, ct);
            var d = new byte[l[0]]; await ReadExact(s, d, ct); host = Encoding.ASCII.GetString(d);
        }
        else throw new InvalidDataException("bad ATYP");
        var p = new byte[2]; await ReadExact(s, p, ct);
        return (host, BinaryPrimitives.ReadUInt16BigEndian(p));
    }

    async Task HandleAsync(TcpClient c, CancellationToken ct)
    {
        Interlocked.Increment(ref _active); Interlocked.Increment(ref _total);
        try
        {
            using (c)
            {
                c.NoDelay = true;
                // Only accept loopback peers (the listener is loopback-bound already; defence in depth).
                if (c.Client.RemoteEndPoint is IPEndPoint rep && !IPAddress.IsLoopback(rep.Address)) return;
                var ns = c.GetStream();
                var h = new byte[2]; await ReadExact(ns, h, ct);
                if (h[0] != 5) return;
                var methods = new byte[h[1]]; await ReadExact(ns, methods, ct);
                await ns.WriteAsync(new byte[] { 5, 0 }, ct);
                var rq = new byte[4]; await ReadExact(ns, rq, ct);
                var (host, port) = await ReadAddr(ns, rq[3], ct);
                if (rq[1] == 1) await ConnectAsync(ns, host, port, ct);
                else if (rq[1] == 3) await UdpAssociateAsync(c, ns, ct);
                else await ns.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or EndOfStreamException or ObjectDisposedException) { }
        catch (Exception ex) { Log.Warn("socks", "handler: " + ex.Message); }
        finally { Interlocked.Decrement(ref _active); }
    }

    async Task ConnectAsync(NetworkStream ns, string host, int port, CancellationToken ct)
    {
        AgentChannel ch;
        try { ch = await AgentChannel.ConnectAsync(_agentPort, _key, host, port, ct); }
        catch (AgentConnectException ex)
        {
            Interlocked.Increment(ref _failed);
            Log.Debug("socks", $"CONNECT {host}:{port} failed: {ex.Message}");
            await ns.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }
        using (ch)
        {
            await ns.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var up = Pump(ns, ch, true, linked.Token);     // PC -> phone
            var down = Pump(ch, ns, false, linked.Token);  // phone -> PC
            await Task.WhenAny(up, down);
            linked.Cancel();
            try { await Task.WhenAll(up, down); } catch { }
        }
    }

    async Task Pump(Stream from, Stream to, bool upstream, CancellationToken ct)
    {
        var buf = new byte[AgentChannel.MaxPlain];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buf, ct)) > 0)
            {
                await to.WriteAsync(buf.AsMemory(0, n), ct);
                if (upstream) Interlocked.Add(ref _bytesUp, n); else Interlocked.Add(ref _bytesDown, n);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException) { }
    }

    async Task UdpAssociateAsync(TcpClient tcp, NetworkStream ns, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var rep = new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port };
        await ns.WriteAsync(rep, ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The association lives as long as the TCP control connection.
        var ctl = Task.Run(async () => { try { var b = new byte[1]; while (await ns.ReadAsync(b, linked.Token) > 0) { } } catch { } linked.Cancel(); });
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var res = await udp.ReceiveAsync(linked.Token);
                _ = Task.Run(() => HandleDatagram(udp, res, linked.Token));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        linked.Cancel();
        await ctl;
    }

    async Task HandleDatagram(UdpClient udp, UdpReceiveResult res, CancellationToken ct)
    {
        try
        {
            var d = res.Buffer;
            if (d.Length < 10 || d[2] != 0) { Interlocked.Increment(ref _udpDropped); return; }
            int pos = 4; string host;
            if (d[3] == 1) { host = new IPAddress(d.AsSpan(pos, 4)).ToString(); pos += 4; }
            else if (d[3] == 4) { host = new IPAddress(d.AsSpan(pos, 16)).ToString(); pos += 16; }
            else if (d[3] == 3) { int l = d[pos]; host = Encoding.ASCII.GetString(d, pos + 1, l); pos += 1 + l; }
            else { Interlocked.Increment(ref _udpDropped); return; }
            int dport = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos)); pos += 2;
            if (dport != 53) { Interlocked.Increment(ref _udpDropped); return; }

            var query = d.AsSpan(pos).ToArray();
            Interlocked.Increment(ref _dnsQueries);
            var answer = await DnsOverTcpAsync(host, query, ct);
            if (answer == null) { Interlocked.Increment(ref _dnsFailures); return; }
            var hdr = d.AsSpan(0, pos).ToArray();   // echo original address header
            var outp = new byte[hdr.Length + answer.Length];
            hdr.CopyTo(outp, 0); answer.CopyTo(outp, hdr.Length);
            await udp.SendAsync(outp, outp.Length, res.RemoteEndPoint);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (Exception ex) { Log.Debug("socks", "udp: " + ex.Message); }
    }

    async Task<byte[]?> DnsOverTcpAsync(string server, byte[] query, CancellationToken ct)
    {
        try
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(6));
            using var ch = await AgentChannel.ConnectAsync(_agentPort, _key, server, 53, to.Token, TimeSpan.FromSeconds(6));
            var msg = new byte[2 + query.Length];
            BinaryPrimitives.WriteUInt16BigEndian(msg, (ushort)query.Length);
            query.CopyTo(msg, 2);
            await ch.WriteAsync(msg, to.Token);
            Interlocked.Add(ref _bytesUp, msg.Length);
            var l = new byte[2]; await ch.ReadExactlyAsync(l, to.Token);
            var resp = new byte[BinaryPrimitives.ReadUInt16BigEndian(l)];
            await ch.ReadExactlyAsync(resp, to.Token);
            Interlocked.Add(ref _bytesDown, resp.Length + 2);
            return resp;
        }
        catch (Exception ex) when (ex is AgentConnectException or IOException or OperationCanceledException) { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        _cts.Dispose();
    }
}
