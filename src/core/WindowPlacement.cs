namespace CiciPDA;

/// <summary>A rectangle in WPF device-independent units (kept free of WPF types so it can be unit-tested).</summary>
public readonly record struct Box(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>Remembers where the user put the window and makes sure it is never restored somewhere unreachable (monitor unplugged, resolution changed).</summary>
public static class WindowPlacement
{
    public const double MinWidth = 1100, MinHeight = 680;

    /// <summary>
    /// Returns the saved box adjusted to the current desktop, or null when nothing usable was saved (the caller then centres the window).
    /// The title bar must be reachable: at least 140x40 DIPs of the top-left area has to lie inside the virtual screen. Size is clamped to the
    /// screen and to the window's minimum size.
    /// </summary>
    public static Box? Fit(double? left, double? top, double? width, double? height, Box virtualScreen)
    {
        if (left is null || top is null || width is null || height is null) return null;
        if (double.IsNaN(left.Value) || double.IsNaN(top.Value) || double.IsNaN(width.Value) || double.IsNaN(height.Value)) return null;
        double w = Math.Clamp(width.Value, MinWidth, Math.Max(MinWidth, virtualScreen.Width));
        double h = Math.Clamp(height.Value, MinHeight, Math.Max(MinHeight, virtualScreen.Height));
        double l = left.Value, t = top.Value;

        // reachable title bar: overlap of the window's top strip with the virtual screen
        double overlapX = Math.Min(l + w, virtualScreen.Right) - Math.Max(l, virtualScreen.Left);
        double overlapY = Math.Min(t + 40, virtualScreen.Bottom) - Math.Max(t, virtualScreen.Top);
        if (overlapX < 140 || overlapY < 40) return null;                       // would be (almost) off screen: let the caller centre it

        // keep the whole window on the desktop when it only sticks out a little
        if (l + w > virtualScreen.Right) l = Math.Max(virtualScreen.Left, virtualScreen.Right - w);
        if (t + h > virtualScreen.Bottom) t = Math.Max(virtualScreen.Top, virtualScreen.Bottom - h);
        if (l < virtualScreen.Left) l = virtualScreen.Left;
        if (t < virtualScreen.Top) t = virtualScreen.Top;
        return new Box(l, t, w, h);
    }
}
