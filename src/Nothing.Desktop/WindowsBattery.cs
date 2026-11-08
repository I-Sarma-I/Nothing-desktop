using System.Runtime.InteropServices;

namespace Nothing.Desktop;

public static class WindowsBattery
{
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Id; public uint Pid; }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_List_SizeW(out uint size, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_DevNode_PropertyW(uint node, ref PropertyKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    public static int? Read(ulong address)
    {
        const uint present = 0x100;
        if (CM_Get_Device_ID_List_SizeW(out var length, null, present) != 0 || length > 4_000_000) return null;
        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW(null, buffer, length, present) != 0) return null;
        var mac = address.ToString("X12");
        var candidates = new string(buffer).Split((char)0, StringSplitOptions.RemoveEmptyEntries)
            .Where(id => id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) && id.Contains(mac, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(id => id.Contains("0000111E", StringComparison.OrdinalIgnoreCase));
        foreach (var id in candidates)
        {
            if (CM_Locate_DevNodeW(out var node, id, 0) != 0) continue;
            var key = new PropertyKey { Id = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), Pid = 2 };
            var bytes = new byte[4]; uint size = 4;
            if (CM_Get_DevNode_PropertyW(node, ref key, out var type, bytes, ref size, 0) != 0) continue;
            var value = type switch { 3 when size == 1 => bytes[0], 7 when size == 4 => BitConverter.ToUInt32(bytes), _ => uint.MaxValue };
            if (value <= 100) return (int)value;
        }
        return null;
    }
}
