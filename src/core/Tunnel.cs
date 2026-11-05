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
    public const int MaxPlain = 15000;
    public const int Protocol = 3;
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

    public static byte[] Derive(byte[] sessionKey, string label, byte[] salt, byte[] serverNonce)
    {
        var data = new byte[label.Length + salt.Length + serverNonce.Length];
        Encoding.ASCII.GetBytes(label, data);
        salt.CopyTo(data, label.Length);
        serverNonce.CopyTo(data, label.Length + salt.Length);
        return HMACSHA256.HashData(sessionKey, data);
    }

    static byte[] Nonce(ulong ctr)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), ctr);
        return n;
    }

    /// <summary>Opens an authenticated channel and sends a command ("T:host:port", "U" or "S"). Returns the channel and the first reply frame.</summary>
    static async Task<(AgentChannel ch, byte[] reply)> OpenAsync(int localPort, byte[] sessionKey, string command, CancellationToken ct, TimeSpan? timeout)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
            await tcp.ConnectAsync(IPAddress.Loopback, localPort, to.Token);
            var ns = tcp.GetStream();
            var salt = RandomNumberGenerator.GetBytes(16);
            await ns.WriteAsync(salt, to.Token);
            var nonce = new byte[16];
            try { await ns.ReadExactlyAsync(nonce, to.Token); }
            catch (EndOfStreamException) { throw new AgentConnectException("The phone helper closed the connection (not running, or protocol mismatch)."); }
            var ch = new AgentChannel(tcp, new AesGcm(Derive(sessionKey, "c2s", salt, nonce), 16), new AesGcm(Derive(sessionKey, "s2c", salt, nonce), 16));
            await ch.WriteAsync(Encoding.UTF8.GetBytes(command), to.Token);
            var hdr = new byte[2];
            // A wrong pairing key makes the relay drop the connection without a reply.
            if (!await ch.ReadFrameAsync(to.Token)) throw new AgentConnectException("Pairing key rejected or the helper stopped.");
            var reply = ch.TakeFrame();
            return (ch, reply);
        }
        catch (AgentConnectException) { tcp.Dispose(); throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { tcp.Dispose(); throw new AgentConnectException("Timed out talking to the phone helper."); }
        catch (Exception ex) when (ex is IOException or SocketException or CryptographicException or InvalidOperationException)
        { tcp.Dispose(); throw new AgentConnectException("Phone helper connection failed: " + ex.Message, ex); }
        catch { tcp.Dispose(); throw; }
    }

    /// <summary>TCP stream to host:port through the phone.</summary>
    public static async Task<AgentChannel> ConnectAsync(int localPort, byte[] sessionKey, string host, int port, CancellationToken ct, TimeSpan? timeout = null)
    {
        var (ch, reply) = await OpenAsync(localPort, sessionKey, $"T:{host}:{port}", ct, timeout);
        if (reply.Length != 1 || reply[0] != 0) { ch.Dispose(); throw new AgentConnectException($"The phone could not reach {host}:{port}."); }
        return ch;
    }

    /// <summary>UDP datagram relay channel (frames: family, address, port, payload).</summary>
    public static async Task<AgentChannel> ConnectUdpAsync(int localPort, byte[] sessionKey, CancellationToken ct)
    {
        var (ch, reply) = await OpenAsync(localPort, sessionKey, "U", ct, null);
        if (reply.Length != 1 || reply[0] != 0) { ch.Dispose(); throw new AgentConnectException("The phone helper refused UDP relaying."); }
        return ch;
    }

    /// <summary>Authenticated health/diagnostic report from the phone helper (JSON). Also proves the pairing key and protocol version.</summary>
    public static async Task<string> GetStatusAsync(int localPort, byte[] sessionKey, CancellationToken ct, TimeSpan? timeout = null)
    {
        var (ch, reply) = await OpenAsync(localPort, sessionKey, "S", ct, timeout ?? TimeSpan.FromSeconds(8));
        ch.Dispose();
        return Encoding.UTF8.GetString(reply);
    }

    byte[]? _frame;
    byte[] TakeFrame() { var f = _frame ?? Array.Empty<byte>(); _frame = null; return f; }

    /// <summary>Reads exactly one frame (for command/reply and datagram exchanges). False if the peer closed cleanly.</summary>
    public async Task<bool> ReadFrameAsync(CancellationToken ct)
    {
        var hdr = new byte[2];
        if (!await ReadFullAsync(hdr, ct)) return false;
        int l = BinaryPrimitives.ReadUInt16BigEndian(hdr);
        if (l < 16 || l > MaxPlain + 16) throw new IOException("Invalid frame length from phone helper.");
        var c = new byte[l];
        if (!await ReadFullAsync(c, ct)) throw new IOException("Truncated frame.");
        var plain = new byte[l - 16];
        try { _recv.Decrypt(Nonce(_rx++), c.AsSpan(0, l - 16), c.AsSpan(l - 16, 16), plain); }
        catch (CryptographicException) { throw new IOException("Frame authentication failed."); }
        _frame = plain;
        return true;
    }

    public byte[] Frame => TakeFrame();

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
            if (!await ReadFrameAsync(ct)) return 0;
            _pending = TakeFrame(); _pendingPos = 0;
            if (_pending.Length == 0) return 0;
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
/// UDP ASSOCIATE is relayed as datagrams through one UDP channel to the phone (DNS, QUIC, ...). IP fragments are dropped and counted.
/// </summary>
public sealed class SocksRelay : IDisposable
{
    readonly int _agentPort;
    readonly byte[] _key;
    TcpListener? _listener;
    readonly CancellationTokenSource _cts = new();
    long _bytesDown, _bytesUp, _active, _total, _failed, _udpDropped, _dnsQueries, _udpForwarded;

