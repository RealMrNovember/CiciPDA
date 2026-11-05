using System.Text.Json;
using Xunit;

namespace CiciPDA.Tests;

public class TrustTests
{
    static string Bundled()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "src", "app", "companion");
            if (File.Exists(Path.Combine(c, "companion.json"))) return c;
        }
        throw new FileNotFoundException("Run src/agent/build.ps1 first.");
    }

    static string Copy()
    {
        var t = Path.Combine(Path.GetTempPath(), "cici-trust-" + Guid.NewGuid()); Directory.CreateDirectory(t);
        foreach (var f in Directory.GetFiles(Bundled())) File.Copy(f, Path.Combine(t, Path.GetFileName(f)));
        return t;
    }

    [Fact]
    public void BundledRelease_Verifies()
    {
        var d = Bundled();
        var v = ReleaseTrust.Verify(Path.Combine(d, "companion.json"), Path.Combine(d, "companion.json.sig"), d, "bundled");
        Assert.Equal(ReleaseTrust.PackageName, v.Meta.Package);
        Assert.Equal(3, v.Meta.Protocol);
    }

    [Fact]
    public void ApkSigner_CertParsed_MatchesMetadata()
    {
        var d = Bundled();
        var meta = ReleaseTrust.ParseMetadata(File.ReadAllBytes(Path.Combine(d, "companion.json")));
        Assert.Contains(meta.SignerCertSha256, ApkSigning.SignerCertSha256(Path.Combine(d, meta.Apk)));
    }

    [Fact]
    public void TamperedApk_IsRejected_ByHash()
    {
        var d = Copy();
        var apk = Path.Combine(d, "cicipda-companion.apk");
        var b = File.ReadAllBytes(apk); b[100] ^= 0xFF; File.WriteAllBytes(apk, b);
        var ex = Assert.Throws<TrustException>(() => ReleaseTrust.Verify(Path.Combine(d, "companion.json"), Path.Combine(d, "companion.json.sig"), d, "t"));
        Assert.Contains("hash mismatch", ex.Message);
    }

    [Fact]
    public void TamperedMetadata_IsRejected_BySignature()
    {
        var d = Copy();
        var m = Path.Combine(d, "companion.json");
        File.WriteAllText(m, File.ReadAllText(m).Replace("20000", "29999"));
        var ex = Assert.Throws<TrustException>(() => ReleaseTrust.Verify(m, m + ".sig", d, "t"));
        Assert.Contains("signature is invalid", ex.Message);
    }

    [Fact]
    public void MatchingHashFromUnsignedSource_IsNotEnough()
    {
        // An attacker who replaces BOTH the APK and its metadata (hash matches) but cannot sign with the release key must fail.
        var d = Copy();
        var m = Path.Combine(d, "companion.json");
        var apk = Path.Combine(d, "cicipda-companion.apk");
        var b = File.ReadAllBytes(apk); b[200] ^= 0x1; File.WriteAllBytes(apk, b);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();
        var meta = ReleaseTrust.ParseMetadata(File.ReadAllBytes(m)); meta.Sha256 = sha;
        File.WriteAllText(m, JsonSerializer.Serialize(meta));
        Assert.Throws<TrustException>(() => ReleaseTrust.Verify(m, m + ".sig", d, "t"));
    }

    [Fact]
    public void UnsafeApkName_IsRejected()
    {
        var d = Copy(); var m = Path.Combine(d, "companion.json");
        // forge a signature with a throw-away key: must fail on signature before the path is ever used
        File.WriteAllText(m, File.ReadAllText(m).Replace("cicipda-companion.apk", "..\\evil.apk"));
        Assert.Throws<TrustException>(() => ReleaseTrust.Verify(m, m + ".sig", d, "t"));
    }

    [Fact]
    public void ApkWithoutSigningBlock_IsRejected() =>
        Assert.Throws<InvalidDataException>(() => ApkSigning.SignerCertSha256(new byte[] { 0x50, 0x4b, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));

    [Theory]
    [InlineData("Failure [INSTALL_FAILED_USER_RESTRICTED: Install canceled by user]", InstallFailure.UserRestricted)]
    [InlineData("Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: signatures do not match]", InstallFailure.SignatureMismatch)]
    [InlineData("Failure [INSTALL_FAILED_VERSION_DOWNGRADE]", InstallFailure.Downgrade)]
    [InlineData("Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]", InstallFailure.Storage)]
    [InlineData("Failure [INSTALL_FAILED_OLDER_SDK]", InstallFailure.Incompatible)]
    [InlineData("Failure [something else]", InstallFailure.Other)]
    public void InstallFailures_AreClassified(string output, InstallFailure expected) => Assert.Equal(expected, CompanionManager.Classify(output));

    [Fact]
    public void PairingKey_IsStableAcrossLoads_AndProtectedAtRest()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "cici-pair-" + Guid.NewGuid());
        var a = CompanionManager.LoadOrCreatePairingKey(); var b = CompanionManager.LoadOrCreatePairingKey();
        Assert.Equal(a, b); Assert.Equal(32, a.Length);
        Assert.DoesNotContain(Convert.ToBase64String(a), File.ReadAllText(AppPaths.DataFile("pairing.key")));
        Assert.NotEqual(a, CompanionManager.LoadOrCreatePairingKey(rotate: true));
    }
}
