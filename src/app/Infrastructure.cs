using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CiciPDA.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    protected bool Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return false;
        f = v; Raise(n); return true;
    }
}

public sealed class RelayCommand : ICommand
{
    readonly Func<object?, Task> _run; readonly Func<object?, bool>? _can;
    public RelayCommand(Func<object?, Task> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) : this(p => { run(p); return Task.CompletedTask; }, can) { }
    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public async void Execute(object? p)
    {
        try { await _run(p); }
        catch (Exception ex) { Log.Error("ui", ex, "Command failed"); Notifier.Show(Severity.Error, ex.Message); }
    }
}

public sealed record Toast(Severity Severity, string Text);

public static class Notifier
{
    public static event Action<Toast>? Shown;
    public static void Show(Severity s, string text) => Shown?.Invoke(new Toast(s, text));
}

/// <summary>Palette. Brushes live in Application.Resources so every control follows the selected theme through DynamicResource.</summary>
public static class Theme
{
    static Color C(string h) => (Color)ColorConverter.ConvertFromString(h);

    public static void Apply(string name)
    {
        var r = Application.Current.Resources;
        bool mid = name == "Midnight";
        Set(r, "Bg", mid ? "#070A14" : "#07090C");
        Set(r, "Panel", mid ? "#0C1120" : "#0C1015");
        Set(r, "Panel2", mid ? "#111933" : "#121820");
        Set(r, "Border", mid ? "#1D2848" : "#1C2631");
        Set(r, "BorderHi", mid ? "#2B3A66" : "#2A394A");
        Set(r, "Text", "#E7EEF5");
        Set(r, "Muted", mid ? "#7D8DB3" : "#7B8CA1");
        Set(r, "Dim", mid ? "#4C5A80" : "#4A586A");
        Set(r, "Green", "#19E08B");
        Set(r, "Cyan", "#22D3EE");
        Set(r, "Red", "#FF5C6C");
        Set(r, "Amber", "#F5B84B");
        Set(r, "OnAccent", "#04110B");
        r["GreenGlow"] = new SolidColorBrush(Color.FromArgb(40, 25, 224, 139));
        r["AccentGradient"] = new LinearGradientBrush(C("#19E08B"), C("#22D3EE"), 0);
        r["ColGreen"] = C("#19E08B"); r["ColCyan"] = C("#22D3EE"); r["ColRed"] = C("#FF5C6C"); r["ColAmber"] = C("#F5B84B");
        r["ColMuted"] = C(mid ? "#7D8DB3" : "#7B8CA1"); r["ColBorder"] = C(mid ? "#1D2848" : "#1C2631");
    }

