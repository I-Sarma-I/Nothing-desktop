using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Nothing.Core;
using Nothing.Desktop;
var passed = 0;
void Test(string name, Action test) { test(); Console.WriteLine($"PASS {name}"); passed++; }
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected rejection"); }
Test("CRC16 Modbus standard check vector", () => Assert(Protocol.Crc(Encoding.ASCII.GetBytes("123456789")) == 0x4B37));
Test("Frame layout and little endian fields", () => {
    var f = Protocol.Encode(0xF00F, 42, [1, 4, 0]);
    Assert(f.Length == 13 && f[0] == 0x55 && f[1] == 0x60 && f[2] == 1);
    Assert(BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(3)) == 0xF00F && f[7] == 42);
    Assert(BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(5)) == 3);
    Assert(BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(11)) == Protocol.Crc(f.AsSpan(0,11)));
});
Test("All split positions preserve frames", () => {
    var f = Protocol.Encode(0x4007, 2, [1,6,89]);
    for(var split=1;split<f.Length;split++) { var p=new FrameParser(); Assert(p.Feed(f.AsSpan(0,split)).Count==0); var frames=p.Feed(f.AsSpan(split)); Assert(frames.Count==1 && frames[0].Payload[2]==89); }
});
Test("Multiple frames and noise resynchronize", () => {
    var p=new FrameParser(); byte[] bytes=[9,8,7,..Protocol.Encode(0x4007,2,[1,6,89]),..Protocol.Encode(0x401E,3,[1,4,0])];
    Assert(p.Feed(bytes).Count==2);
});
Test("CRC corruption never accepted", () => {
    var bad=Protocol.Encode(0x4007,2,[1,6,89]); bad[8]^=1; var p=new FrameParser();
    Assert(p.Feed([..bad,..Protocol.Encode(0x401F,3,[0])]).Count==1 && p.RejectedFrames>0);
});
Test("Alternate control bytes accepted by CRC", () => {
    var f=Protocol.Encode(0xE015,0,new byte[16]); f[1]=0x20;f[2]=3; BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(f.Length-2),Protocol.Crc(f.AsSpan(0,f.Length-2)));
    Assert(new FrameParser().Feed(f).Single().Command==0xE015);
});
Test("Corrupted length cannot trap a subsequent valid frame", () => {
    byte[] bad=[0x55,0x60,1,7,0x40,0x00,0x08,2,1];
    Assert(new FrameParser().Feed([..bad,..Protocol.Encode(0x401F,3,[0])]).Count==1);
});
Test("Oversized payload rejected", () => Reject(()=>Protocol.Encode(1,1,new byte[4097])));
Test("Headphone battery with charging flag", () => { var b=DevicePayload.Battery([1,6,0xD9]); Assert(b.Percent==89 && b.Charging); });
Test("Battery parser respects unit and length", () => { Reject(()=>DevicePayload.Battery([1,6])); Reject(()=>DevicePayload.Battery([1,2,50])); Reject(()=>DevicePayload.Battery([1,6,127])); });
Test("ANC payload validates modes", () => { Assert(DevicePayload.Anc([1,7,0,2,4,0])==7); Reject(()=>DevicePayload.Anc([1,6,0])); });
Test("Custom EQ eight-band round trip", () => {
    var e=EqualizerProfile.Flat();e.Bands[0]=e.Bands[0] with {Gain=3.5f};
    var data=e.Encode();var read=EqualizerProfile.Decode(data);
    Assert(data.Length==110 && read.Bands.Length==8 && read.Bands[0].Gain==3.5f && read.Bands[7].Frequency==13200);
    Assert(read.EquivalentTo(e));
});
Test("Custom EQ rejects unsafe gains and nonfinite values", () => {
    var e=EqualizerProfile.Flat();e.Bands[0]=e.Bands[0] with {Gain=13}; Reject(()=>e.Encode());
    e.Bands[0]=e.Bands[0] with {Gain=float.NaN}; Reject(()=>e.Encode());
    Reject(()=>EqualizerProfile.Decode([0,8,0]));
});
Test("Custom EQ preserves device filter shape", () => {
    var e=EqualizerProfile.Flat();e.Bands[2]=new EqualizerBand(1,2,250,0.7f);e.Preamp=-2;
    Assert(EqualizerProfile.Decode(e.Encode()).EquivalentTo(e));
});
Test("Native Windows Bluetooth ABI layout", () => {
    Assert(Marshal.SizeOf<BluetoothNative.SocketAddress>()==30);
    Assert(Marshal.OffsetOf<BluetoothNative.SocketAddress>(nameof(BluetoothNative.SocketAddress.Address)).ToInt32()==2);
    Assert(Marshal.SizeOf<BluetoothNative.DeviceInfo>()==560);
    Assert(Marshal.SizeOf<BluetoothNative.SearchParams>()==(IntPtr.Size==8?40:32));
});
Test("Windows battery overrides stale RFCOMM response", () => { var s=BatteryReading.Resolve(100,false,90); Assert(s.Percent==90 && s.Source=="Windows" && s.Disagrees); });
Test("Battery unknown stays unknown; valid protocol fallback is explicit", () => { Assert(BatteryReading.Resolve(null,false,null).Percent==null); Assert(BatteryReading.Resolve(84,true,null).Percent==84); Reject(()=>BatteryReading.Resolve(150,false,null)); });
Test("EQ flat response is zero across the audible range", () => { var p=EqualizerProfile.Flat();foreach(var f in new[]{20d,55,1000,10000,20000})Assert(Math.Abs(EqualizerResponse.Decibels(p,f))<0.00001); });
Test("EQ peaking center gain and reciprocal cut", () => { var p=EqualizerProfile.Flat();p.Bands[3]=new(1,6,1000,1);Assert(Math.Abs(EqualizerResponse.Decibels(p,1000)-6)<0.0001);p.Bands[3]=p.Bands[3] with {Gain=-6};Assert(Math.Abs(EqualizerResponse.Decibels(p,1000)+6)<0.0001); });
Test("EQ shelf asymptotes and Q-dependent response", () => { var p=EqualizerProfile.Flat();p.Bands[0]=new(0,6,200,0.7071f);Assert(Math.Abs(EqualizerResponse.Decibels(p,1)-6)<0.01);Assert(Math.Abs(EqualizerResponse.Decibels(p,15000))<0.01);p.Bands[0]=new(1,6,1000,0.5f);var broad=EqualizerResponse.Decibels(p,500);p.Bands[0]=p.Bands[0] with {Q=4};Assert(EqualizerResponse.Decibels(p,500)<broad); });
Test("EQ invalid frequency rejected and log axis round trips", () => { Reject(()=>EqualizerResponse.Decibels(EqualizerProfile.Flat(),0));foreach(var f in new[]{20d,100,1000,20000})Assert(Math.Abs(EqualizerResponse.FrequencyAt(EqualizerResponse.PositionAt(f))-f)<0.0001); });
Test("Battery source recovers when Windows property disappears", () => { var first=BatteryReading.Resolve(100,false,90); var next=BatteryReading.Resolve(88,false,null); Assert(first.Percent==90 && next.Percent==88 && next.Source=="Headphone"); });
Test("High shelf asymptotes and cascaded gain", () => { var p=EqualizerProfile.Flat();p.Bands[7]=new(2,6,3000,0.7071f);Assert(Math.Abs(EqualizerResponse.Decibels(p,20))<0.01);Assert(Math.Abs(EqualizerResponse.Decibels(p,22000)-6)<0.01);p.Preamp=-3;Assert(Math.Abs(EqualizerResponse.Decibels(p,20)+3)<0.01); });
Test("EQ negative and positive gains are reciprocal", () => {foreach(var type in new byte[]{0,1,2}){var p=EqualizerProfile.Flat();p.Bands[0]=new(type,6,1000,0.7071f);var a=EqualizerResponse.Decibels(p,400);p.Bands[0]=p.Bands[0] with{Gain=-6};Assert(Math.Abs(a+EqualizerResponse.Decibels(p,400))<0.0001);}});
Console.WriteLine($"{passed} tests passed.");
if (args.Contains("--hardware"))
{
    var device = BluetoothNative.Discover().FirstOrDefault(d => new string(d.Name.Where(char.IsLetterOrDigit).ToArray()).Equals("NothingHeadphone1", StringComparison.OrdinalIgnoreCase));
    if (device == null) throw new Exception("No paired Headphone (1) available.");
    using var client = new HeadphoneClient();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    Console.WriteLine("HARDWARE Connecting to paired Headphone (1), read-only verification");
    await client.ConnectAsync(device, deadline.Token);
    var rawBattery = await client.RequestAsync(0xC007); Console.WriteLine($"RAW BATTERY {Convert.ToHexString(rawBattery)}"); var battery = DevicePayload.Battery(rawBattery);
    var reported = BatteryReading.Resolve(battery.Percent, battery.Charging, WindowsBattery.Read(device.Address)); Console.WriteLine($"HARDWARE Battery displayed={reported.Percent}%, source={reported.Source}, Windows={reported.WindowsPercent}, NT LINK={reported.ProtocolPercent}, mismatch={reported.Disagrees}");
    Console.WriteLine($"HARDWARE Firmware {await client.ReadFirmwareAsync()}");
    Console.WriteLine($"HARDWARE ANC mode {DevicePayload.Anc(await client.RequestAsync(0xC01E))}");
    Console.WriteLine($"HARDWARE EQ preset {await client.ReadByteAsync(0xC01F)}");
    Console.WriteLine($"HARDWARE Spatial payload {Convert.ToHexString(await client.RequestAsync(0xC04F))}");
    var eq = EqualizerProfile.Decode(await client.RequestAsync(0xC04D, [0]));
    Console.WriteLine($"HARDWARE EQ bands={eq.Bands.Length}; frequencies={string.Join(',', eq.Bands.Select(b=>b.Frequency))}");
    Console.WriteLine($"HARDWARE Latency={await client.ReadByteAsync(0xC041)}, multipoint={await client.ReadByteAsync(0xC027)}, codec preference={await client.ReadByteAsync(0xC029)}");
    if (args.Contains("--verify-anc-write")) { var original = DevicePayload.Anc(await client.RequestAsync(0xC01E)); await client.SetAncAsync(original); Console.WriteLine($"HARDWARE PASS ANC write + ACK + read-back; existing mode {original} preserved"); } Console.WriteLine($"HARDWARE PASS / TX {client.Transmitted}, RX {client.Received}; current listening settings preserved");
}
