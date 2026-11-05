using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Xunit;

namespace CiciPDA.Tests;

/// <summary>Runs the real Java agent on this PC (no phone needed) and exercises the C# client, relay and framing against it.</summary>
public sealed class AgentFixture : IDisposable
{
    public int Port { get; }
    public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
    public bool Available { get; }
    ManagedProcess? _p;

    public AgentFixture()
    {
        string? java = new[] { @"C:\Program Files\Android\Android Studio\jbr\bin\java.exe" }.FirstOrDefault(File.Exists);
        string? cls = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "agent", "out", "TunnelMain.class"))) { cls = Path.Combine(d.FullName, "src", "agent", "out"); break; }
        if (java == null || cls == null) return;
        Port = ((IPEndPoint)new Func<IPEndPoint>(() => { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var e = (IPEndPoint)l.LocalEndpoint; l.Stop(); return e; })()).Port;
        _p = new ManagedProcess(java, new[] { "-cp", cls, "TunnelMain", Port.ToString() }, redirectStdin: true);
        var ready = new TaskCompletionSource<bool>();
        _p.OutLine += l => { if (l.StartsWith("READY")) ready.TrySetResult(true); };
        _p.Start();
        _p.WriteLineAsync(Convert.ToHexString(Key).ToLowerInvariant()).Wait();
        Available = ready.Task.Wait(TimeSpan.FromSeconds(15));
    }

    public void Dispose() => _p?.Dispose();
}

public class ProtocolTests : IClassFixture<AgentFixture>
{
    readonly AgentFixture _a;
    public ProtocolTests(AgentFixture a) { _a = a; }

    static (TcpListener l, int port) Echo()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c; try { c = await l.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () => { using var _c = c; var s = c.GetStream(); var b = new byte[8192]; int n; try { while ((n = await s.ReadAsync(b)) > 0) await s.WriteAsync(b.AsMemory(0, n)); } catch { } });
            }
        });
        return (l, ((IPEndPoint)l.LocalEndpoint).Port);
    }

    [Fact]
    public void Agent_Started_ForIntegrationTests() => Assert.True(_a.Available, "Java agent could not start; integration tests would be vacuous.");

    [Fact]
    public async Task Channel_RoundTrips_LargePayload_ThroughRealAgent()
    {
        if (!_a.Available) return;
        var (l, port) = Echo();
        using var _l = new LStop(l);
        using var ch = await AgentChannel.ConnectAsync(_a.Port, _a.Key, "127.0.0.1", port, CancellationToken.None);
        var data = RandomNumberGenerator.GetBytes(100_000);       // forces many 16000-byte frames
        var writer = ch.WriteAsync(data).AsTask();
        var got = new byte[data.Length];
        await ch.ReadExactlyAsync(got);
        await writer;
        Assert.Equal(data, got);
    }

    [Fact]
    public async Task Channel_WithWrongKey_IsRejected()
    {
        if (!_a.Available) return;
        var (l, port) = Echo();
        using var _l = new LStop(l);
        var wrong = RandomNumberGenerator.GetBytes(32);
        await Assert.ThrowsAsync<AgentConnectException>(() => AgentChannel.ConnectAsync(_a.Port, wrong, "127.0.0.1", port, CancellationToken.None, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Channel_ReportsUnreachableTarget()
    {
        if (!_a.Available) return;
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int dead = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        await Assert.ThrowsAsync<AgentConnectException>(() => AgentChannel.ConnectAsync(_a.Port, _a.Key, "127.0.0.1", dead, CancellationToken.None));
    }

    [Fact]
    public async Task SocksRelay_Connect_RelaysTcpThroughAgent()
    {
        if (!_a.Available) return;
        var (l, port) = Echo();
        using var _l = new LStop(l);
        using var relay = new SocksRelay(_a.Port, _a.Key); relay.Start();
        using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, relay.Port);
        var s = c.GetStream();
        await s.WriteAsync(new byte[] { 5, 1, 0 }); var m = new byte[2]; await s.ReadExactlyAsync(m); Assert.Equal(new byte[] { 5, 0 }, m);
        var req = new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port };
        await s.WriteAsync(req); var rep = new byte[10]; await s.ReadExactlyAsync(rep); Assert.Equal(0, rep[1]);
        var msg = System.Text.Encoding.ASCII.GetBytes("hello through the phone");
        await s.WriteAsync(msg); var back = new byte[msg.Length]; await s.ReadExactlyAsync(back);
        Assert.Equal(msg, back);
        Assert.True(relay.BytesUp >= msg.Length && relay.BytesDown >= msg.Length);
    }

    [Fact]
    public async Task SocksRelay_Connect_ToClosedPort_RepliesFailure()
    {
        if (!_a.Available) return;
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int dead = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var relay = new SocksRelay(_a.Port, _a.Key); relay.Start();
        using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, relay.Port);
        var s = c.GetStream();
        await s.WriteAsync(new byte[] { 5, 1, 0 }); await s.ReadExactlyAsync(new byte[2]);
        await s.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(dead >> 8), (byte)dead });
        var rep = new byte[10]; await s.ReadExactlyAsync(rep);
        Assert.NotEqual(0, rep[1]);
    }

    [Fact]
    public void Derive_GivesDifferentKeysPerDirectionAndSalt()
    {
        var k = RandomNumberGenerator.GetBytes(32); var salt = RandomNumberGenerator.GetBytes(16);
        Assert.NotEqual(AgentChannel.Derive(k, "c2s", salt), AgentChannel.Derive(k, "s2c", salt));
        Assert.NotEqual(AgentChannel.Derive(k, "c2s", salt), AgentChannel.Derive(k, "c2s", RandomNumberGenerator.GetBytes(16)));
    }

    sealed class LStop : IDisposable { readonly TcpListener _l; public LStop(TcpListener l) { _l = l; } public void Dispose() => _l.Stop(); }
}

