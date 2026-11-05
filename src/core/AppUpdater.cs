using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CiciPDA;

public sealed class UpdateManifest
{
    [JsonPropertyName("product")] public string Product { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("installer")] public string Installer { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
}

public sealed record UpdateInfo(UpdateManifest Manifest, Version Version, Uri BaseUri);

/// <summary>
/// Windows-app self-update. The release channel serves update-windows-x64.json + .sig + the installer.
/// The signature on the manifest (pinned ECDSA release key) is verified before anything else is trusted or downloaded;
/// the installer must then match the signed SHA-256. The installer is run only after the user confirms.
/// </summary>
public sealed class AppUpdater
{
    public const string DefaultChannel = "https://github.com/RealMrNovember/CiciPDA/releases/latest/download/";
    public const string ProductName = "CiciPDA";
    /// <summary>Per-platform manifest name. Only Windows x64 exists; there are no Linux or macOS builds.</summary>
    public const string ManifestName = "update-windows-x64.json";
    readonly string _channel;
    readonly string _pinnedKey;

    public AppUpdater(string? channel = null, string? pinnedKey = null) { _channel = channel ?? DefaultChannel; _pinnedKey = pinnedKey ?? ReleaseTrust.PinnedMetadataKey; }

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(AppUpdater).Assembly.GetName().Version;
            var exe = Environment.ProcessPath is { } p && File.Exists(p) ? FileVersionInfo.GetVersionInfo(p).FileVersion : null;
            return Version.TryParse(exe, out var ev) ? Normalize(ev) : Normalize(v ?? new Version(0, 0, 0));
        }
    }

    static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    static void RequireTrustedChannel(Uri u)
    {
        if (u.Scheme != Uri.UriSchemeHttps || !(u.Host == "github.com" || u.Host.EndsWith(".github.com") || u.Host.EndsWith(".githubusercontent.com")))
            throw new TrustException("The update channel must be an HTTPS GitHub address.");
    }

    static HttpClient NewHttp() => new(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Pure verification step (testable): signature first, then field sanity.</summary>
    public static UpdateManifest VerifyManifest(byte[] json, byte[] sig, string pinnedKey)
    {
        using (var e = ECDsa.Create())
        {
            e.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pinnedKey), out _);
            if (!e.VerifyData(json, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new TrustException("The update information is not signed by the CiciPDA release key.");
        }
        var span = json.AsSpan();
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) span = span[3..];
        UpdateManifest m;
        try { m = JsonSerializer.Deserialize<UpdateManifest>(span) ?? throw new TrustException("Empty update information."); }
        catch (JsonException ex) { throw new TrustException("Update information is malformed: " + ex.Message); }
        if (m.Product != ProductName) throw new TrustException("The update is for a different product.");
        if (!Version.TryParse(m.Version, out _)) throw new TrustException("The update has no valid version.");
        if (m.Installer != Path.GetFileName(m.Installer) || !m.Installer.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new TrustException("The update names an unsafe installer file.");
        if (m.Sha256.Length != 64 || !m.Sha256.All(Uri.IsHexDigit)) throw new TrustException("The update has no valid checksum.");
        return m;
    }

    /// <summary>Returns null when already up to date.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct, Version? current = null)
    {
        var baseUri = new Uri(_channel); RequireTrustedChannel(baseUri);
        using var http = NewHttp();
        var json = await http.GetByteArrayAsync(new Uri(baseUri, ManifestName), ct);
        var sig = await http.GetByteArrayAsync(new Uri(baseUri, ManifestName + ".sig"), ct);
        if (json.Length > 16_384 || sig.Length > 256) throw new TrustException("Update information is unexpectedly large.");
        var m = VerifyManifest(json, sig, _pinnedKey);
        var v = Version.Parse(m.Version);
        return v > (current ?? CurrentVersion) ? new UpdateInfo(m, v, baseUri) : null;
    }

    /// <summary>Downloads to a private temp folder and verifies the signed checksum. Returns the verified installer path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        RequireTrustedChannel(info.BaseUri);
        var dir = Path.Combine(Path.GetTempPath(), "CiciPDA-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, info.Manifest.Installer);
        using var http = NewHttp();
        using var r = await http.GetAsync(new Uri(info.BaseUri, info.Manifest.Installer), HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        long total = r.Content.Headers.ContentLength ?? info.Manifest.Size;
        if (total > 500_000_000) throw new TrustException("The installer is unexpectedly large.");
        await using (var src = await r.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(file))
        {
            var buf = new byte[81920]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n), ct); done += n; if (total > 0) progress?.Report((double)done / total); }
        }
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(file), ct)).ToLowerInvariant();
        if (actual != info.Manifest.Sha256.ToLowerInvariant()) { try { File.Delete(file); } catch { } throw new TrustException("The downloaded installer does not match the signed checksum and was discarded."); }
        return file;
    }

    /// <summary>Starts the verified installer (UAC prompt). The caller must then shut the application down.</summary>
    public static void LaunchInstaller(string installerPath)
    {
        // Constant, non-user-controlled switches only.
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS") { UseShellExecute = true, Verb = "runas" });
    }
}
