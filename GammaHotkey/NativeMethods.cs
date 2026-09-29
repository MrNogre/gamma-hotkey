using System.Runtime.InteropServices;

namespace GammaHotkey;

internal static class NativeMethods
{
    internal const int WmHotkey = 0x0312;
    internal const int WmDisplayChange = 0x007E;
    internal const int WmInputLangChange = 0x0051;
    internal const uint ModNoRepeat = 0x4000;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PathSource
    {
        public long Adapter;
        public uint Id, Mode, Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PathTarget
    {
        public long Adapter;
        public uint Id, Mode, Output, Rotation, Scaling, RefreshNumerator, RefreshDenominator;
        public uint Scanline, Available, Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DisplayPath
    {
        public PathSource Source;
        public PathTarget Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DeviceInfoHeader
    {
        public uint Type, Size;
        public long Adapter;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        public DeviceInfoHeader Header;
        public uint Flags, Output;
        public ushort Manufacturer, Product;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths,
        ref uint modeCount, nint modes, nint topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceName name);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetName name);

    internal static string DisplayLabel(string deviceName)
    {
        const uint activePaths = 2;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (GetDisplayConfigBufferSizes(activePaths, out uint pathCount, out uint modeCount) != 0) break;
            var paths = new DisplayPath[pathCount];
            var modes = new byte[Math.Max(1, checked((int)modeCount * 64))];
            var handle = GCHandle.Alloc(modes, GCHandleType.Pinned);
            int result;
            try { result = QueryDisplayConfig(activePaths, ref pathCount, paths, ref modeCount,
                handle.AddrOfPinnedObject(), 0); }
            finally { handle.Free(); }
            if (result == 122) continue;
            if (result != 0) break;
            foreach (var path in paths.Take((int)pathCount))
            {
                var source = new SourceName { Header = new DeviceInfoHeader
                {
                    Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(),
                    Adapter = path.Source.Adapter, Id = path.Source.Id
                } };
                if (DisplayConfigGetDeviceInfo(ref source) != 0 ||
                    !string.Equals(source.Name, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                var target = new TargetName { Header = new DeviceInfoHeader
                {
                    Type = 2, Size = (uint)Marshal.SizeOf<TargetName>(),
                    Adapter = path.Target.Adapter, Id = path.Target.Id
                } };
                if (DisplayConfigGetDeviceInfo(ref target) == 0 &&
                    !string.IsNullOrWhiteSpace(target.Name))
                    return FormatDisplayLabel(deviceName, target.Name);
            }
            break;
        }
        return FormatDisplayLabel(deviceName, null);
    }

    internal static string FormatDisplayLabel(string deviceName, string? description)
    {
        const string prefix = @"\\.\DISPLAY";
        string display = deviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(deviceName.AsSpan(prefix.Length), out int number)
            ? $"DISPLAY {number}" : deviceName;
        return !string.IsNullOrWhiteSpace(description) &&
            !description.StartsWith("Generic ", StringComparison.OrdinalIgnoreCase)
            ? $"{display} - {description.Trim()}" : display;
    }

    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateDC(string driver, string device, nint port, nint data);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDeviceGammaRamp(nint dc, nint ramp);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDeviceGammaRamp(nint dc, nint ramp);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint thread);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, nint layout);

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState,
        [Out, MarshalAs(UnmanagedType.LPArray)] char[] buffer, int bufferSize, uint flags, nint layout);

    // RegisterHotKey matches modifiers exactly, so only Ctrl+Alt(+Shift) without Win collides with AltGr typing.
    // Returns the character the key types with AltGr on the current layout, or null if nothing is blocked.
    internal static string? AltGrCharacter(uint modifiers, uint virtualKey)
    {
        if ((modifiers & 0xB) != 0x3 || virtualKey >= 0x70) return null;
        nint layout = GetKeyboardLayout(0);
        var state = new byte[256];
        state[0x11] = state[0x12] = 0x80;
        if ((modifiers & 0x4) != 0) state[0x10] = 0x80;
        var buffer = new char[8];
        // Flag 0x4 keeps the keyboard's dead-key state untouched; a negative count means a dead key, which is still blocked.
        int count = ToUnicodeEx(virtualKey, MapVirtualKeyEx(virtualKey, 0, layout), state, buffer, buffer.Length, 0x4, layout);
        return count != 0 && !char.IsControl(buffer[0]) ? new string(buffer, 0, Math.Max(count, 1)) : null;
    }

    internal static bool Read(nint dc, out GammaRamp? ramp)
    {
        var buffer = new ushort[GammaRamp.Length];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bool ok = GetDeviceGammaRamp(dc, handle.AddrOfPinnedObject());
            ramp = ok ? new GammaRamp(buffer) : null;
            return ok;
        }
        finally { handle.Free(); }
    }

    internal static bool Write(nint dc, GammaRamp ramp)
    {
        var buffer = ramp.ToArray();
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { return SetDeviceGammaRamp(dc, handle.AddrOfPinnedObject()); }
        finally { handle.Free(); }
    }
}
