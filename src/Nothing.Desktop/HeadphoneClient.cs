using System.Collections.Concurrent;
using System.Text;
using Nothing.Core;

namespace Nothing.Desktop;

public sealed class HeadphoneClient : IDisposable
{
    private readonly RfcommTransport transport = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly ConcurrentDictionary<byte, (ushort Command, TaskCompletionSource<Frame> Completion)> pending = new();
    private readonly DateTime[] lastOperation = new DateTime[256];
    private int operation;
    private int disposed;
    private long received, transmitted;
    public bool IsConnected { get; private set; }
    public long Received => Interlocked.Read(ref received);
    public long Transmitted => Interlocked.Read(ref transmitted);
    public event Action<Frame>? Notification;
    public event Action<string>? ConnectionLost;

    public async Task ConnectAsync(PairedDevice device, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await transport.ConnectAsync(device.Address, linked.Token).ConfigureAwait(false);
        IsConnected = true;
        _ = ReceiveLoopAsync();
    }

    private async Task ReceiveLoopAsync()
    {
        var parser = new FrameParser(); var buffer = new byte[4096];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var length = await transport.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (length == 0) throw new IOException("Headphone disconnected.");
                foreach (var frame in parser.Feed(buffer.AsSpan(0, length)))
                {
                    Interlocked.Increment(ref received);
                    if (frame.Operation != 0 && pending.TryGetValue(frame.Operation, out var entry) && entry.Command == frame.Command)
                        entry.Completion.TrySetResult(frame);
                    else if (frame.Operation == 0) Notification?.Invoke(frame);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            IsConnected = false;
            foreach (var entry in pending.Values) entry.Completion.TrySetException(new IOException("Bluetooth connection closed.", ex));
            if (!lifetime.IsCancellationRequested) ConnectionLost?.Invoke(ex.Message);
        }
    }

    public async Task<byte[]> RequestAsync(ushort command, byte[]? payload = null)
    {
        await requests.WaitAsync(lifetime.Token).ConfigureAwait(false);
        byte id = 0;
        try
        {
            if (!IsConnected) throw new IOException("Connect your headphone first.");
            for (var i = 0; i < 255; i++)
            {
                operation = operation % 255 + 1;
                if (DateTime.UtcNow - lastOperation[operation] > TimeSpan.FromSeconds(30)) { id = (byte)operation; break; }
            }
            if (id == 0) throw new IOException("Too many commands. Wait a moment and retry.");
            lastOperation[id] = DateTime.UtcNow;
            var completion = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = ((ushort)(command & 0x7FFF), completion);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                await transport.WriteAsync(Protocol.Encode(command, id, payload ?? []), timeout.Token).ConfigureAwait(false);
                Interlocked.Increment(ref transmitted);
                return (await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false)).Payload;
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            { throw new TimeoutException($"No reply to 0x{command:X4}. Device may be busy or this firmware may not support it."); }
        }
        finally { if (id != 0) pending.TryRemove(id, out _); requests.Release(); }
    }

    public async Task WriteAsync(ushort command, byte[] payload)
    {
        var ack = await RequestAsync(command, payload).ConfigureAwait(false);
        if (ack.Length > 0 && ack[0] != 0) throw new IOException($"Device rejected 0x{command:X4} (status {ack[0]}).");
    }

    public async Task<byte> ReadByteAsync(ushort command)
    {
        var data = await RequestAsync(command).ConfigureAwait(false);
        if (data.Length != 1) throw new IOException($"Unexpected response to 0x{command:X4}.");
        return data[0];
    }

    public async Task<string> ReadFirmwareAsync()
    {
        var data = await RequestAsync(0xC042).ConfigureAwait(false);
        return Encoding.ASCII.GetString(data).Trim((char)0, (char)32, (char)13, (char)10);
    }

    public async Task SetAncAsync(byte mode)
    {
        if (mode is not (1 or 2 or 3 or 4 or 5 or 7)) throw new ArgumentException("Unsupported ANC mode.");
        await WriteAsync(0xF00F, [1, mode, 0]).ConfigureAwait(false);
        if (DevicePayload.Anc(await RequestAsync(0xC01E).ConfigureAwait(false)) != mode) throw new IOException("Noise control read-back did not match.");
    }

    public async Task SetByteAsync(ushort write, ushort read, byte value)
    {
        await WriteAsync(write, [value]).ConfigureAwait(false);
        if (await ReadByteAsync(read).ConfigureAwait(false) != value) throw new IOException("Setting was not confirmed by the headphone.");
    }

    public async Task SetBalancedAsync()
    {
        await WriteAsync(0xF010, [0, 0]).ConfigureAwait(false);
        if (await ReadByteAsync(0xC01F).ConfigureAwait(false) != 0) throw new IOException("Balanced preset was not confirmed.");
    }

    public async Task SetEqualizerAsync(EqualizerProfile profile)
    {
        await WriteAsync(0xF050, profile.Encode()).ConfigureAwait(false);
        var read = EqualizerProfile.Decode(await RequestAsync(0xC04D, [0]).ConfigureAwait(false));
        if (!read.EquivalentTo(profile)) throw new IOException("Custom curve read-back did not match. The active preset was not changed.");
        await WriteAsync(0xF010, [5, 0]).ConfigureAwait(false);
        if (await ReadByteAsync(0xC01F).ConfigureAwait(false) != 5) throw new IOException("Custom curve saved, but active preset was not confirmed.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        IsConnected = false; lifetime.Cancel(); transport.Dispose();
        foreach (var entry in pending.Values) entry.Completion.TrySetCanceled();
    }
}
