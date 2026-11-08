using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Nothing.Desktop;

public sealed record PairedDevice(string Name, ulong Address, bool Connected)
{
    public string AddressText => string.Join(":", Enumerable.Range(0, 6).Reverse().Select(i => ((Address >> (i * 8)) & 255).ToString("X2")));
    public override string ToString() => $"{Name}   /   {AddressText}";
}

public static class BluetoothNative
{
    public static readonly Guid ServiceId = new("AEAC4A03-DFF5-498F-843A-34487CF133EB");
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SocketAddress { public ushort Family; public ulong Address; public Guid Service; public uint Port; }
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DeviceInfo
    {
        public uint Size; public ulong Address; public uint Class;
        public int Connected, Remembered, Authenticated; public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct SearchParams
    {
        public uint Size; public int Authenticated, Remembered, Unknown, Connected, Inquiry; public byte Timeout; public IntPtr Radio;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct FdSet
    {
        public uint Count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public IntPtr[] Handles;
        public static FdSet One(IntPtr socket) { var a = new IntPtr[64]; a[0] = socket; return new() { Count = 1, Handles = a }; }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct TimeValue { public int Seconds, Microseconds; }

    [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstDevice(ref SearchParams search, ref DeviceInfo info);
    [DllImport("bthprops.cpl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindNextDevice(IntPtr find, ref DeviceInfo info);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindDeviceClose(IntPtr find);
    [DllImport("ws2_32.dll")] private static extern int WSAStartup(ushort version, IntPtr data);
    [DllImport("ws2_32.dll")] internal static extern int WSAGetLastError();
    [DllImport("ws2_32.dll")] internal static extern IntPtr socket(int af, int type, int protocol);
    [DllImport("ws2_32.dll")] internal static extern int connect(IntPtr socket, ref SocketAddress address, int size);
    [DllImport("ws2_32.dll")] internal static extern int closesocket(IntPtr socket);
    [DllImport("ws2_32.dll")] internal static extern int ioctlsocket(IntPtr socket, uint command, ref uint value);
    [DllImport("ws2_32.dll")] internal static extern int recv(IntPtr socket, [Out] byte[] buffer, int length, int flags);
    [DllImport("ws2_32.dll")] internal static extern int send(IntPtr socket, byte[] buffer, int length, int flags);
    [DllImport("ws2_32.dll")] internal static extern int select(int ignored, IntPtr read, ref FdSet write, ref FdSet error, ref TimeValue timeout);
    [DllImport("ws2_32.dll")] internal static extern int getsockopt(IntPtr socket, int level, int option, out int value, ref int size);

    private static readonly Lazy<bool> Initialized = new(() =>
    {
        var data = Marshal.AllocHGlobal(512);
        try { var result = WSAStartup(0x0202, data); if (result != 0) throw new Win32Exception(result); return true; }
        finally { Marshal.FreeHGlobal(data); }
    });
    internal static void Initialize() => _ = Initialized.Value;

    public static IReadOnlyList<PairedDevice> Discover()
    {
        var search = new SearchParams { Size = (uint)Marshal.SizeOf<SearchParams>(), Authenticated = 1, Remembered = 1, Connected = 1 };
        var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>(), Name = "" };
        var handle = BluetoothFindFirstDevice(ref search, ref info);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 0 or 259 or 1168) return [];
            throw new Win32Exception(error, "Windows Bluetooth discovery failed. Enable your Bluetooth adapter.");
        }
        List<PairedDevice> devices = [];
        try
        {
            do
            {
                if (info.Authenticated != 0 || info.Remembered != 0)
                    devices.Add(new(info.Name, info.Address, info.Connected != 0));
                info.Size = (uint)Marshal.SizeOf<DeviceInfo>();
            } while (BluetoothFindNextDevice(handle, ref info));
            var error = Marshal.GetLastWin32Error();
            if (error is not (0 or 259)) throw new Win32Exception(error);
        }
        finally { BluetoothFindDeviceClose(handle); }
        return devices.DistinctBy(d => d.Address).OrderByDescending(d => d.Name.Contains("Nothing", StringComparison.OrdinalIgnoreCase)).ThenBy(d => d.Name).ToArray();
    }
}

public sealed class RfcommTransport : IDisposable
{
    private IntPtr handle = new(-1);
    private readonly object gate = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private bool disposed;
    private static IOException SocketError(string action, int code) => new($"{action}: {new Win32Exception(code).Message} (Bluetooth {code}).");

    public async Task ConnectAsync(ulong address, CancellationToken cancellationToken)
    {
        BluetoothNative.Initialize();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            handle = BluetoothNative.socket(32, 1, 3);
            if (handle == new IntPtr(-1)) throw SocketError("Create RFCOMM socket", BluetoothNative.WSAGetLastError());
            uint nonblocking = 1;
            if (BluetoothNative.ioctlsocket(handle, 0x8004667E, ref nonblocking) != 0) throw SocketError("Set nonblocking mode", BluetoothNative.WSAGetLastError());
            var endpoint = new BluetoothNative.SocketAddress { Family = 32, Address = address, Service = BluetoothNative.ServiceId, Port = 0 };
            if (BluetoothNative.connect(handle, ref endpoint, Marshal.SizeOf<BluetoothNative.SocketAddress>()) == 0) return;
            var error = BluetoothNative.WSAGetLastError();
            if (error != 10035) throw SocketError("Connect to NT LINK", error);
        }
        while (true)
        {
            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var write = BluetoothNative.FdSet.One(handle); var errorSet = BluetoothNative.FdSet.One(handle); var poll = new BluetoothNative.TimeValue();
                var selected = BluetoothNative.select(0, IntPtr.Zero, ref write, ref errorSet, ref poll);
                if (selected < 0) throw SocketError("Poll connection", BluetoothNative.WSAGetLastError());
                if (selected == 0) continue;
                var size = 4;
                if (BluetoothNative.getsockopt(handle, 0xFFFF, 0x1007, out var error, ref size) != 0) throw SocketError("Read socket status", BluetoothNative.WSAGetLastError());
                if (error != 0) throw SocketError("Connect to NT LINK", error);
                if (write.Count > 0) return;
                throw new IOException("NT LINK connection rejected. Close other headphone control apps and try again.");
            }
        }
    }

    public async Task<int> ReadAsync(byte[] buffer, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var count = BluetoothNative.recv(handle, buffer, buffer.Length, 0);
                if (count >= 0) return count;
                var error = BluetoothNative.WSAGetLastError();
                if (error != 10035) throw SocketError("Receive", error);
            }
            await Task.Delay(25, token).ConfigureAwait(false);
        }
    }

    public async Task WriteAsync(byte[] data, CancellationToken token)
    {
        await sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var offset = 0;
            while (offset < data.Length)
            {
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    var remaining = data.AsSpan(offset).ToArray();
                    var count = BluetoothNative.send(handle, remaining, remaining.Length, 0);
                    if (count > 0) offset += count;
                    else if (count == 0) throw new IOException("Bluetooth connection closed.");
                    else { var error = BluetoothNative.WSAGetLastError(); if (error != 10035) throw SocketError("Send", error); }
                }
                if (offset < data.Length) await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
        finally { sendGate.Release(); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (handle != new IntPtr(-1)) BluetoothNative.closesocket(handle);
            handle = new(-1);
        }
    }
}
