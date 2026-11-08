using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;

namespace Nothing.Desktop;

public static class Appearance
{
    private static readonly string SettingsFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NothingDesktop", "theme.txt");
    public static bool IsDark { get; private set; } = true;
    public static void Load()
    {
        var dark = true;
        try { if (System.IO.File.Exists(SettingsFile)) dark = System.IO.File.ReadAllText(SettingsFile).Trim() != "light"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Apply(dark, false);
    }
    public static void Apply(bool dark, bool persist = true)
    {
        IsDark = dark;
        var keys = new[] { "Page", "CardFill", "Raised", "Ink", "Muted", "Line", "Selected", "SelectedInk" };
        var colors = dark ? new[] { "#090909", "#1C1C1E", "#2A2A2C", "#F2F2F2", "#96969B", "#343438", "#F0F0F0", "#101010" } : new[] { "#F4F4F3", "#FFFFFF", "#E9E9E7", "#161616", "#747477", "#DADAD8", "#171717", "#FFFFFF" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        if (!persist) return;
        try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsFile)!); System.IO.File.WriteAllText(SettingsFile, dark ? "dark" : "light"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class ModeIcon : FrameworkElement
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode), typeof(object), typeof(ModeIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PenBrushProperty = DependencyProperty.Register(nameof(PenBrush), typeof(Brush), typeof(ModeIcon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public object? Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public Brush PenBrush { get => (Brush)GetValue(PenBrushProperty); set => SetValue(PenBrushProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        var scale = Math.Min(ActualWidth, ActualHeight) / 28;
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(PenBrush, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var mode = Mode?.ToString();
        if (mode == "theme")
        {
            dc.DrawEllipse(null, pen, new(14, 14), 5, 5);
            for (var i = 0; i < 8; i++) { var a = i * Math.PI / 4; dc.DrawLine(pen, new(14 + Math.Cos(a) * 9, 14 + Math.Sin(a) * 9), new(14 + Math.Cos(a) * 11, 14 + Math.Sin(a) * 11)); }
        }
        else if (mode == "7" || mode == "spatial")
        {
            dc.DrawEllipse(PenBrush, null, new(14, 14), 4, 4);
            for (var i = 0; i < 10; i++) { var a = i * Math.PI / 5; dc.DrawEllipse(PenBrush, null, new(14 + Math.Cos(a) * 9, 14 + Math.Sin(a) * 9), 1.1, 1.1); }
        }
        else
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open()) { g.BeginFigure(new(7, 21), false, false); g.ArcTo(new(21, 21), new(10, 10), 0, true, SweepDirection.Clockwise, true, false); }
            dc.DrawGeometry(null, pen, geometry);
            if (mode == "5") dc.DrawLine(pen, new(5, 4), new(24, 24));
            else dc.DrawEllipse(null, pen, new(14, 14), 3.4, 3.4);
        }
        dc.Pop();
    }
}

public sealed class MiniBattery : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(MiniBattery), new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ChargingProperty = DependencyProperty.Register(nameof(Charging), typeof(bool), typeof(MiniBattery), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PenBrushProperty = DependencyProperty.Register(nameof(PenBrush), typeof(Brush), typeof(MiniBattery), new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool Charging { get => (bool)GetValue(ChargingProperty); set => SetValue(ChargingProperty, value); }
    public Brush PenBrush { get => (Brush)GetValue(PenBrushProperty); set => SetValue(PenBrushProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 16));
        var brush = Level is >= 0 and < 20 ? Brushes.OrangeRed : PenBrush;
        dc.DrawRoundedRectangle(null, new Pen(brush, 1.2), new Rect(1, 3, 19, 10), 2, 2);
        dc.DrawRoundedRectangle(brush, null, new Rect(21, 6, 2, 4), 0.5, 0.5);
        if (Level >= 0) dc.DrawRoundedRectangle(brush, null, new Rect(3, 5, 15 * Math.Clamp(Level / 100, 0, 1), 6), 1, 1);
        if (Charging) { var bolt = Geometry.Parse("M 13,1 L 8,8 L 12,8 L 9,15 L 16,6 L 12,6 Z"); dc.DrawGeometry(Brushes.White, new Pen(brush, 0.5), bolt); }
        dc.Pop();
    }
}
