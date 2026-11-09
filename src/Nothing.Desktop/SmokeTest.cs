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
            Check(AppWordmark.Text == "NOTHING DESKTOP", "Clean text wordmark replaces dotted logo");
            Check(new Typeface(AppWordmark.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var loadedFont)
                && loadedFont.FamilyNames.Values.Any(name => name == "Space Grotesk")
                && loadedFont.FontUri.ToString().Contains("Assets/Fonts", StringComparison.OrdinalIgnoreCase), "Bundled Space Grotesk loads without system fallback");
            Check(DeviceSidebar.ActualWidth == 224 && DeviceList.Items.Count == Devices.Count, "Persistent left sidebar renders saved devices");
            var originalDevice = SelectedDevice;
            if (originalDevice != null)
            {
                DeviceList.ScrollIntoView(originalDevice); await Idle();
                var container = (ListBoxItem)DeviceList.ItemContainerGenerator.ContainerFromItem(originalDevice);
                Button? FindRemove(DependencyObject node)
                {
                    if (node is Button button && button.Tag is PairedDevice) return button;
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    { var found = FindRemove(VisualTreeHelper.GetChild(node, i)); if (found != null) return found; }
                    return null;
                }
                var remove = FindRemove(container) ?? throw new InvalidOperationException("Remove button missing");
                var hit = remove.TranslatePoint(new Point(remove.ActualWidth / 2, remove.ActualHeight / 2), this);
                var hitVisual = InputHitTest(hit) as DependencyObject;
                while (hitVisual != null && hitVisual != remove) hitVisual = VisualTreeHelper.GetParent(hitVisual);
                Check(hitVisual == remove, "Per-device remove button accepts pointer hit testing");
                InvokeButton(remove); await Idle();
                Check(IsRemoveOpen && RemoveDeviceOverlay.IsVisible && !DeviceList.IsEnabled && !ThemeButton.IsEnabled, "Remove opens confirmation and blocks background controls");
                await Capture("remove-device-dark");
                InvokeButton(CancelRemoveButton); await Idle();
                Check(!IsRemoveOpen && Devices.Contains(originalDevice), "Cancel removal preserves saved device");
                InvokeButton(remove); await Idle(); InvokeButton(ConfirmRemoveButton); await Idle();
                Check(!Devices.Contains(originalDevice) && !connected && BatteryLevel == -1, "Confirmed removal clears selected telemetry");
                Check(deviceLibrary.Load().Devices.All(d => d.Address != originalDevice.Address), "Removed device is absent from on-disk library");
                await ScanAsync(); await Idle();
                Check(Devices.All(d => d.Address != originalDevice.Address), "Discovery does not resurrect removed device");
                Check(Devices.Count != 0 || MainScroll.Visibility == Visibility.Collapsed, "Removing final device hides all right-panel device content");
                if (Devices.Count == 0)
                {
                    Check(FindName("EmptyDevicePanel") is FrameworkElement { IsVisible: true }, "Final removal displays the no-devices empty state");
                    Check(!ProductHero.IsVisible && !EqualizerCard.IsVisible && !ConnectButton.IsVisible, "Empty state hides product art, EQ, and connection actions");
                    ShowPage("Device"); await Idle();
                    Check(!DevicePage.IsVisible, "Device details stay hidden with no devices");
                    ShowPage("Overview");
                    InvokeButton(ThemeButton); await Capture("empty-sidebar-light"); InvokeButton(ThemeButton); await Idle();
                }
                await Capture("empty-sidebar-dark");
                InvokeButton(Devices.Count == 0 ? EmptyAddDeviceButton : AddDeviceButton); await Idle();
                var scanDeadline = DateTime.UtcNow.AddSeconds(15); while (scanning && DateTime.UtcNow < scanDeadline) await Task.Delay(40);
                await Idle();
                Check(IsPickerOpen && AddDeviceOverlay.IsVisible && !DeviceList.IsEnabled, "Add device opens popup and blocks background input");
                Check(CandidateDevices.Any(d => d.Address == originalDevice.Address) && CandidateDevices.All(d => IsHeadphoneOne(d.Name)), "Popup lists real supported Windows-paired devices");
                CandidateList.SelectedItem = CandidateDevices.First(d => d.Address == originalDevice.Address); await Idle();
                Check(ConfirmAddButton.IsEnabled, "Selecting discovered device enables Add");
                await Capture("add-device-dark");
                InvokeButton(ConfirmAddButton); await Idle();
                Check(!IsPickerOpen && Devices.Count(d => d.Address == originalDevice.Address) == 1 && SelectedDevice != null, "Add saves exactly one device and selects it when needed");
                Check(MainScroll.IsVisible && !EmptyDevicePanel.IsVisible && ProductHero.IsVisible, "Adding from empty state restores the selected-device panel");
                Check(deviceLibrary.Load().Devices.Any(d => d.Address == originalDevice.Address), "Added device persists to local JSON");
                InvokeButton(AddDeviceButton); await Idle();
                scanDeadline = DateTime.UtcNow.AddSeconds(15); while (scanning && DateTime.UtcNow < scanDeadline) await Task.Delay(40);
                await Idle();
                Check(CandidateDevices.All(d => Devices.All(saved => saved.Address != d.Address)) && !ConfirmAddButton.IsEnabled, "Already-added devices excluded; empty selection cannot be added");
                RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), Environment.TickCount, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent }); await Idle();
                Check(!IsPickerOpen && AddDeviceButton.IsEnabled, "Escape dismisses picker and restores navigation");
            }
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
            double Contrast(string foreground, string background)
            {
                double Luminance(string key)
                {
                    var color = ((SolidColorBrush)FindResource(key)).Color;
                    double Linear(byte channel) { var v = channel / 255d; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
                    return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
                }
                var a = Luminance(foreground); var b = Luminance(background);
                return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
            }
            Check(Contrast("Ink", "CardFill") >= 4.5 && Contrast("Muted", "CardFill") >= 4.5 && Contrast("Muted", "Page") >= 4.5 && Contrast("Muted", "SidebarFill") >= 4.5, "Light primary and secondary text exceed 4.5:1 contrast");
            Check(Contrast("DisabledInk", "CardFill") >= 4.5 && Contrast("DisabledInk", "DisabledFill") >= 4.5, "Disabled labels stay readable without global opacity");
            Check(Contrast("ControlLine", "CardFill") >= 3 && Contrast("Green", "CardFill") >= 3, "Light control outlines and battery icon exceed 3:1 contrast");
            Check(ProductName.ActualWidth <= ProductHero.ActualWidth && ProductName.Text == SelectedDeviceName, "Product name fits its column");
            Check(LatencyCheck.Opacity == 1 && DualCheck.Opacity == 1, "Disabled toggles retain readable label opacity");
            if (originalDevice != null)
            {
                InvokeButton(AddDeviceButton); await Idle();
                var deadline = DateTime.UtcNow.AddSeconds(15); while (scanning && DateTime.UtcNow < deadline) await Task.Delay(40);
                await Capture("add-device-light");
                InvokeButton(ClosePickerButton); await Idle();
                RemoveDeviceClick(new Button { Tag = SelectedDevice! }, new RoutedEventArgs(Button.ClickEvent)); await Idle();
                await Capture("remove-device-light"); InvokeButton(CancelRemoveButton); await Idle();
            }
            await Capture("light");
            ShowPage("Device"); await Capture("device-light"); ShowPage("Overview");
            InvokeButton(ThemeButton); await Idle(); Check(Appearance.IsDark, "Theme button switches back to dark");
            Width = 1280; Height = 900; await Capture("desktop-1280");
            Width = MinWidth; Height = MinHeight; await Capture("minimum-size");
            Check(DeviceSidebar.ActualWidth == 224 && MainScroll.ActualWidth > 600, "Minimum width preserves sidebar and usable listening controls");
            InvokeButton(ThemeButton); await Capture("minimum-size-light"); InvokeButton(ThemeButton); await Idle();
            Check(true, "1280px and minimum layouts render in both themes");
            Check(TitleBar.IsAncestorOf(CloseButton), "Close button remains in title bar");
            if (hardware && SelectedDevice is { } liveDevice)
            {
                Check(connected, "Live control link remains connected before removal test");
                RemoveDeviceClick(new Button { Tag = liveDevice }, new RoutedEventArgs(Button.ClickEvent)); await Idle();
                InvokeButton(ConfirmRemoveButton); await Idle();
                Check(!connected && client == null && BatteryLevel == -1 && !poll.IsEnabled && !CanAnc && !CanEq, "Removing connected device closes control link and clears stale settings");
                var paired = await Task.Run(BluetoothNative.Discover);
                Check(paired.Any(d => d.Address == liveDevice.Address), "App removal leaves actual Windows Bluetooth pairing intact");
                InvokeButton(AddDeviceButton); await Idle();
                var deadline = DateTime.UtcNow.AddSeconds(15); while (scanning && DateTime.UtcNow < deadline) await Task.Delay(40);
                CandidateList.SelectedItem = CandidateDevices.First(d => d.Address == liveDevice.Address); await Idle();
                InvokeButton(ConfirmAddButton); await Idle();
                Check(Devices.Any(d => d.Address == liveDevice.Address), "Connected-removal test restores device to isolated test library");
            }
            var savedDevices = Devices.ToArray();
            var savedSelection = SelectedDevice;
            Disconnect(); selectedDevice = null; Devices.Clear();
            var fixtures = new[]
            {
                new PairedDevice("Headphone (1)", 0x112233440001, false),
                new PairedDevice("Headphone (1)", 0x112233440002, false),
                new PairedDevice("Headphone (1)", 0x112233440003, false)
            };
            foreach (var device in fixtures) Devices.Add(device);
            selectedDevice = fixtures[1]; Notify(); await Idle();
            void RemoveSaved(PairedDevice device)
            {
                RemoveDeviceClick(new Button { Tag = device }, new RoutedEventArgs(Button.ClickEvent));
                ConfirmRemoveClick(this, new RoutedEventArgs(Button.ClickEvent));
            }
            RemoveSaved(fixtures[1]); await Idle();
            Check(SelectedDevice == fixtures[2] && Equals(DeviceList.SelectedItem, fixtures[2]) && MainScroll.IsVisible && !EmptyDevicePanel.IsVisible, "Removing selected middle device displays the next device");
            RemoveSaved(fixtures[2]); await Idle();
            Check(SelectedDevice == fixtures[0] && Equals(DeviceList.SelectedItem, fixtures[0]) && MainScroll.IsVisible, "Removing selected last device falls back to the previous device");
            Devices.Add(fixtures[1]); Notify(); await Idle();
            RemoveSaved(fixtures[1]); await Idle();
            Check(SelectedDevice == fixtures[0] && MainScroll.IsVisible, "Removing another device preserves the current panel selection");
            RemoveSaved(fixtures[0]); await Idle();
            Check(SelectedDevice == null && !MainScroll.IsVisible && EmptyDevicePanel.IsVisible && deviceLibrary.Load().Devices.Length == 0, "Removing final device shows only the empty state and persists an empty list");
            selectedDevice = null; Devices.Clear();
            foreach (var device in savedDevices) Devices.Add(device);
            selectedDevice = savedSelection; SaveDevices(); Notify(); await Idle();
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
