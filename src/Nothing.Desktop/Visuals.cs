using System.Windows;
using System.Windows.Media;

namespace Nothing.Desktop;

public partial class HeadphoneArt : System.Windows.Controls.UserControl
{
    public HeadphoneArt() => InitializeComponent();
}

public sealed class DotMatrix : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(DotMatrix), new FrameworkPropertyMetadata("NOTHING", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty DotColorProperty = DependencyProperty.Register(nameof(DotColor), typeof(Brush), typeof(DotMatrix), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush DotColor { get => (Brush)GetValue(DotColorProperty); set => SetValue(DotColorProperty, value); }
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['N']=["10001","11001","11001","10101","10011","10011","10001"],
        ['O']=["01110","10001","10001","10001","10001","10001","01110"],
        ['T']=["11111","00100","00100","00100","00100","00100","00100"],
        ['H']=["10001","10001","10001","11111","10001","10001","10001"],
        ['I']=["11111","00100","00100","00100","00100","00100","11111"],
        ['G']=["01111","10000","10000","10111","10001","10001","01110"]
    };
    protected override Size MeasureOverride(Size availableSize) => new(Text.Length * 22, 25);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var step = Math.Min(ActualHeight / 7, ActualWidth / Math.Max(1, Text.Length * 6));
        var index = 0;
        foreach (var character in Text.ToUpperInvariant())
        {
            if (Glyphs.TryGetValue(character, out var rows))
                for (var y = 0; y < 7; y++) for (var x = 0; x < 5; x++)
                    if (rows[y][x] == '1') dc.DrawEllipse(DotColor, null, new Point((index * 6 + x + 0.5) * step, (y + 0.5) * step), step * 0.32, step * 0.32);
            index++;
        }
    }
}

public sealed class BatteryGauge : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(BatteryGauge), new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var center = new Point(ActualWidth / 2, ActualHeight / 2); var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 13;
        if (radius < 10) return;
        var enabled = Level < 0 ? 0 : (int)Math.Round(Math.Clamp(Level, 0, 100) * 0.6);
        for (var i = 0; i < 60; i++)
        {
            var a = (i * 6 - 90) * Math.PI / 180;
            var brush = i < enabled ? new SolidColorBrush(Level < 20 ? Color.FromRgb(217, 45, 39) : Color.FromRgb(42, 48, 39)) : new SolidColorBrush(Color.FromRgb(222, 226, 215));
            var pen = new Pen(brush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new(center.X + Math.Cos(a) * (radius - 10), center.Y + Math.Sin(a) * (radius - 10)), new(center.X + Math.Cos(a) * radius, center.Y + Math.Sin(a) * radius));
        }
    }
}
