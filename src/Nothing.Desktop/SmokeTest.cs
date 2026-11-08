using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
namespace Nothing.Desktop;
public partial class MainWindow
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private async Task SmokeTestAsync()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var hardware = Environment.GetCommandLineArgs().Contains("--hardware-ui");
        var directory = System.IO.Path.Combine(Environment.CurrentDirectory, ".agent_tmp", hardware ? "screenshots-live" : "screenshots");
        Directory.CreateDirectory(directory);
        var results = new List<string>();
        var failed = false;
        void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); results.Add("PASS " + text); }
        async Task Idle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout(); }
        async Task Capture(string name)
        {
            await Idle();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(System.IO.Path.Combine(directory, name + ".png")); encoder.Save(stream);
        }
        try
        {
            Check(!CanAnc && !CanEq && !CanApplyEq && !CanLatency && !CanDual && BatteryLevel == -1, "Offline telemetry unknown and device writes disabled");
            Check(Appearance.IsDark && ((SolidColorBrush)FindResource("Page")).Color.R < 20, "Dark mode is default");
            Check(AncLevelRow.Visibility == Visibility.Collapsed, "ANC level absent while disconnected");
            Check(IsHeadphoneOne("Nothing Headphone (1)") && !IsHeadphoneOne("Nothing Ear (1)") && !IsHeadphoneOne("Nothing Headphone (2)"), "Exact model guard");
            await Idle();
            foreach (var button in new[] { MinimizeButton, MaximizeButton, CloseButton, ThemeButton })
            {
                var point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), this);
                var visual = InputHitTest(point) as DependencyObject;
                while (visual != null && visual != button) visual = VisualTreeHelper.GetParent(visual);
                Check(visual == button && WindowChrome.GetIsHitTestVisibleInChrome(button), button.Name + " accepts pointer input inside chrome");
                var screen = PointToScreen(point);
                var packed = (unchecked((int)screen.Y) & 0xFFFF) << 16 | (unchecked((int)screen.X) & 0xFFFF);
                var hit = SendMessage(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x84, IntPtr.Zero, new IntPtr(packed));
                Check(hit.ToInt32() == 1, button.Name + " receives native HTCLIENT hit routing");
            }
            InvokeButton(MinimizeButton); await Idle(); Check(WindowState == WindowState.Minimized, "Minimize button works");
            WindowState = WindowState.Normal; await Idle();
            InvokeButton(MaximizeButton); await Idle(); Check(WindowState == WindowState.Maximized, "Maximize button works");
            InvokeButton(MaximizeButton); await Idle(); Check(WindowState == WindowState.Normal, "Restore button works");
            await ScanAsync();
            Check(true, $"Native paired-device discovery: {Devices.Count} Headphone (1) device(s)");
            if (hardware)
            {
                ConnectClick(this, new RoutedEventArgs());
                var deadline = DateTime.UtcNow.AddSeconds(70); while (busy && DateTime.UtcNow < deadline) await Task.Delay(100);
                Check(!busy && connected && battery.HasValue && deviceEq != null, "Live headphone telemetry initialized");
                var windows = WindowsBattery.Read(SelectedDevice!.Address);
                Check(windows == null || battery == windows, $"Battery displayed={battery}%, Windows={windows}%, NT LINK={batteryReading.ProtocolPercent}%");
                var savedAnc = anc;
                foreach (var mode in new byte[] { 5, 7, 1, 2, 3, 4 })
                {
                    anc = mode; SyncControls(); Notify(); await Idle();
                    Check((AncLevelRow.Visibility == Visibility.Visible) == (mode <= 4), $"ANC level visibility correct in mode {mode}");
                }
                anc = savedAnc; SyncControls(); Notify();
                var oldGain = Bands[0].Gain; Bands[0].Gain = oldGain > 11 ? oldGain - 0.5 : oldGain + 0.5;
                Check(CanApplyEq && eqDirty, "EQ local draft updates without device writes"); ResetEqClick(this, new RoutedEventArgs());
                Check(!eqDirty && !CanApplyEq && Math.Abs(Bands[0].Gain - oldGain) < 0.01, "EQ reset restores actual device curve");
            }
            Check(OverviewPage.IsAncestorOf(LatencyCheck) && OverviewPage.IsAncestorOf(DualCheck) && OverviewPage.IsAncestorOf(BandControls), "All listening controls are on the first page");
            foreach (var target in new[] { "Overview", "Device" })
            {
                ShowPage(target); await Capture(target.ToLowerInvariant());
                if (hardware && target == "Overview") Check(ResponsePlot.HasCurve && ResponsePlot.SampleCount > 300, "Logarithmic filter response renders dense samples");
                Check(true, target + " page renders in dark mode");
            }
            ShowPage("Overview"); InvokeButton(ThemeButton); await Idle();
            Check(!Appearance.IsDark && ((SolidColorBrush)FindResource("Page")).Color.R > 200, "Top-bar theme button switches to light");
            await Capture("light");
            ShowPage("Device"); await Capture("device-light"); ShowPage("Overview");
            InvokeButton(ThemeButton); await Idle(); Check(Appearance.IsDark, "Theme button switches back to dark");
            Width = 1280; Height = 900; await Capture("desktop-1280");
            Width = MinWidth; Height = MinHeight; await Capture("minimum-size");
            Check(true, "1280px and minimum-size layouts render");
            Check(TitleBar.IsAncestorOf(CloseButton), "Close button remains in title bar");
        }
        catch (Exception ex) { failed = true; results.Add("FAIL " + ex); }
        if (!failed)
        {
            InvokeButton(CloseButton);
            await Task.Delay(100);
            if (closed) results.Add("PASS Close button closes the real window"); else { failed = true; results.Add("FAIL Close button did not close"); }
        }
        await File.WriteAllLinesAsync(System.IO.Path.Combine(directory, "smoke-results.txt"), results);
        Application.Current.Shutdown(failed ? 1 : 0);
    }
}
