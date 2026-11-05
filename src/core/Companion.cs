using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CiciPDA;

public enum CompanionStatus { Unknown, NotInstalled, Outdated, UpToDate, NewerThanBundled }

public sealed record CompanionInspection(string Serial, string Model, string AndroidVersion, int Sdk, bool Installed, int InstalledCode, string InstalledName, CompanionStatus Status, bool NotificationsGranted);

public enum InstallFailure { None, UserRestricted, SignatureMismatch, Downgrade, Storage, Incompatible, Other }
public sealed record InstallResult(bool Ok, InstallFailure Failure, string Message);

/// <summary>Everything needed to get the Android companion onto a phone and running: inspect, verify, install/update, start, check ready.</summary>
public sealed class CompanionManager
{
    readonly AdbService _adb;
    readonly Func<AppSettings> _settings;
    public CompanionManager(AdbService adb, Func<AppSettings> settings) { _adb = adb; _settings = settings; }

    public static string BundledDir => Path.Combine(AppPaths.BaseDir, "companion");
    public static string UpdateDir => Path.Combine(AppPaths.DataDir, "companion");

    // ---------- release selection ----------
    /// <summary>Returns the newest companion that passes full verification from the downloaded-update folder or the installer-bundled folder.</summary>
    public VerifiedCompanion LoadBestRelease()
    {
        var found = new List<VerifiedCompanion>(); var errors = new List<string>();
        foreach (var (dir, src) in new[] { (UpdateDir, "update"), (BundledDir, "bundled") })
        {
            var meta = Path.Combine(dir, "companion.json"); var sig = meta + ".sig";
            if (!File.Exists(meta) || !File.Exists(sig)) continue;
            try { found.Add(ReleaseTrust.Verify(meta, sig, dir, src)); }
            catch (Exception ex) when (ex is TrustException or JsonException or FormatException or CryptographicException or IOException)
            { errors.Add($"{src}: {ex.Message}"); Log.Warn("companion", $"Rejected {src} companion release: {ex.Message}"); }
        }
        if (found.Count == 0) throw new TrustException("No trusted companion APK is available. " + string.Join(" ", errors));
        return found.OrderByDescending(f => f.Meta.VersionCode).First();
    }