    public int Port { get; private set; }
    public long BytesDown => Interlocked.Read(ref _bytesDown);
    public long BytesUp => Interlocked.Read(ref _bytesUp);
    public long ActiveConnections => Interlocked.Read(ref _active);
    public long TotalConnections => Interlocked.Read(ref _total);
    public long FailedConnections => Interlocked.Read(ref _failed);
    public long UdpDropped => Interlocked.Read(ref _udpDropped);
    public long DnsQueries => Interlocked.Read(ref _dnsQueries);
    public long UdpForwarded => Interlocked.Read(ref _udpForwarded);

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
        await ns.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port }, ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The association lives as long as the TCP control connection.
        var ctl = Task.Run(async () => { try { var b = new byte[1]; while (await ns.ReadAsync(b, linked.Token) > 0) { } } catch { } linked.Cancel(); });
        AgentChannel? ch = null;
        IPEndPoint? client = null;
        Task? reader = null;
        try
        {
            ch = await AgentChannel.ConnectUdpAsync(_agentPort, _key, linked.Token);
            while (!linked.IsCancellationRequested)
            {
                var res = await udp.ReceiveAsync(linked.Token);
                client = res.RemoteEndPoint;
                var d = res.Buffer;
                if (d.Length < 10 || d[2] != 0) { Interlocked.Increment(ref _udpDropped); continue; }   // fragments unsupported
                byte[] frame;
                if (d[3] == 1 && d.Length >= 10) { frame = new byte[1 + 4 + 2 + d.Length - 10]; frame[0] = 4; Array.Copy(d, 4, frame, 1, 6); Array.Copy(d, 10, frame, 7, d.Length - 10); }
                else if (d[3] == 4 && d.Length >= 22) { frame = new byte[1 + 16 + 2 + d.Length - 22]; frame[0] = 6; Array.Copy(d, 4, frame, 1, 18); Array.Copy(d, 22, frame, 19, d.Length - 22); }
                else { Interlocked.Increment(ref _udpDropped); continue; }                                // domain-name UDP targets are not produced by tun2socks
                if (frame.Length > AgentChannel.MaxPlain) { Interlocked.Increment(ref _udpDropped); continue; }
                int dport = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(frame[0] == 4 ? 5 : 17));
                if (dport == 53) Interlocked.Increment(ref _dnsQueries);
                Interlocked.Increment(ref _udpForwarded);
                await ch.WriteAsync(frame, linked.Token);
                Interlocked.Add(ref _bytesUp, frame.Length);
                reader ??= Task.Run(() => UdpReturnLoop(ch, udp, () => client, linked.Token));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException or AgentConnectException)
        {
            if (ex is AgentConnectException) Log.Warn("socks", "UDP channel: " + ex.Message);
        }
        linked.Cancel();
        ch?.Dispose();
        await ctl;
        if (reader != null) try { await reader; } catch { }
    }

    async Task UdpReturnLoop(AgentChannel ch, UdpClient udp, Func<IPEndPoint?> client, CancellationToken ct)
    {
        try
        {
            while (await ch.ReadFrameAsync(ct))
            {
                var f = ch.Frame;
                if (f.Length < 7) continue;
                int alen = f[0] == 4 ? 4 : 16;
                if (f.Length < 1 + alen + 2) continue;
                var hdr = new byte[4 + alen + 2];
                hdr[3] = (byte)(alen == 4 ? 1 : 4);
                Array.Copy(f, 1, hdr, 4, alen + 2);
                var outp = new byte[hdr.Length + f.Length - (1 + alen + 2)];
                hdr.CopyTo(outp, 0);
                Array.Copy(f, 1 + alen + 2, outp, hdr.Length, f.Length - (1 + alen + 2));
                var dst = client();
                if (dst == null) continue;
                await udp.SendAsync(outp, outp.Length, dst);
                Interlocked.Add(ref _bytesDown, f.Length);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        _cts.Dispose();
    }
}