    static void Set(ResourceDictionary r, string key, string hex)
    {
        var b = new SolidColorBrush(C(hex)); b.Freeze(); r[key] = b;
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>English / Turkish UI strings. Missing keys fall back to English, then to the key itself.</summary>
public sealed class Loc : Observable
{
    string _lang = "en";
    public string Language { get => _lang; set { if (Set(ref _lang, value)) Raise("Item[]"); } }
    public string this[string key] => (_lang == "tr" && Tr.TryGetValue(key, out var t)) ? t : En.TryGetValue(key, out var e) ? e : key;

    static readonly Dictionary<string, string> En = new()
    {
        ["nav.connect"] = "Connect", ["nav.monitor"] = "Live Monitor", ["nav.activity"] = "Activity", ["nav.history"] = "Connection History",
        ["nav.devices"] = "Devices", ["nav.diagnostics"] = "Network Diagnostics", ["nav.settings"] = "Settings", ["nav.help"] = "Help & About",
        ["st.Offline"] = "Offline", ["st.DetectingDevice"] = "Detecting Device", ["st.DeviceConnected"] = "Device Connected", ["st.EstablishingTunnel"] = "Establishing Tunnel",
        ["st.Connecting"] = "Connecting", ["st.Connected"] = "Connected", ["st.Reconnecting"] = "Reconnecting", ["st.Disconnecting"] = "Disconnecting",
        ["st.Error"] = "Connection Error", ["st.Limited"] = "Limited Connectivity",
        ["btn.connect"] = "CONNECT", ["btn.disconnect"] = "DISCONNECT", ["btn.cancel"] = "CANCEL",
        ["sec.method"] = "Choose connection method", ["sec.prepare"] = "Prepare your phone",
        ["m.vpn"] = "Phone VPN", ["m.vpn.d"] = "Windows virtual adapter (Wintun) routed through an encrypted channel to your phone's own connection. USB or ADB over Wi-Fi.",
        ["m.usb"] = "USB Connection", ["m.usb.d"] = "Same encrypted tunnel, restricted to a USB data cable. Needs USB debugging.",
        ["m.and"] = "Android Wi-Fi Hotspot", ["m.and.d"] = "Join your phone's hotspot from Windows. CiciPDA detects the network, verifies the internet and monitors it. No tunnel.",
        ["m.ios"] = "iPhone Personal Hotspot", ["m.ios.d"] = "Detects an iPhone hotspot (Wi-Fi) or Apple USB Ethernet adapter, then verifies and monitors. No tunnel.",
        ["rec"] = "RECOMMENDED", ["ready"] = "Ready", ["setup"] = "Needs setup",
        ["lbl.status"] = "Status", ["lbl.method"] = "Method", ["lbl.device"] = "Device", ["lbl.duration"] = "Duration", ["lbl.vpn"] = "Tunnel", ["lbl.internet"] = "Internet", ["lbl.iface"] = "Interface",
        ["traffic"] = "Live traffic", ["down"] = "Download", ["up"] = "Upload", ["totdown"] = "Downloaded", ["totup"] = "Uploaded", ["total"] = "Total", ["latency"] = "Latency", ["loss"] = "Probe loss", ["signal"] = "Signal",
        ["empty.traffic"] = "Connect to see live traffic.", ["empty.activity"] = "No events yet.", ["empty.history"] = "No sessions recorded yet.", ["empty.devices"] = "No devices detected.",
        ["admin.needed"] = "Administrator rights are needed to create the virtual adapter.", ["admin.restart"] = "Restart as administrator",
        ["set.save"] = "Save changes", ["set.title"] = "Settings", ["diag.runall"] = "Run all tests", ["diag.title"] = "Network Diagnostics",
        ["dev.refresh"] = "Refresh", ["dev.restart"] = "Restart ADB", ["hist.clear"] = "Clear history", ["act.clear"] = "Clear",
        ["lang"] = "Language", ["theme"] = "Theme",
    };

    static readonly Dictionary<string, string> Tr = new()
    {
        ["nav.connect"] = "Bağlan", ["nav.monitor"] = "Canlı İzleme", ["nav.activity"] = "Etkinlik", ["nav.history"] = "Bağlantı Geçmişi",
        ["nav.devices"] = "Cihazlar", ["nav.diagnostics"] = "Ağ Tanılama", ["nav.settings"] = "Ayarlar", ["nav.help"] = "Yardım ve Hakkında",
        ["st.Offline"] = "Çevrimdışı", ["st.DetectingDevice"] = "Cihaz Aranıyor", ["st.DeviceConnected"] = "Cihaz Bağlı", ["st.EstablishingTunnel"] = "Tünel Kuruluyor",
        ["st.Connecting"] = "Bağlanıyor", ["st.Connected"] = "Bağlı", ["st.Reconnecting"] = "Yeniden Bağlanıyor", ["st.Disconnecting"] = "Bağlantı Kesiliyor",
        ["st.Error"] = "Bağlantı Hatası", ["st.Limited"] = "Sınırlı Bağlantı",
        ["btn.connect"] = "BAĞLAN", ["btn.disconnect"] = "BAĞLANTIYI KES", ["btn.cancel"] = "İPTAL",
        ["sec.method"] = "Bağlantı yöntemini seçin", ["sec.prepare"] = "Telefonunuzu hazırlayın",
        ["m.vpn"] = "Telefon VPN", ["m.vpn.d"] = "Wintun sanal adaptörü, telefonunuzun kendi bağlantısına şifreli kanaldan yönlendirilir. USB veya Wi-Fi üzerinden ADB.",
        ["m.usb"] = "USB Bağlantısı", ["m.usb.d"] = "Aynı şifreli tünel, yalnızca USB veri kablosu ile. USB hata ayıklama gerekir.",
        ["m.and"] = "Android Wi-Fi Erişim Noktası", ["m.and.d"] = "Telefonunuzun erişim noktasına Windows'tan katılın. CiciPDA ağı algılar, interneti doğrular ve izler. Tünel yok.",
        ["m.ios"] = "iPhone Kişisel Erişim Noktası", ["m.ios.d"] = "iPhone erişim noktasını (Wi-Fi) veya Apple USB Ethernet adaptörünü algılar, doğrular ve izler. Tünel yok.",
        ["rec"] = "ÖNERİLEN", ["ready"] = "Hazır", ["setup"] = "Kurulum gerekli",
        ["lbl.status"] = "Durum", ["lbl.method"] = "Yöntem", ["lbl.device"] = "Cihaz", ["lbl.duration"] = "Süre", ["lbl.vpn"] = "Tünel", ["lbl.internet"] = "İnternet", ["lbl.iface"] = "Arayüz",
        ["traffic"] = "Canlı trafik", ["down"] = "İndirme", ["up"] = "Yükleme", ["totdown"] = "İndirilen", ["totup"] = "Yüklenen", ["total"] = "Toplam", ["latency"] = "Gecikme", ["loss"] = "Sınama kaybı", ["signal"] = "Sinyal",
        ["empty.traffic"] = "Canlı trafiği görmek için bağlanın.", ["empty.activity"] = "Henüz olay yok.", ["empty.history"] = "Henüz kayıtlı oturum yok.", ["empty.devices"] = "Cihaz algılanmadı.",
        ["admin.needed"] = "Sanal adaptör oluşturmak için yönetici hakları gerekir.", ["admin.restart"] = "Yönetici olarak yeniden başlat",
        ["set.save"] = "Değişiklikleri kaydet", ["set.title"] = "Ayarlar", ["diag.runall"] = "Tüm testleri çalıştır", ["diag.title"] = "Ağ Tanılama",
        ["dev.refresh"] = "Yenile", ["dev.restart"] = "ADB'yi yeniden başlat", ["hist.clear"] = "Geçmişi temizle", ["act.clear"] = "Temizle",
        ["lang"] = "Dil", ["theme"] = "Tema",
    };
}

public sealed class EqualsToVisibility : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => Equals(v?.ToString(), p?.ToString()) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
public sealed class EqualsToBool : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => Equals(v?.ToString(), p?.ToString());
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? v, Type t, object? p, CultureInfo c) => (v is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? v, Type t, object? p, CultureInfo c) => ((v is null or "") ^ Invert) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
public sealed class SeverityBrush : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        Severity.Success => Theme.Brush("Green"), Severity.Warning => Theme.Brush("Amber"), Severity.Error => Theme.Brush("Red"),
        Severity.Debug => Theme.Brush("Dim"), _ => Theme.Brush("Cyan"),
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
public sealed class StateBrush : IValueConverter   // ItemState -> brush
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        ItemState.Ok => Theme.Brush("Green"), ItemState.Warn => Theme.Brush("Amber"), ItemState.Fail => Theme.Brush("Red"), _ => Theme.Brush("Dim"),
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
public enum ItemState { Pending, Ok, Warn, Fail }

public sealed class PositiveToVisibility : IValueConverter
{
    public static readonly PositiveToVisibility Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
public sealed class InvertBool : IValueConverter
{
    public static readonly InvertBool Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is not true;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => v is not true;
}
public sealed class ResourceKeyConverter : IValueConverter
{
    public static readonly ResourceKeyConverter Instance = new();
    public object? Convert(object? v, Type t, object? p, CultureInfo c) => v is string k ? Application.Current.Resources[k] : null;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
