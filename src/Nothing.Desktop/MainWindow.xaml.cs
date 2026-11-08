using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Nothing.Core;

namespace Nothing.Desktop;

public sealed class BandViewModel : INotifyPropertyChanged
{
    private double gain;
    private string label = "";
    public string Label { get => label; set { label = value; PropertyChanged?.Invoke(this, new(nameof(Label))); } }
    public double Gain { get => gain; set { if (Math.Abs(gain - value) < 0.001) return; gain = value; PropertyChanged?.Invoke(this, new(nameof(Gain))); PropertyChanged?.Invoke(this, new(nameof(GainText))); } }
    public string GainText => Gain.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private HeadphoneClient? client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Queue<string> journal = new();
    private bool busy, updating, closed, connected, experimental, eqDirty;
    private int? battery;
    private BatteryReading batteryReading = BatteryReading.Resolve(null, false, null);
    private int pollTick;
    private bool charging;
    private byte? anc, preset, latency, dual;
    private bool? spatial;
    private string firmware = "—", codec = "Not read", lastRead = "—";
    private EqualizerProfile? deviceEq;
    private PairedDevice? selectedDevice;
    private string page = "Overview", message = "";
    private bool messageError;

    public MainWindow()
    {
        if (Environment.GetCommandLineArgs().Contains("--smoke-test")) Appearance.Apply(true, false); else Appearance.Load();
        InitializeComponent();
        DataContext = this;
        foreach (var label in new[] { "55 Hz", "110 Hz", "220 Hz", "440 Hz", "1.32 kHz", "3.3 kHz", "6.6 kHz", "13.2 kHz" })
        {
            var band = new BandViewModel { Label = label };
            band.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(BandViewModel.Gain)) return;
                if (!updating) { eqDirty = true; Notify(); }
                DrawEq();
            };
            Bands.Add(band);
        }
        poll.Tick += async (_, _) => { if (connected && !busy) await RunAsync(async () => { if (++pollTick % 3 == 0) await RefreshTelemetryAsync(); else await RefreshLiveAsync(); }, false); };
        Log("Ready.");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<PairedDevice> Devices { get; } = [];
    public ObservableCollection<BandViewModel> Bands { get; } = [];
    public PairedDevice? SelectedDevice { get => selectedDevice; set { selectedDevice = value; Notify(); } }
    public string ConnectionLabel => connected ? "Connected" : busy ? "Connecting" : "Disconnected";
    public Brush StatusBrush => Brush(connected ? "#9EB685" : busy ? "#E2BC64" : "#D35E53");
    public string ConnectButtonText => connected ? "Disconnect" : busy ? "Connecting" : "Connect";
    public bool CanConnect => !busy;
    public bool CanRefresh => connected && !busy;
    public bool CanChooseDevice => !busy && !connected;
    public bool CanAnc => connected && !busy && anc.HasValue;
    public bool ShowAncLevel => connected && anc is >= 1 and <= 4;
    public bool CanAncLevel => CanAnc && anc is >= 1 and <= 4;
    public bool CanEq => connected && !busy && preset.HasValue && spatial == false;
    public bool CanEditEq => CanEq && deviceEq != null;
    public bool CanApplyEq => CanEditEq && eqDirty;
    public bool CanLatency => connected && !busy && experimental && latency.HasValue;
    public bool CanDual => connected && !busy && experimental && dual.HasValue;
    public bool Experimental { get => experimental; set { experimental = value; Notify(); } }
    public double BatteryLevel => battery ?? -1;
    public string BatteryText => battery.HasValue ? $"{battery}%" : "—";
    public bool IsCharging => battery.HasValue && charging;
    public bool HasError => messageError;
    public string ThemeToolTip => Appearance.IsDark ? "Switch to light mode" : "Switch to dark mode";
    public string WindowsBatteryText => batteryReading.WindowsPercent is int p ? $"{p}%" : "Unavailable";
    public string ProtocolBatteryText => batteryReading.ProtocolPercent is int p ? $"{p}%" : "Unavailable";
    public string BatterySource => batteryReading.Source;
    public string BatteryTooltip => batteryReading.Disagrees ? $"Windows: {WindowsBatteryText}; NT LINK: {ProtocolBatteryText}. Using Windows." : $"{BatterySource} / {lastRead}";
    public string LastRead => lastRead;
    public string Firmware => firmware;
    public string LinkText => connected ? "RFCOMM / LIVE" : "OFFLINE";
    public string PresetText => preset switch { 0 => "Balanced", 5 => "Custom", null => "Not read", _ => $"Device preset {preset}" };

    public string EqHint => !connected ? "Connect to edit" : spatial != false ? "Disable spatial audio to edit" : deviceEq == null ? "EQ unavailable" : eqDirty ? "Unsaved" : "Custom slot";
    public string SpatialText => spatial switch { true => "On", false => "Off", _ => "Unavailable" };
    public string CodecText => codec;
    public string DeviceAddress => connected ? selectedDevice?.AddressText ?? "—" : "Not connected";
    public string DiscoveryText => Devices.Count == 0 ? "No paired Headphone (1) found" : $"{Devices.Count} paired device(s)";
    public string PacketText => client == null ? "No active session" : $"TX {client.Transmitted} / RX {client.Received}";
    public string JournalText => string.Join(Environment.NewLine, journal);
    public string Message => message;
    private static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    private void Notify() { PropertyChanged?.Invoke(this, new(null)); DrawEq(); }
    private void Log(string text) { journal.Enqueue($"{DateTime.Now:HH:mm:ss}  {text}"); while (journal.Count > 70) journal.Dequeue(); Notify(); }
    private void Status(string text, bool error = false) { message = text; messageError = error; Log(text); }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (Environment.GetCommandLineArgs().Contains("--smoke-test")) { await SmokeTestAsync(); return; }
        await RunAsync(ScanAsync, false);
        DrawEq();
        if (Environment.GetCommandLineArgs().Contains("--connect") && !closed) ConnectClick(this, new RoutedEventArgs());
    }

    private async Task RunAsync(Func<Task> action, bool successMessage = true)
    {
        if (busy || closed) return;
        busy = true; Notify();
        try { await action(); if (successMessage && !closed) Status("Saved."); }
        catch (OperationCanceledException) { if (!closed) Status("Operation canceled or timed out. Reconnect and try again.", true); }
        catch (Exception ex) when (ex is IOException or TimeoutException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { if (!closed) Status(ex.Message, true); }
        finally { busy = false; if (!closed) { SyncControls(); Notify(); } }
    }

    private async Task ScanAsync()
    {
        var found = await Task.Run(BluetoothNative.Discover, lifetime.Token);
        if (closed) return;
        Devices.Clear();
        foreach (var d in found.Where(d => IsHeadphoneOne(d.Name))) Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault();
        Log($"Discovery: {Devices.Count} paired Headphone (1) device(s).");
    }

    internal static bool IsHeadphoneOne(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Equals("NothingHeadphone1", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Headphone1", StringComparison.OrdinalIgnoreCase);
    }

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (connected)
        {
            if (eqDirty && MessageBox.Show(this, "Disconnect and discard the unsaved EQ draft?", "Unsaved curve", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Disconnect(); Status("Disconnected."); return;
        }
        await RunAsync(async () =>
        {
            if (SelectedDevice == null) await ScanAsync();
            if (SelectedDevice == null) { ShowPage("Device"); Status("No paired Headphone (1) found. Pair in Windows, unplug USB-C, then scan again.", true); return; }
            ClearTelemetry();
            var next = new HeadphoneClient(); client = next;
            next.ConnectionLost += text =>
            {
                if (closed || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(() => { if (client != next) return; Disconnect(); Status(text + " Reconnect when the headphone is available.", true); });
            };
            next.Notification += frame =>
            {
                if (closed || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (client != next || !connected) return;
                    if (frame.Command == 0xE001)
                    {
                        try { var power = DevicePayload.Battery(frame.Payload); ApplyBattery(power.Percent, power.Charging, batteryReading.WindowsPercent); }
                        catch (ArgumentException) { Log("Invalid battery notification."); }
                    }
                    if (frame.Command == 0xE003)
                    {
                        try { anc = DevicePayload.Anc(frame.Payload); SyncControls(); Notify(); }
                        catch (ArgumentException) { Log("Ignored malformed ANC notification."); }
                    }
                });
            };
            try
            {
                Status("Opening the headphone's NT LINK service…");
                await next.ConnectAsync(SelectedDevice, lifetime.Token);
                connected = true;
                await RefreshBatteryAsync(next);
                await ReadDetailsAsync(next);
                if (!next.IsConnected) throw new IOException("The headphone disconnected during initialization.");
                poll.Start();
                Status("Connected.");
            }
            catch { if (client == next) Disconnect(); else next.Dispose(); throw; }
        }, false);
    }

    private async Task OptionalRead(string label, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is TimeoutException or ArgumentException or IOException)
        { Log($"{label} unavailable: {ex.Message}"); }
    }

    private async Task ReadDetailsAsync(HeadphoneClient current)
    {
        await OptionalRead("Firmware", async () => firmware = await current.ReadFirmwareAsync());
        await OptionalRead("Noise control", async () => anc = DevicePayload.Anc(await current.RequestAsync(0xC01E)));
        await OptionalRead("Equalizer preset", async () => preset = await current.ReadByteAsync(0xC01F));
        await OptionalRead("Spatial audio", async () =>
        {
            var data = await current.RequestAsync(0xC04F);
            if (data.Length != 2 || data[0] > 1 || data[1] > 1) throw new ArgumentException("Unexpected spatial state.");
            spatial = data[0] == 1;
        });
        await OptionalRead("Custom equalizer", async () =>
        {
            deviceEq = EqualizerProfile.Decode(await current.RequestAsync(0xC04D, [0]));
            if (!eqDirty) LoadEq(deviceEq);
        });
        await OptionalRead("Low latency", async () => { var v = await current.ReadByteAsync(0xC041); if (v is not (1 or 2)) throw new ArgumentException("Unknown latency value."); latency = v; });
        await OptionalRead("Dual connection", async () => { var v = await current.ReadByteAsync(0xC027); if (v > 1) throw new ArgumentException("Unknown multipoint value."); dual = v; });
        await OptionalRead("Codec preference", async () => { var v = await current.ReadByteAsync(0xC029); codec = v switch { 0 => "Standard (AAC / SBC)", 2 => "LDAC preference", _ => $"Unknown value ({v})" }; });
        SyncControls(); Notify();
    }

    private async Task RefreshBatteryAsync(HeadphoneClient current)
    {
        int? protocol = null; var isCharging = false;
        try
        {
            var power = DevicePayload.Battery(await current.RequestAsync(0xC007));
            protocol = power.Percent; isCharging = power.Charging;
        }
        catch (Exception ex) when (ex is TimeoutException or ArgumentException or IOException) { Log("Battery control read: " + ex.Message); }
        var address = SelectedDevice?.Address;
        var windows = address.HasValue ? await Task.Run(() => WindowsBattery.Read(address.Value)) : null;
        ApplyBattery(protocol, isCharging, windows);
    }
    private void ApplyBattery(int? protocol, bool isCharging, int? windows)
    {
        var next = BatteryReading.Resolve(protocol, isCharging, windows);
        if (next.Disagrees && (batteryReading.WindowsPercent != windows || batteryReading.ProtocolPercent != protocol))
            Log($"Battery source mismatch: Windows={windows}%, NT LINK={protocol}%; using Windows.");
        batteryReading = next; battery = next.Percent; charging = next.Charging;
        lastRead = next.Percent.HasValue ? DateTime.Now.ToString("HH:mm:ss") : "Unavailable"; Notify();
    }
    private async Task RefreshLiveAsync()
    {
        var current = client ?? throw new IOException("Disconnected.");
        await RefreshBatteryAsync(current);
        anc = null; preset = null; spatial = null;
        await OptionalRead("Noise control", async () => anc = DevicePayload.Anc(await current.RequestAsync(0xC01E)));
        await OptionalRead("EQ preset", async () => preset = await current.ReadByteAsync(0xC01F));
        await OptionalRead("Spatial audio", async () => { var data = await current.RequestAsync(0xC04F); if (data.Length != 2 || data[0] > 1) throw new ArgumentException("Invalid spatial state."); spatial = data[0] == 1; });
        SyncControls(); Notify();
    }
    private async Task RefreshTelemetryAsync()
    {
        var current = client ?? throw new IOException("Disconnected.");
        await RefreshBatteryAsync(current);
        anc = null; preset = null; spatial = null; latency = null; dual = null;
        firmware = "Not read"; codec = "Not read"; deviceEq = null;
        await ReadDetailsAsync(current);
    }

    private void ClearTelemetry()
    {
        batteryReading = BatteryReading.Resolve(null, false, null);
        battery = null; charging = false; anc = null; preset = null; spatial = null; latency = null; dual = null;
        firmware = "—"; codec = "Not read"; lastRead = "—"; deviceEq = null; eqDirty = false;
        LoadEq(EqualizerProfile.Flat()); SyncControls(); Notify();
    }
    private void Disconnect() { poll.Stop(); var old = client; client = null; connected = false; old?.Dispose(); ClearTelemetry(); }
    private void SyncControls()
    {
        if (AncOff == null) return;
        updating = true;
        try
        {
            AncOff.IsChecked = anc == 5; AncTransparency.IsChecked = anc == 7; AncOn.IsChecked = anc is >= 1 and <= 4;
            AncLevel.SelectedIndex = anc switch { 4 => 0, 1 => 1, 2 => 2, 3 => 3, _ => -1 };
            LatencyCheck.IsChecked = latency == 1; DualCheck.IsChecked = dual == 1;
        }
        finally { updating = false; }
    }
    private void LoadEq(EqualizerProfile profile)
    {
        updating = true;
        try
        {
            for (var i = 0; i < Bands.Count; i++)
            {
                Bands[i].Label = profile.Bands[i].Frequency >= 1000 ? $"{profile.Bands[i].Frequency / 1000:0.##}k" : $"{profile.Bands[i].Frequency:0}";
                Bands[i].Gain = profile.Bands[i].Gain;
            }
        }
        finally { updating = false; }
        eqDirty = false; DrawEq(); Notify();
    }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RunAsync(RefreshTelemetryAsync, false);
    private async void ScanClick(object sender, RoutedEventArgs e) => await RunAsync(ScanAsync, false);
    private async void AncClick(object sender, RoutedEventArgs e)
    {
        if (updating || !CanAnc || sender is not RadioButton radio || !byte.TryParse(radio.Tag?.ToString(), out var mode)) return;
        await RunAsync(async () => { var c = client ?? throw new IOException("Disconnected."); try { await c.SetAncAsync(mode); anc = mode; } catch { anc = null; throw; } });
    }
    private async void AncLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || !CanAncLevel || AncLevel.SelectedItem is not ComboBoxItem item || !byte.TryParse(item.Tag?.ToString(), out var mode)) return;
        await RunAsync(async () => { var c = client ?? throw new IOException("Disconnected."); try { await c.SetAncAsync(mode); anc = mode; } catch { anc = null; throw; } });
    }
    private async void BalancedClick(object sender, RoutedEventArgs e)
    {
        if (!CanEq) return;
        await RunAsync(async () => { var c = client ?? throw new IOException("Disconnected."); await CheckSpatialBeforeEqAsync(c); try { await c.SetBalancedAsync(); preset = 0; } catch { preset = null; throw; } });
    }
    private async Task CheckSpatialBeforeEqAsync(HeadphoneClient c)
    {
        spatial = null;
        var s = await c.RequestAsync(0xC04F);
        if (s.Length != 2 || s[0] != 0) throw new IOException("Disable spatial audio in Nothing X before changing EQ.");
        spatial = false;
    }
    private async void ApplyEqClick(object sender, RoutedEventArgs e)
    {
        if (!CanApplyEq || deviceEq == null) return;
        if (MessageBox.Show(this, "Replace the headphone's stored custom curve and activate it? This experimental feature verifies stored values, not the audible effect.", "Apply custom EQ", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunAsync(async () =>
        {
            var c = client ?? throw new IOException("Disconnected."); await CheckSpatialBeforeEqAsync(c);
            var next = new EqualizerProfile { Preamp = deviceEq.Preamp, Bands = deviceEq.Bands.Select((b, i) => b with { Gain = (float)Bands[i].Gain }).ToArray() };
            try { await c.SetEqualizerAsync(next); deviceEq = next; preset = 5; eqDirty = false; } catch { preset = null; deviceEq = null; throw; }
        });
    }
    private void ResetEqClick(object sender, RoutedEventArgs e) { if (deviceEq != null) LoadEq(deviceEq); }
    private async void LatencyClick(object sender, RoutedEventArgs e)
    {
        if (!CanLatency) return;
        var value = (byte)(LatencyCheck.IsChecked == true ? 1 : 2);
        await RunAsync(async () => { var c = client ?? throw new IOException("Disconnected."); try { await c.SetByteAsync(0xF040, 0xC041, value); latency = value; } catch { latency = null; throw; } });
    }
    private async void DualClick(object sender, RoutedEventArgs e)
    {
        if (!CanDual) return;
        var value = (byte)(DualCheck.IsChecked == true ? 1 : 0);
        await RunAsync(async () => { var c = client ?? throw new IOException("Disconnected."); try { await c.SetByteAsync(0xF01A, 0xC027, value); dual = value; } catch { dual = null; throw; } });
    }
    private void Navigate(object sender, RoutedEventArgs e) { if (OverviewPage != null && sender is RadioButton button) ShowPage(button.Tag?.ToString() ?? "Overview"); }
    private void ShowPage(string target)
    {
        if (target == "Sound") target = "Overview";
        if (page != target)
        {
            page = target;
            if (target == "Overview") OverviewNav.IsChecked = true;
            else DeviceNav.IsChecked = true;
        }
        OverviewPage.Visibility = target == "Overview" ? Visibility.Visible : Visibility.Collapsed; DevicePage.Visibility = target == "Device" ? Visibility.Visible : Visibility.Collapsed; Notify();
        Dispatcher.BeginInvoke(DrawEq, DispatcherPriority.Loaded);
    }
    private void ThemeClick(object sender, RoutedEventArgs e) { Appearance.Apply(!Appearance.IsDark, !Environment.GetCommandLineArgs().Contains("--smoke-test")); Notify(); }
    private void DrawEq()
    {
        if (ResponsePlot == null || Bands.Count != 8) return;
        EqualizerProfile? edited = deviceEq == null ? null : new() { Preamp = deviceEq.Preamp, Bands = deviceEq.Bands.Select((b, i) => b with { Gain = (float)Bands[i].Gain }).ToArray() };
        var active = preset == 0 ? EqualizerProfile.Flat() : preset == 5 ? deviceEq : null;
        ResponsePlot.SetProfiles(edited, active, eqDirty);
    }
    private void LaunchSettings(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Status("Could not open Windows Settings: " + ex.Message, true); }
    }
    private void OpenBluetooth(object sender, RoutedEventArgs e) => LaunchSettings("ms-settings:bluetooth");
    private void OpenSound(object sender, RoutedEventArgs e) => LaunchSettings("ms-settings:sound");
    private void ExportDiagnostics(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = $"nothing-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt", Filter = "Text file|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, $"Nothing Desktop / 0.2.0{Environment.NewLine}{PacketText}{Environment.NewLine}Firmware: {firmware}{Environment.NewLine}Battery Windows={WindowsBatteryText}, NT LINK={ProtocolBatteryText}, displayed={BatterySource}{Environment.NewLine}{JournalText}"); Status("Diagnostics saved locally. No device address or serial number was included."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status(ex.Message, true); }
    }
    private void DragTitle(object sender, MouseButtonEventArgs e) { for (var source = e.OriginalSource as DependencyObject; source != null; source = VisualTreeHelper.GetParent(source))
            if (source is System.Windows.Controls.Primitives.ButtonBase) return; if (e.ClickCount == 2) ToggleMaximize(); else if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void WindowClosed(object? sender, EventArgs e) { closed = true; poll.Stop(); lifetime.Cancel(); client?.Dispose(); }
}
