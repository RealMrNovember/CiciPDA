using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CiciPDA.App;

public enum OrbState { Disconnected, Preparing, Connecting, Connected, Disconnecting, Error }

/// <summary>The circular connection control. A real Button (Command, keyboard focus, Enter/Space) that draws its own state.</summary>
public sealed class ConnectOrb : Button
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(OrbState), typeof(ConnectOrb),
        new FrameworkPropertyMetadata(OrbState.Disconnected, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ConnectOrb)d).UpdateTimer()));
    public OrbState State { get => (OrbState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    readonly DispatcherTimer _t = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    double _angle, _phase;

    public ConnectOrb()
    {
        Cursor = System.Windows.Input.Cursors.Hand;
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        Template = new ControlTemplate(typeof(Button)) { VisualTree = root };
        _t.Tick += (_, _) => { _angle = (_angle + 5) % 360; _phase = (_phase + 0.03) % (Math.PI * 2); InvalidateVisual(); };
        Loaded += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _t.Stop();
        AutomationProperties.SetName(this, "Connection control");
    }

    void UpdateTimer()
    {
        bool anim = IsLoaded && State != OrbState.Disconnected && State != OrbState.Error;
        if (anim && !_t.IsEnabled) _t.Start(); else if (!anim && _t.IsEnabled) { _t.Stop(); InvalidateVisual(); }
    }

    static Color ColorFor(OrbState s) => s switch
    {
        OrbState.Connected => (Color)Application.Current.Resources["ColGreen"],
        OrbState.Error => (Color)Application.Current.Resources["ColRed"],
        OrbState.Disconnected => (Color)Application.Current.Resources["ColMuted"],
        _ => (Color)Application.Current.Resources["ColCyan"],
    };

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight; if (w < 20) return;
        var c = new Point(w / 2, h / 2); double R = Math.Min(w, h) / 2 - 10;
        var col = ColorFor(State);
        var panel = (Brush)Application.Current.Resources["Panel2"];
        var border = (Color)Application.Current.Resources["ColBorder"];
        bool busy = State is OrbState.Preparing or OrbState.Connecting or OrbState.Disconnecting;

        // soft glow
        double glow = State == OrbState.Connected ? 0.16 + 0.08 * Math.Sin(_phase) : State == OrbState.Error ? 0.14 : busy ? 0.08 : IsMouseOver ? 0.07 : 0.0;
        if (glow > 0)
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb((byte)(glow * 255), col.R, col.G, col.B), Color.FromArgb(0, col.R, col.G, col.B)), null, c, R + 14, R + 14);

        dc.DrawEllipse(panel, new Pen(new SolidColorBrush(State == OrbState.Disconnected ? border : Color.FromArgb(70, col.R, col.G, col.B)), 1.5), c, R, R);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(State == OrbState.Disconnected ? (byte)90 : (byte)200, col.R, col.G, col.B)), 2.5), c, R - 12, R - 12);

        if (busy)
        {
            var g = new StreamGeometry();
            using (var x = g.Open())
            {
                double a0 = _angle * Math.PI / 180, a1 = a0 + Math.PI * 0.55; double r = R - 12;
                x.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false, false);
                x.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(col), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        }

        var pen = new Pen(new SolidColorBrush(col), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        double s = R * 0.36;
        if (State == OrbState.Connected)
        {
            var ck = new StreamGeometry();
            using (var x = ck.Open()) { x.BeginFigure(new Point(c.X - s * 0.85, c.Y + s * 0.05), false, false); x.LineTo(new Point(c.X - s * 0.2, c.Y + s * 0.65), true, true); x.LineTo(new Point(c.X + s * 0.9, c.Y - s * 0.6), true, true); }
            dc.DrawGeometry(null, pen, ck);
        }
        else if (State == OrbState.Error)
        {
            dc.DrawLine(pen, new Point(c.X - s * 0.6, c.Y - s * 0.6), new Point(c.X + s * 0.6, c.Y + s * 0.6));
            dc.DrawLine(pen, new Point(c.X + s * 0.6, c.Y - s * 0.6), new Point(c.X - s * 0.6, c.Y + s * 0.6));
        }
        else
        {
            var pw = new StreamGeometry();
            using (var x = pw.Open())
            {
                x.BeginFigure(new Point(c.X - s * 0.62, c.Y - s * 0.55), false, false);
                x.ArcTo(new Point(c.X + s * 0.62, c.Y - s * 0.55), new Size(s * 0.88, s * 0.88), 0, true, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, pen, pw);
            dc.DrawLine(pen, new Point(c.X, c.Y - s * 1.0), new Point(c.X, c.Y - s * 0.05));
        }
    }
}