public class AdbParsingTests
{
    [Fact]
    public void ParseDevices_HandlesAuthStatesAndTransports()
    {
        const string o = "List of devices attached\n" +
            "R5CT123ABC             device usb:1-2 product:a54xeea model:SM_A546B device:a54x transport_id:1\n" +
            "192.168.1.50:5555      device product:x model:Pixel_7 device:panther transport_id:2\n" +
            "ZX1G22                 unauthorized usb:1-3 transport_id:3\n" +
            "EMU                    offline\n";
        var l = AdbService.ParseDevices(o);
        Assert.Equal(4, l.Count);
        Assert.Equal("SM A546B", l[0].DisplayName); Assert.True(l[0].IsAuthorized); Assert.Equal("USB", l[0].TransportName);
        Assert.True(l[1].IsWifi); Assert.Equal(IPAddress.Parse("192.168.1.50"), l[1].WifiAddress);
        Assert.Equal("unauthorized", l[2].State); Assert.False(l[2].IsAuthorized);
        Assert.Equal("offline", l[3].State);
    }

    [Fact]
    public void ParseSignalLevel_ReadsTelephonyDump()
        => Assert.Equal(3, AdbService.ParseSignalLevel("mSignalStrength=SignalStrength: ... level=3 foo"));
}

public class StorageTests
{
    [Fact]
    public void Secret_RoundTrips_AndIsNotPlainText()
    {
        var p = Secret.Protect("s3cret-pass");
        Assert.DoesNotContain("s3cret", p);
        Assert.Equal("s3cret-pass", Secret.Unprotect(p));
        Assert.Equal("", Secret.Unprotect("not-base64!!"));
    }

    [Fact]
    public void Log_MasksRegisteredSecrets()
    {
        Log.RegisterSecret("abcd-secret-key");
        Assert.Equal("key=***", Log.Redact("key=abcd-secret-key"));
    }

    [Fact]
    public void Passphrase_HasRequestedLength() => Assert.Equal(14, Secret.GeneratePassphrase().Length);
}

public class OrchestratorTests
{
    [Fact]
    public async Task Connect_WithoutAdmin_FailsHonestly_AndNeverReportsConnected()
    {
        if (WinNet.IsElevated()) return; // this test only makes sense for a non-elevated run
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "cici-test-" + Guid.NewGuid());
        var s = new SettingsStore(); var adb = new AdbService(() => s.Current);
        var o = new Orchestrator(s, adb, new DeviceStore(), new HistoryStore(), new RecoveryJournal());
        var seen = new List<ConnState>();
        o.Changed += () => seen.Add(o.State);
        await o.ConnectAsync(ConnectionMethod.PhoneVpn);
        Assert.Equal(ConnState.Error, o.State);
        Assert.True(o.NeedsElevation);
        Assert.DoesNotContain(ConnState.Connected, seen);
        Assert.Equal(PathState.Down, o.Vpn);
        await o.DisconnectAsync();      // must be safe/idempotent
    }

    [Fact]
    public void Journal_RoundTrip_AndRecoveryOfEmptyStateIsNoop()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "cici-test-" + Guid.NewGuid());
        var j = new RecoveryJournal();
        j.State.ForwardPort = 1234; j.State.BypassRoutes.Add(new BypassRoute("10.0.0.5/32", 3)); j.Save();
        var j2 = new RecoveryJournal();
        Assert.Equal(1234, j2.State.ForwardPort); Assert.Single(j2.State.BypassRoutes);
        j2.Reset();
        Assert.True(new RecoveryJournal().State.IsEmpty);
    }
}
