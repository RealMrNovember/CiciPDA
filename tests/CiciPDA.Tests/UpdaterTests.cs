using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CiciPDA.Tests;

public class UpdaterTests
{
    static (string pub, ECDsa key) NewKey() { var k = ECDsa.Create(ECCurve.NamedCurves.nistP256); return (Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()), k); }
    static byte[] Sign(ECDsa k, byte[] data) => k.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    static byte[] Manifest(string version = "9.9.9", string installer = "CiciPDA-Setup-9.9.9.exe", string sha = null!, string product = "CiciPDA") =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { product, version, installer, sha256 = sha ?? new string('a', 64), size = 1000, notes = "n" }));

    [Fact]
    public void ValidSignedManifest_IsAccepted()
    {
        var (pub, k) = NewKey(); var j = Manifest();
        var m = AppUpdater.VerifyManifest(j, Sign(k, j), pub);
        Assert.Equal("9.9.9", m.Version);
    }

    [Fact]
    public void ManifestSignedByOtherKey_IsRejected()
    {
        var (pub, _) = NewKey(); var (_, attacker) = NewKey(); var j = Manifest();
        Assert.Throws<TrustException>(() => AppUpdater.VerifyManifest(j, Sign(attacker, j), pub));
    }

    [Fact]
    public void TamperedManifest_IsRejected()
    {
        var (pub, k) = NewKey(); var j = Manifest(); var sig = Sign(k, j);
        var evil = Manifest(sha: new string('b', 64));
        Assert.Throws<TrustException>(() => AppUpdater.VerifyManifest(evil, sig, pub));
    }

    [Theory]
    [InlineData("..\\evil.exe")]
    [InlineData("sub/setup.exe")]
    [InlineData("setup.bat")]
    public void UnsafeInstallerNames_AreRejected(string name)
    {
        var (pub, k) = NewKey(); var j = Manifest(installer: name);
        Assert.Throws<TrustException>(() => AppUpdater.VerifyManifest(j, Sign(k, j), pub));
    }

    [Fact]
    public void WrongProduct_AndBadChecksum_AreRejected()
    {
        var (pub, k) = NewKey();
        var a = Manifest(product: "Other"); Assert.Throws<TrustException>(() => AppUpdater.VerifyManifest(a, Sign(k, a), pub));
        var b = Manifest(sha: "xyz"); Assert.Throws<TrustException>(() => AppUpdater.VerifyManifest(b, Sign(k, b), pub));
    }

    [Fact]
    public async Task Check_RefusesNonGithubChannel()
    {
        var u = new AppUpdater("http://evil.example/");
        await Assert.ThrowsAsync<TrustException>(() => u.CheckAsync(CancellationToken.None));
        var u2 = new AppUpdater("https://evil.example/");
        await Assert.ThrowsAsync<TrustException>(() => u2.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public void CurrentVersion_IsReadable() => Assert.True(AppUpdater.CurrentVersion >= new Version(0, 0, 0));
}

public class ReleaseArtifactTests
{
    [Fact]
    public void BuiltReleaseArtifacts_VerifyAgainstCompiledInPins_WhenPresent()
    {
        string? dir = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "artifacts", "update.json"))) { dir = Path.Combine(d.FullName, "artifacts"); break; }
        if (dir == null) return;   // artifacts are build outputs, not committed
        var m = AppUpdater.VerifyManifest(File.ReadAllBytes(Path.Combine(dir, "update.json")), File.ReadAllBytes(Path.Combine(dir, "update.json.sig")), ReleaseTrust.PinnedMetadataKey);
        var setup = Path.Combine(dir, m.Installer);
        Assert.Equal(m.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(setup))).ToLowerInvariant());
        var c = ReleaseTrust.Verify(Path.Combine(dir, "companion.json"), Path.Combine(dir, "companion.json.sig"), dir, "artifacts");
        Assert.Equal(20000, c.Meta.VersionCode);
        Assert.False(ReleaseTrust.IsDevelopmentTrust);
    }
}

/// <summary>Talks to the real GitHub release channel. Runs only when CICI_LIVE=1 (needs a published release).</summary>
public class LiveUpdateChannelTests
{
    [Fact]
    public async Task RealChannel_OfferedToOlderVersion_DownloadsAndVerifies_AndNotOfferedToCurrent()
    {
        if (Environment.GetEnvironmentVariable("CICI_LIVE") != "1") return;
        var u = new AppUpdater();
        var offered = await u.CheckAsync(CancellationToken.None, new Version(2, 0, 0));
        Assert.NotNull(offered);
        Assert.True(offered!.Version >= new Version(2, 1, 0));
        Assert.Null(await u.CheckAsync(CancellationToken.None, offered.Version));             // already current: nothing offered
        var path = await u.DownloadAsync(offered, null, CancellationToken.None);               // verifies the signed SHA-256 internally
        try { Assert.True(new FileInfo(path).Length > 10_000_000); Assert.EndsWith(".exe", path); }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }
}
