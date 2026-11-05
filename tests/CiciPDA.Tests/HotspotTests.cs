using System.Net;
using System.Net.Sockets;
using Xunit;

namespace CiciPDA.Tests;

[Collection("AppPaths")]
public class HotspotTests
{
    static async Task<string> Get(int port, string path)
    {
        using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, port);
        var ns = c.GetStream();
        await ns.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: x\r\n\r\n"));
        using var r = new StreamReader(ns); return await r.ReadToEndAsync();
    }

    [Fact]
    public async Task ProbeServer_ServesCheckPage_OnlyWithToken_AndRecordsClientResult()
    {
        using var s = new HotspotProbeServer(IPAddress.Loopback); s.Start();
        Assert.Contains("404", await Get(s.Port, "/check?t=wrong"));
        Assert.Contains("CiciPDA connection check", await Get(s.Port, "/check?t=" + s.Token));
        Assert.Contains("403", await Get(s.Port, "/result?t=bad&dns=1&https=1"));
        Assert.False(s.Verified);
        await Get(s.Port, $"/result?t={s.Token}&dns=1&https=0");
        Assert.False(s.Verified); Assert.True(s.DnsOk); Assert.False(s.HttpsOk);      // DNS ok but HTTPS failing is not "verified"
        await Get(s.Port, $"/result?t={s.Token}&dns=1&https=1");
        Assert.True(s.Verified);
    }

    [Theory]
    [InlineData("Enabled", "can share")]
    [InlineData("NoProfile", "Internet Connection Sharing")]
    [InlineData("DisabledByGroupPolicy", "group policy")]
    [InlineData("DisabledByHardwareLimitation", "Wi-Fi")]
    public void Explain_GivesUnderstandableReasons(string cap, string fragment) => Assert.Contains(fragment, HotspotService.Explain(cap), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Check_ForAdapterWithoutProfile_ReportsNoProfile_NeverFallsBackToAnotherConnection()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "cici-hs-" + Guid.NewGuid());
        var chk = await new HotspotService().CheckAsync(Guid.NewGuid().ToString());   // random GUID: no Windows profile belongs to it
        Assert.False(chk.Found); Assert.False(chk.CanShare); Assert.Equal("NoProfile", chk.Capability);
    }

    [Fact]
    public async Task Start_ForUnknownAdapter_FailsWithoutStartingAnything()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "cici-hs-" + Guid.NewGuid());
        var h = new HotspotService();
        Assert.False(await h.StartAsync(Guid.NewGuid().ToString(), "CiciTest", "passphrase123"));
        Assert.Equal(HotspotState.Failed, h.State);
        Assert.Contains("profile", h.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_RejectsInvalidSsidOrPassphrase()
    {
        var h = new HotspotService();
        Assert.False(await h.StartAsync(Guid.NewGuid().ToString(), "", "passphrase123")); Assert.Equal(HotspotState.Failed, h.State);
        Assert.False(await h.StartAsync(Guid.NewGuid().ToString(), "ok", "short")); Assert.Equal(HotspotState.Failed, h.State);
    }

    [Fact]
    public void AdapterGuid_IsNullForMissingInterface() => Assert.Null(HotspotService.AdapterGuid("definitely-not-an-interface"));
}
