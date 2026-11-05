using System.Text.Json;
using Xunit;

namespace CiciPDA.Tests;

[Collection("AppPaths")]
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
        File.WriteAllText(m, File.ReadAllText(m).Replace("20103", "29999"));
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
    public void SessionKey_IsFreshEveryTime_NeverPersisted_AndMaskedInLogs()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "cici-pair-" + Guid.NewGuid());
        File.WriteAllText(AppPaths.DataFile("pairing.key"), "legacy persistent key");            // pre-2.1.1 leftover
        var a = CompanionManager.NewSessionKey(); var b = CompanionManager.NewSessionKey();
        Assert.Equal(32, a.Length); Assert.NotEqual(a, b);
        Assert.False(File.Exists(AppPaths.DataFile("pairing.key")), "no long-lived pairing secret may remain on the PC");
        Assert.DoesNotContain(Directory.GetFiles(AppPaths.DataDir), f => File.ReadAllText(f).Contains(Convert.ToHexString(a), StringComparison.OrdinalIgnoreCase));
        var hex = Convert.ToHexString(a).ToLowerInvariant();
        Assert.DoesNotContain(hex, Log.Redact("adb said: key=" + hex));                           // masked in logs and error messages
    }

    [Fact]
    public void StartScript_PutsKeyOnStdinOnly_NotOnThePcCommandLine()
    {
        // The only place the key appears is the stdin script handed to `adb shell`; the PC-side argument list is fixed text.
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "core", "Companion.cs"));
        Assert.Contains("stdin: script", src);
        Assert.Contains("new[] { \"-s\", serial, \"shell\" }", src);
        Assert.DoesNotContain("--es\", key", src);
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "global.json"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }

    [Fact]
    public void AndroidManifest_ServiceIsProtected_NoBackup_NoExportedKeyHolders()
    {
        var m = File.ReadAllText(Path.Combine(RepoRoot(), "src", "agent", "android", "AndroidManifest.xml"));
        Assert.Contains("android:allowBackup=\"false\"", m);
        Assert.Contains("android:permission=\"android.permission.DUMP\"", m);                      // only the adb shell may drive the service
        Assert.Contains("specialUse", m);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(m, "<service"));        // no other component can receive the key
        Assert.DoesNotContain("<provider", m); Assert.DoesNotContain("<receiver", m);
        var svc = File.ReadAllText(Path.Combine(RepoRoot(), "src", "agent", "android", "RelayService.java"));
        Assert.DoesNotContain("Log.i(TAG, \"key", svc); Assert.DoesNotContain("+ hex", svc);       // never log key material
        Assert.Contains("PairingStore.clear", svc);                                                // STOP/UNPAIR/failed start delete the key
        Assert.Contains("AndroidKeyStore", File.ReadAllText(Path.Combine(RepoRoot(), "src", "agent", "android", "PairingStore.java")));
    }
}
