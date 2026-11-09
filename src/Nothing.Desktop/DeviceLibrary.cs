using System.IO;
using System.Text.Json;
namespace Nothing.Desktop;
public sealed record SavedDeviceSet(PairedDevice[] Devices, ulong? SelectedAddress);
public sealed class DeviceLibrary(string filePath)
{
    private sealed record Entry(string Name, ulong Address);
    private sealed record Document(Entry[] Devices, ulong? SelectedAddress);
    public string FilePath { get; } = filePath;
    public bool Exists => File.Exists(FilePath);
    public SavedDeviceSet Load()
    {
        if (!Exists) return new([], null);
        try
        {
            var data = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath));
            if (data?.Devices == null || data.Devices.Any(d => d == null || !Valid(d.Name, d.Address)))
                throw new IOException("The saved device list is invalid. It has not been overwritten.");
            var devices = data.Devices.DistinctBy(d => d.Address).Select(d => new PairedDevice(d.Name, d.Address, false)).ToArray();
            return new(devices, devices.Any(d => d.Address == data.SelectedAddress) ? data.SelectedAddress : null);
        }
        catch (JsonException ex) { throw new IOException("The saved device list could not be read. It has not been overwritten.", ex); }
    }
    public void Save(IEnumerable<PairedDevice> devices, ulong? selectedAddress)
    {
        var entries = devices.DistinctBy(d => d.Address).Select(d => new Entry(d.Name, d.Address)).ToArray();
        if (entries.Any(d => !Valid(d.Name, d.Address))) throw new ArgumentException("A saved device must have a name and valid Bluetooth address.");
        var data = new Document(entries, entries.Any(d => d.Address == selectedAddress) ? selectedAddress : null);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temporary = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data));
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool Valid(string name, ulong address) => !string.IsNullOrWhiteSpace(name) && address is > 0 and <= 0xFFFFFFFFFFFF;
}