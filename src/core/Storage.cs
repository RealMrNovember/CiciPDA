using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CiciPDA;

public enum ConnectionMethod { PhoneVpn, Usb, AndroidHotspot, IphoneHotspot }
public enum TransportPref { Auto, Usb, Wifi }

public sealed class AppSettings
{
    public string Language { get; set; } = "en";            // en | tr
    public string Theme { get; set; } = "Graphite";           // Graphite | Midnight
    public ConnectionMethod LastMethod { get; set; } = ConnectionMethod.PhoneVpn;
    public TransportPref Transport { get; set; } = TransportPref.Auto;
    public string? PreferredDeviceSerial { get; set; }
    public bool AutoConnect { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectAttempts { get; set; } = 3;
    public int ReconnectDelaySec { get; set; } = 5;
    public string AdbPath { get; set; } = "";
    public string DnsPrimary { get; set; } = "1.1.1.1";
    public string DnsSecondary { get; set; } = "1.0.0.1";
    public int Mtu { get; set; } = 1400;
    public bool Ipv6LeakProtection { get; set; } = true;
    public bool ForceDnsThroughTunnel { get; set; } = true;     // NRPT catch-all rule: only the tunnel's DNS servers answer while connected
    public bool UseCompanion { get; set; } = true;            // install/use the signed Android companion app (recommended)
    public bool AutoUpdateCompanion { get; set; } = true;     // check the signed release channel for a newer companion
    public bool AllowLegacyFallback { get; set; }              // fall back to the no-install shell helper if the APK cannot be installed
    public bool ShareWifi { get; set; }
    public string HotspotSsid { get; set; } = "CiciPDA";
    public string HotspotPasswordProtected { get; set; } = "";   // DPAPI, never plain text
    public long DataWarningMb { get; set; } = 10240;
    public long DataCriticalMb { get; set; } = 20480;
    public bool AutoCheckUpdates { get; set; } = true;       // check the signed release channel for a new CiciPDA
    public bool StartMinimized { get; set; }
    public string LogLevel { get; set; } = "Info";            // Debug | Info | Warning | Error
}

public static class Secret
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        return Convert.ToBase64String(Dpapi(Encoding.UTF8.GetBytes(plain), true));
    }

    public static string Unprotect(string protectedB64)
    {
        if (string.IsNullOrEmpty(protectedB64)) return "";
        try { return Encoding.UTF8.GetString(Dpapi(Convert.FromBase64String(protectedB64), false)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct DataBlob { public int cbData; public IntPtr pbData; }
    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool CryptProtectData(ref DataBlob i, string? d, IntPtr e, IntPtr r, IntPtr p, int f, out DataBlob o);
    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DataBlob i, IntPtr d, IntPtr e, IntPtr r, IntPtr p, int f, out DataBlob o);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

    /// <summary>Windows DPAPI (current user scope) without an extra package.</summary>
    static byte[] Dpapi(byte[] data, bool protect)
    {
        var m = System.Runtime.InteropServices.Marshal.AllocHGlobal(data.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(data, 0, m, data.Length);
            var inp = new DataBlob { cbData = data.Length, pbData = m };
            bool ok = protect ? CryptProtectData(ref inp, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var o1) : CryptUnprotectData(ref inp, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out o1);
            if (!ok) throw new CryptographicException(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            var res = new byte[o1.cbData];
            System.Runtime.InteropServices.Marshal.Copy(o1.pbData, res, 0, res.Length);
            LocalFree(o1.pbData);
            return res;
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(m); }
    }

    /// <summary>Random WPA2-safe passphrase (no ambiguous characters).</summary>
    public static string GeneratePassphrase(int len = 14)
    {
        const string a = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var sb = new StringBuilder();
        for (int i = 0; i < len; i++) sb.Append(a[RandomNumberGenerator.GetInt32(a.Length)]);
        return sb.ToString();
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public static T LoadOrNew<T>(string path) where T : new()
    {
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Opts) ?? new T(); }
        catch (Exception ex) { Log.Warn("storage", $"Could not read {Path.GetFileName(path)}: {ex.Message}"); }
        return new T();
    }

    /// <summary>Atomic write: temp file then replace, so a crash never leaves a half-written file.</summary>
    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opts));
        File.Move(tmp, path, true);
    }
}

public sealed class SettingsStore
{
    readonly string _path = AppPaths.DataFile("settings.json");
    public AppSettings Current { get; private set; }
    public event Action? Changed;
    public SettingsStore() { Current = Json.LoadOrNew<AppSettings>(_path); Apply(); }
    public void Save() { try { Json.Save(_path, Current); } catch (Exception ex) { Log.Error("settings", ex, "Save failed"); } Apply(); Changed?.Invoke(); }
    void Apply() => Log.MinFileLevel = Enum.TryParse<Severity>(Current.LogLevel, true, out var s) ? s : Severity.Info;
}

public sealed class SessionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public ConnectionMethod Method { get; set; }
    public string Device { get; set; } = "";
    public string Interface { get; set; } = "";
    public long BytesDown { get; set; }
    public long BytesUp { get; set; }
    public string Result { get; set; } = "";     // Completed | Failed | Interrupted
    public string? Error { get; set; }
    [JsonIgnore] public TimeSpan Duration => End - Start;
}

public sealed class HistoryStore
{
    readonly string _path = AppPaths.DataFile("sessions.json");
    readonly List<SessionRecord> _items;
    public event Action? Changed;
    public HistoryStore() { _items = Json.LoadOrNew<List<SessionRecord>>(_path); }
    public IReadOnlyList<SessionRecord> Items => _items.OrderByDescending(s => s.Start).ToList();

    public void Add(SessionRecord r)
    {
        _items.Add(r);
        if (_items.Count > 500) _items.RemoveRange(0, _items.Count - 500);
        try { Json.Save(_path, _items); } catch (Exception ex) { Log.Error("history", ex, "Save failed"); }
        Changed?.Invoke();
    }

    public void Clear() { _items.Clear(); try { Json.Save(_path, _items); } catch { } Changed?.Invoke(); }
}

public sealed class KnownDevice
{
    public string Serial { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string Transport { get; set; } = "USB";    // USB | Wi-Fi | Emulator
    public bool Trusted { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public int Connections { get; set; }
}

public sealed class DeviceStore
{
    readonly string _path = AppPaths.DataFile("devices.json");
    readonly List<KnownDevice> _items;
    public event Action? Changed;
    public DeviceStore() { _items = Json.LoadOrNew<List<KnownDevice>>(_path); }
    public IReadOnlyList<KnownDevice> Items => _items.OrderByDescending(d => d.LastSeen).ToList();

    public void Observe(AdbDevice d)
    {
        var k = _items.FirstOrDefault(x => x.Serial == d.Serial);
        if (k == null) { k = new KnownDevice { Serial = d.Serial, FirstSeen = DateTimeOffset.Now }; _items.Add(k); }
        k.LastSeen = DateTimeOffset.Now;
        k.Name = d.DisplayName; k.Model = d.Model; k.Transport = d.TransportName;
        k.Trusted = d.State == "device";
        Save();
    }

    public void CountConnection(string serial) { var k = _items.FirstOrDefault(x => x.Serial == serial); if (k != null) { k.Connections++; Save(); } }
    public void Forget(string serial) { _items.RemoveAll(x => x.Serial == serial); Save(); }
    void Save() { try { Json.Save(_path, _items); } catch (Exception ex) { Log.Error("devices", ex, "Save failed"); } Changed?.Invoke(); }
}
