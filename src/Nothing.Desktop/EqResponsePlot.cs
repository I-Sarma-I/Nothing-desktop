using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Nothing.Core;

namespace Nothing.Desktop;

public sealed class EqResponsePlot : FrameworkElement
{
    private static DependencyProperty BrushProperty(string name, Brush fallback) => DependencyProperty.Register(name, typeof(Brush), typeof(EqResponsePlot), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InkBrushProperty = BrushProperty(nameof(InkBrush), Brushes.White);
    public static readonly DependencyProperty MutedBrushProperty = BrushProperty(nameof(MutedBrush), Brushes.Gray);
    public static readonly DependencyProperty GridBrushProperty = BrushProperty(nameof(GridBrush), Brushes.DimGray);
    public static readonly DependencyProperty AccentBrushProperty = BrushProperty(nameof(AccentBrush), Brushes.IndianRed);
    public Brush InkBrush { get => (Brush)GetValue(InkBrushProperty); set => SetValue(InkBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    private EqualizerProfile? curve, active;
    private bool draft;
    private double hover = -1;
    public int SampleCount { get; private set; }
    public bool HasCurve => curve != null;
    public EqResponsePlot()
    {
        MouseMove += (_, e) => { hover = e.GetPosition(this).X; InvalidateVisual(); };
        MouseLeave += (_, _) => { hover = -1; InvalidateVisual(); };
        ToolTip = "Modeled EQ response at 48 kHz, not a live audio spectrum or acoustic measurement.";
    }
    public void SetProfiles(EqualizerProfile? stored, EqualizerProfile? playing, bool isDraft) { curve = stored; active = playing; draft = isDraft; InvalidateVisual(); }
    private void Text(DrawingContext dc, string text, Point point, Brush brush, double size = 11, bool right = false)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface((FontFamily)FindResource("UiFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, right ? new(point.X - formatted.Width, point.Y) : point);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (ActualWidth < 90 || ActualHeight < 70) return;
        var box = new Rect(34, 16, ActualWidth - 45, ActualHeight - 46);
        double X(double f) => box.Left + EqualizerResponse.PositionAt(f) * box.Width;
        double Y(double db) => box.Top + (18 - db) / 36 * box.Height;
        foreach (var db in new[] { -12, 0, 12 })
        {
            dc.DrawLine(new Pen(GridBrush, db == 0 ? 1 : 0.5), new(box.Left, Y(db)), new(box.Right, Y(db)));
            Text(dc, db > 0 ? $"+{db}" : db.ToString(), new(box.Left - 8, Y(db) - 7), MutedBrush, 11, true);
        }
        foreach (var f in new[] { 20, 100, 1000, 10000, 20000 })
        {
            dc.DrawLine(new Pen(GridBrush, 0.5) { DashStyle = DashStyles.Dot }, new(X(f), box.Top), new(X(f), box.Bottom));
            var label = f >= 1000 ? $"{f / 1000}k" : f.ToString();
            Text(dc, label, new(X(f) - (f == 20000 ? 16 : 4), box.Bottom + 9), MutedBrush, 11);
        }
        if (curve == null)
        {
            Text(dc, "Connect to load EQ", new(box.Left + 12, box.Top + 10), MutedBrush, 12); SampleCount = 0; return;
        }
        var samples = Math.Clamp((int)box.Width * 2, 320, 2000); SampleCount = samples + 1;
        StreamGeometry Line(EqualizerProfile profile)
        {
            var geometry = new StreamGeometry();
            using var path = geometry.Open();
            for (var i = 0; i <= samples; i++)
            {
                var f = EqualizerResponse.FrequencyAt(i / (double)samples);
                var point = new Point(box.Left + i * box.Width / samples, Y(EqualizerResponse.Decibels(profile, f)));
                if (i == 0) path.BeginFigure(point, false, false); else path.LineTo(point, true, false);
            }
            return geometry;
        }
        dc.PushClip(new RectangleGeometry(box));
        if (active != null) dc.DrawGeometry(null, new Pen(MutedBrush, 1) { DashStyle = DashStyles.Dash }, Line(active));
        var points = Enumerable.Range(0, samples + 1).Select(i => new Point(box.Left + i * box.Width / samples, Y(EqualizerResponse.Decibels(curve, EqualizerResponse.FrequencyAt(i / (double)samples))))).ToArray();
        var area = new StreamGeometry();
        using (var g = area.Open()) { g.BeginFigure(new(box.Left, Y(0)), true, true); g.PolyLineTo(points, true, false); g.LineTo(new(box.Right, Y(0)), true, false); }
        var color = AccentBrush is SolidColorBrush solid ? solid.Color : Colors.IndianRed;
        dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(45, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90), null, area);
        dc.DrawGeometry(null, new Pen(AccentBrush, 1.8) { LineJoin = PenLineJoin.Round }, Line(curve));
        foreach (var band in curve.Bands)
        {
            var p = new Point(X(band.Frequency), Y(EqualizerResponse.Decibels(curve, band.Frequency)));
            dc.DrawEllipse(AccentBrush, new Pen(InkBrush, 1), p, 3, 3);
        }
        if (hover >= box.Left && hover <= box.Right)
        {
            var f = EqualizerResponse.FrequencyAt((hover - box.Left) / box.Width); var db = EqualizerResponse.Decibels(curve, f);
            dc.DrawLine(new Pen(MutedBrush, 1) { DashStyle = DashStyles.Dot }, new(hover, box.Top), new(hover, box.Bottom));
            dc.DrawEllipse(AccentBrush, new Pen(InkBrush, 1.5), new(hover, Y(db)), 4, 4);
            Text(dc, $"{f:0} Hz   {db:+0.0;-0.0;0.0} dB", new(Math.Clamp(hover - 55, box.Left + 4, Math.Max(box.Left + 4, box.Right - 145)), box.Top + 5), InkBrush, 11);
        }
        dc.Pop();
        Text(dc, draft ? "Draft" : "Custom slot", new(box.Left, 0), MutedBrush, 11);
        if (active != null) Text(dc, "Dashed: active", new(box.Right, 0), MutedBrush, 11, true);
    }
}