/// <summary>Download/upload history chart drawn from real samples (no sample data).</summary>
public sealed class TrafficChart : FrameworkElement
{
    public static readonly DependencyProperty DownProperty = DependencyProperty.Register(nameof(Down), typeof(double[]), typeof(TrafficChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpProperty = DependencyProperty.Register(nameof(Up), typeof(double[]), typeof(TrafficChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(TrafficChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowAxisProperty = DependencyProperty.Register(nameof(ShowAxis), typeof(bool), typeof(TrafficChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public double[]? Down { get => (double[]?)GetValue(DownProperty); set => SetValue(DownProperty, value); }
    public double[]? Up { get => (double[]?)GetValue(UpProperty); set => SetValue(UpProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    public bool ShowAxis { get => (bool)GetValue(ShowAxisProperty); set => SetValue(ShowAxisProperty, value); }

    public static string Rate(double bps)
    {
        double b = bps * 8; // bits per second display for axis
        return bps < 1024 ? $"{bps:0} B/s" : bps < 1048576 ? $"{bps / 1024:0.#} KB/s" : $"{bps / 1048576:0.#} MB/s";
    }

    static double Nice(double v)
    {
        if (v <= 0) return 1024;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v))); double f = v / p;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight; if (w < 10 || h < 10) return;
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(70, 128, 150, 175)), 1);
        double left = ShowAxis ? 58 : 0, bottom = 4, top = 6;
        double pw = w - left, ph = h - top - bottom;
        var d = Down ?? Array.Empty<double>(); var u = Up ?? Array.Empty<double>();
        double max = Nice(Math.Max(d.Length > 0 ? d.Max() : 0, u.Length > 0 ? u.Max() : 0) * 1.15);
        var muted = Theme.Brush("Dim");
        var tf = new Typeface("Segoe UI");
        for (int i = 0; i <= 4; i++)
        {
            double y = top + ph * i / 4.0;
            dc.DrawLine(grid, new Point(left, y), new Point(w, y));
            if (ShowAxis && d.Length + u.Length > 0)
            {
                var ft = new FormattedText(Rate(max * (4 - i) / 4.0), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, 10, muted, 1.0);
                dc.DrawText(ft, new Point(0, Math.Min(h - 14, Math.Max(0, y - 7))));
            }
        }
        if (d.Length + u.Length < 2)
        {
            if (!string.IsNullOrEmpty(EmptyText))
            {
                var ft = new FormattedText(EmptyText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, 13, Theme.Brush("Muted"), 1.0);
                dc.DrawText(ft, new Point(left + (pw - ft.Width) / 2, top + (ph - ft.Height) / 2));
            }
            return;
        }
        Plot(dc, d, max, left, top, pw, ph, (Color)Application.Current.Resources["ColGreen"], true);
        Plot(dc, u, max, left, top, pw, ph, (Color)Application.Current.Resources["ColCyan"], false);
    }

    static void Plot(DrawingContext dc, double[] v, double max, double left, double top, double pw, double ph, Color col, bool fill)
    {
        if (v.Length < 2) return;
        const int slots = 120; double step = pw / (slots - 1);
        double x0 = left + pw - (v.Length - 1) * step;
        Point P(int i) => new(x0 + i * step, top + ph - Math.Min(1, v[i] / max) * ph);
        var line = new StreamGeometry();
        using (var c = line.Open()) { c.BeginFigure(P(0), false, false); for (int i = 1; i < v.Length; i++) c.LineTo(P(i), true, true); }
        line.Freeze();
        if (fill)
        {
            var area = new StreamGeometry();
            using (var c = area.Open())
            {
                c.BeginFigure(new Point(x0, top + ph), true, true);
                for (int i = 0; i < v.Length; i++) c.LineTo(P(i), true, false);
                c.LineTo(new Point(x0 + (v.Length - 1) * step, top + ph), true, false);
            }
            area.Freeze();
            dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(70, col.R, col.G, col.B), Color.FromArgb(0, col.R, col.G, col.B), 90), null, area);
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(col), 1.8) { LineJoin = PenLineJoin.Round }, line);
    }
}