    /// <summary>Checks the controlled release channel (HTTPS, pinned host) for a newer companion and stores it only if it verifies.</summary>
    public async Task<VerifiedCompanion?> DownloadUpdateAsync(int currentCode, CancellationToken ct, string baseUrl = "https://github.com/RealMrNovember/CiciPDA/releases/latest/download/")
    {
        var u = new Uri(baseUrl);
        if (u.Scheme != Uri.UriSchemeHttps || !(u.Host == "github.com" || u.Host.EndsWith(".github.com") || u.Host.EndsWith(".githubusercontent.com")))
            throw new TrustException("Update channel is not an allowed HTTPS host.");
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 4 }) { Timeout = TimeSpan.FromSeconds(30) };
        var tmp = Path.Combine(Path.GetTempPath(), "cici-upd-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            var metaBytes = await http.GetByteArrayAsync(new Uri(u, "companion.json"), ct);
            var sig = await http.GetByteArrayAsync(new Uri(u, "companion.json.sig"), ct);
            if (metaBytes.Length > 16_384 || sig.Length > 256) throw new TrustException("Update metadata is unexpectedly large.");
            ReleaseTrust.VerifyMetadataSignature(metaBytes, sig);            // verify BEFORE trusting any field or downloading the APK
            var meta = ReleaseTrust.ParseMetadata(metaBytes);
            if (meta.VersionCode <= currentCode) return null;
            if (meta.Apk != Path.GetFileName(meta.Apk)) throw new TrustException("Unsafe APK name in metadata.");
            using (var r = await http.GetAsync(new Uri(u, meta.Apk), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                r.EnsureSuccessStatusCode();
                if (r.Content.Headers.ContentLength is > 60_000_000) throw new TrustException("APK is unexpectedly large.");
                await using var fs = File.Create(Path.Combine(tmp, meta.Apk));
                await r.Content.CopyToAsync(fs, ct);
            }
            File.WriteAllBytes(Path.Combine(tmp, "companion.json"), metaBytes);
            File.WriteAllBytes(Path.Combine(tmp, "companion.json.sig"), sig);
            var v = ReleaseTrust.Verify(Path.Combine(tmp, "companion.json"), Path.Combine(tmp, "companion.json.sig"), tmp, "download");
            Directory.CreateDirectory(UpdateDir);
            foreach (var f in Directory.GetFiles(UpdateDir)) File.Delete(f);
            foreach (var f in Directory.GetFiles(tmp)) File.Copy(f, Path.Combine(UpdateDir, Path.GetFileName(f)), true);
            Log.Ok("companion", $"Downloaded and verified companion {v.Meta.VersionName}.");
            return v with { ApkPath = Path.Combine(UpdateDir, meta.Apk) };
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    // ---------- device inspection ----------
    string Adb => _adb.AdbPath ?? _adb.Locate() ?? throw new FileNotFoundException("adb.exe was not found.");

    Task<ProcResult> Shell(string serial, CancellationToken ct, params string[] cmd) =>
        ProcessRunner.RunAsync(Adb, new[] { "-s", serial, "shell" }.Concat(cmd), TimeSpan.FromSeconds(20), ct);

    static readonly Regex VCode = new(@"versionCode=(\d+)", RegexOptions.Compiled);
    static readonly Regex VName = new(@"versionName=(\S+)", RegexOptions.Compiled);

    public async Task<CompanionInspection> InspectAsync(string serial, VerifiedCompanion release, CancellationToken ct)
    {
        string Prop(ProcResult r) => r.Stdout.Trim();
        var model = Prop(await Shell(serial, ct, "getprop", "ro.product.model"));
        var ver = Prop(await Shell(serial, ct, "getprop", "ro.build.version.release"));
        int.TryParse(Prop(await Shell(serial, ct, "getprop", "ro.build.version.sdk")), out var sdk);
        var pm = await Shell(serial, ct, "pm", "path", ReleaseTrust.PackageName);
        bool installed = pm.Ok && pm.Stdout.Contains("package:");
        int code = 0; string name = ""; bool notif = true;
        if (installed)
        {
            var d = await Shell(serial, ct, "dumpsys", "package", ReleaseTrust.PackageName);
            var mc = VCode.Match(d.Stdout); var mn = VName.Match(d.Stdout);
            if (mc.Success) code = int.Parse(mc.Groups[1].Value);
            if (mn.Success) name = mn.Groups[1].Value;
            if (sdk >= 33) notif = Regex.IsMatch(d.Stdout, @"android\.permission\.POST_NOTIFICATIONS: granted=true");
        }
        var st = !installed ? CompanionStatus.NotInstalled : code < release.Meta.VersionCode ? CompanionStatus.Outdated : code == release.Meta.VersionCode ? CompanionStatus.UpToDate : CompanionStatus.NewerThanBundled;
        if (sdk > 0 && sdk < release.Meta.MinSdk) throw new InvalidOperationException($"This phone runs Android {ver} (API {sdk}); the companion needs API {release.Meta.MinSdk} or newer.");
        return new CompanionInspection(serial, model, ver, sdk, installed, code, name, st, notif);
    }

    // ---------- install ----------
    public async Task<InstallResult> InstallAsync(string serial, VerifiedCompanion release, CancellationToken ct)
    {
        // Re-verify immediately before handing the file to adb (guards against swap-after-check).
        ReleaseTrust.Verify(Path.Combine(Path.GetDirectoryName(release.ApkPath)!, "companion.json"), Path.Combine(Path.GetDirectoryName(release.ApkPath)!, "companion.json.sig"), Path.GetDirectoryName(release.ApkPath)!, release.Source);
        var r = await ProcessRunner.RunAsync(Adb, new[] { "-s", serial, "install", "-r", release.ApkPath }, TimeSpan.FromSeconds(120), ct);
        var text = r.Combined;
        if (r.Ok && text.Contains("Success")) return new InstallResult(true, InstallFailure.None, "Installed");
        return new InstallResult(false, Classify(text), Explain(text));
    }

    public static InstallFailure Classify(string o) =>
        o.Contains("INSTALL_FAILED_USER_RESTRICTED") || o.Contains("INSTALL_FAILED_VERIFICATION") && o.Contains("USB") || o.Contains("Install canceled by user") ? InstallFailure.UserRestricted :
        o.Contains("UPDATE_INCOMPATIBLE") || o.Contains("INSTALL_PARSE_FAILED_INCONSISTENT_CERTIFICATES") ? InstallFailure.SignatureMismatch :
        o.Contains("VERSION_DOWNGRADE") ? InstallFailure.Downgrade :
        o.Contains("INSUFFICIENT_STORAGE") ? InstallFailure.Storage :
        o.Contains("OLDER_SDK") || o.Contains("NO_MATCHING_ABIS") ? InstallFailure.Incompatible : InstallFailure.Other;

    public static string Explain(string o) => Classify(o) switch
    {
        InstallFailure.UserRestricted => "The phone blocked the installation. On the phone, allow \"Install via USB\" (Developer options; on Xiaomi/Oppo/Vivo this is a separate switch that may need a SIM or account), then tap Install if asked, and try again.",
        InstallFailure.SignatureMismatch => "A copy of the companion signed with a different key is already installed. Uninstall \"CiciPDA Companion\" on the phone, then connect again.",
        InstallFailure.Downgrade => "The phone already has a newer companion than this PC offers. Update CiciPDA on the PC.",
        InstallFailure.Storage => "The phone has too little free storage to install the companion.",
        InstallFailure.Incompatible => "This phone's Android version or CPU is not supported by the companion.",
        _ => "Installation failed: " + (o.Length > 300 ? o[..300] : o),
    };

    public async Task UninstallAsync(string serial, CancellationToken ct) =>
        await ProcessRunner.RunAsync(Adb, new[] { "-s", serial, "uninstall", ReleaseTrust.PackageName }, TimeSpan.FromSeconds(30), ct);

    /// <summary>Opens the companion once so the user sees what it does and can grant the notification permission themselves.</summary>
    public Task ShowOnPhoneAsync(string serial, CancellationToken ct) =>
        Shell(serial, ct, "am", "start", "-n", ReleaseTrust.PackageName + "/.MainActivity");

    // ---------- session key + service ----------
    /// <summary>
    /// A fresh random key for ONE connection. Nothing is persisted on the PC: when the session ends the key is simply discarded,
    /// and the phone deletes its copy on STOP/UNPAIR, so no long-lived pairing secret exists on either side.
    /// </summary>
    public static byte[] NewSessionKey()
    {
        var k = RandomNumberGenerator.GetBytes(32);
        Log.RegisterSecret(Convert.ToHexString(k).ToLowerInvariant());      // masked in every log line from now on
        try { var old = AppPaths.DataFile("pairing.key"); if (File.Exists(old)) File.Delete(old); } catch { }   // remove the pre-2.1.1 persistent key
        return k;
    }

    string Action(string name) => $"{ReleaseTrust.PackageName}.{name}";

    /// <summary>
    /// Starts the foreground relay service. Runtime path of the key: PC memory -> adb's stdin pipe (never a PC command line)
    /// -> `am start-foreground-service ... --es key` run by the phone's shell -> Intent extra delivered to the service ->
    /// Keystore-wrapped storage on the phone. On the phone the key is visible, for the duration of that command, only to the
    /// shell/system (the same trust level as USB debugging itself); it is not reachable by ordinary apps because the service
    /// requires android.permission.DUMP from callers.
    /// </summary>
    public async Task StartServiceAsync(string serial, byte[] key, int remotePort, CancellationToken ct)
    {
        var hex = Convert.ToHexString(key).ToLowerInvariant();
        var script = $"am start-foreground-service -n {ReleaseTrust.PackageName}/.RelayService -a {Action("START")} --es key {hex} --ei port {remotePort}\nexit\n";
        var r = await ProcessRunner.RunAsync(Adb, new[] { "-s", serial, "shell" }, TimeSpan.FromSeconds(20), ct, stdin: script);
        var o = Log.Redact(r.Combined);
        if (!r.Ok || o.Contains("Error") || o.Contains("Exception") || o.Contains("not found"))
            throw new InvalidOperationException("The companion service could not be started: " + (o.Length > 300 ? o[..300] : o));
    }

    /// <summary>Stops the relay and makes the phone delete the session key.</summary>
    public Task StopServiceAsync(string serial) => SendAsync(serial, "STOP");

    /// <summary>Explicit "forget this PC": stops the service and deletes the stored key and its Keystore wrapper.</summary>
    public Task UnpairAsync(string serial) => SendAsync(serial, "UNPAIR");

    Task SendAsync(string serial, string action) =>
        ProcessRunner.RunAsync(Adb, new[] { "-s", serial, "shell", "am", "start-foreground-service", "-n", ReleaseTrust.PackageName + "/.RelayService", "-a", Action(action) }, TimeSpan.FromSeconds(10));

    /// <summary>Forward a free local port to the service and wait until it answers an authenticated status request.</summary>
    public async Task<(int localPort, string statusJson)> WaitReadyAsync(string serial, byte[] key, int remotePort, CancellationToken ct)
    {
        var fw = await ProcessRunner.RunAsync(Adb, new[] { "-s", serial, "forward", "tcp:0", $"tcp:{remotePort}" }, TimeSpan.FromSeconds(10), ct);
        if (!fw.Ok || !int.TryParse(fw.Stdout.Trim(), out var local)) throw new InvalidOperationException("adb forward failed: " + fw.Combined);
        Exception? last = null;
        for (int i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { return (local, await AgentChannel.GetStatusAsync(local, key, ct, TimeSpan.FromSeconds(3))); }
            catch (AgentConnectException ex) { last = ex; await Task.Delay(500, ct); }
        }
        await _adb.RemoveForwardAsync(serial, local);
        throw new InvalidOperationException("The companion service did not become ready. " + (last?.Message ?? "") + " Open the CiciPDA Companion app on the phone once, and check that battery optimisation is not blocking it.");
    }
}
