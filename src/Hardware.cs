using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal sealed class ThinkPad : IDisposable
{
    // N3JET32W DSDT: EC.TMP0, GPUT, HFSP and HSPD. The dual-fan mux
    // follows thinkpad_acpi: change only bit 0 and read tach low byte first.
    private const byte FanMode = 0x2f, FanSelect = 0x31, Cpu = 0x78, Gpu = 0x79;
    internal const byte Level7 = 7, FullSpeed = 0x40, Automatic = 0x80;
    private const byte Data = 0x62, Command = 0x66;
    private readonly SafeFileHandle device;
    private readonly Mutex ecLock = new Mutex(false, @"Global\Access_EC");
    private readonly byte[] read = Payload("ioctl_pio_read", 1);
    private readonly byte[] write = Payload("ioctl_pio_write", 2);
    private readonly byte[] result = new byte[8];
    private const uint LoadModule = (41394u << 16) | (0x821u << 2);
    private const uint CallModule = (41394u << 16) | (0x841u << 2);

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input,
        int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);

    internal static void CheckMachine()
    {
        const string bios = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";
        const string table = @"HKEY_LOCAL_MACHINE\HARDWARE\ACPI\DSDT\LENOVO\ICL_____\00000002";
        var aml = Registry.GetValue(table, "00000000", null) as byte[];
        string product = Convert.ToString(Registry.GetValue(bios, "SystemProductName", ""));
        string family = Convert.ToString(Registry.GetValue(bios, "SystemFamily", ""));
        if (product != "21DDS70700" || family != "ThinkPad P1 Gen 5" ||
            aml == null || Digest(aml) != "DFB70EF5F130A6FA0F3A129929534FD7B0336244169BB67EEB8618F26B212166")
            throw new InvalidOperationException(string.Format("Unsupported hardware or ACPI table. Detected {0} / {1}, BIOS {2}. See the compatibility section in README.md.",
                family, product, Registry.GetValue(bios, "BIOSVersion", "unknown")));
    }
    private static string Digest(byte[] value)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "");
    }
    internal ThinkPad()
    {
        CheckMachine();
        byte[] module = File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LpcACPIEC.bin"));
        if (Digest(module) != "C38FD116E7AFF4D1FDB0A494E296BE0A6708E5A22FC72F14587442FB7F8F7906")
            throw new InvalidDataException("Unexpected PawnIO EC module.");
        device = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (device.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open PawnIO. Install the signed driver and run elevated.");
        try { Io(LoadModule, module, null); }
        catch { Dispose(); throw; }
    }
    private static byte[] Payload(string name, int count)
    {
        byte[] bytes = new byte[32 + count * 8];
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        return bytes;
    }
    private void Io(uint code, byte[] input, byte[] output)
    {
        int size = output == null ? 0 : output.Length;
        int returned;
        if (!DeviceIoControl(device, code, input, input.Length, output, size, out returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Signed PawnIO EC module call failed.");
        if (returned != size) throw new IOException("Incomplete EC response.");
    }
    private byte In(byte port) { read[32] = port; Io(CallModule, read, result); return result[0]; }
    private void Out(byte port, byte value) { write[32] = port; write[40] = value; Io(CallModule, write, null); }
    private void Wait(int bits, int expected)
    {
        var elapsed = Stopwatch.StartNew();
        while ((In(Command) & bits) != expected)
        {
            if (elapsed.ElapsedMilliseconds >= 120) throw new TimeoutException(
                "EC handshake timed out waiting for mask " + bits + " value " + expected + ".");
            // EC replies usually arrive in microseconds. A Windows scheduler
            // quantum between handshake steps needlessly exposes a half-read
            // transaction to concurrent firmware/ACPI activity.
            if (elapsed.ElapsedMilliseconds < 2) Thread.SpinWait(16);
            else Thread.Sleep(1);
        }
    }
    private void Ready()
    {
        // Give the OS time to finish an outstanding transaction. If OBF is still
        // latched after that interval, consume one stale reply before retrying.
        try { Wait(3, 0); }
        catch (TimeoutException) {
            if ((In(Command) & 3) != 1) throw;
            In(Data);
            Wait(3, 0);
        }
    }
    private byte Register(byte address)
    {
        for (int attempt = 0; ; attempt++)
        {
            try {
                Ready(); Out(Command, 0x80); Wait(2, 0); Out(Data, address); Wait(1, 1);
                return In(Data);
            } catch (TimeoutException ex) {
                if (attempt == 2) throw new TimeoutException("Could not read EC field 0x" + address.ToString("X2") + ": " + ex.Message, ex);
                Thread.Sleep(10 + 10 * attempt);
            }
        }
    }
    private void Put(byte address, byte value)
    {
        Ready(); Out(Command, 0x81); Wait(2, 0); Out(Data, address);
        Wait(2, 0); Out(Data, value); Wait(2, 0);
    }
    private void Select(int fan)
    {
        byte before = Register(FanSelect);
        byte after = (byte)((before & 0xfe) | fan);
        if (before != after) {
            if (Register(FanSelect) != before) throw new TimeoutException("Fan selector changed during read-modify-write.");
            Put(FanSelect, after);
        }
    }
    private int Tachometer()
    {
        // The EC latches the high byte on a low-byte read. Cross-check two
        // full pairs to reject a torn reading on this shared ACPI interface.
        int previous = -1;
        for (int i = 0; i < 5; i++)
        {
            int lo = Register(0x84), hi = Register(0x85);
            int rpm = lo + (hi << 8);
            if (rpm < 12000 && previous >= 0 && Math.Abs(rpm - previous) <= Math.Max(100, rpm / 30))
                return rpm;
            previous = rpm < 12000 ? rpm : -1;
        }
        throw new TimeoutException("Fan tachometer did not provide two consistent RPM readings.");
    }
    private void Locked(Action action)
    {
        try { if (!ecLock.WaitOne(750)) throw new TimeoutException("EC is in use."); }
        catch (AbandonedMutexException) { }
        try { action(); } finally { ecLock.ReleaseMutex(); }
    }
    internal sealed class Sample
    {
        internal int CpuC, GpuC;
        internal readonly int[] Rpm = new int[2], Mode = new int[2];
        internal bool BothAuto { get { return Mode.All(m => (m & 0xf8) == Automatic); } }
        internal bool BothAt(byte target) { return Mode.All(m => m == target); }
        public override string ToString()
        {
            return string.Format("CPU={0}C GPU_EC={1} RPM={2}/{3} mode=0x{4:X2}/0x{5:X2}",
                CpuC, GpuC, Rpm[0], Rpm[1], Mode[0], Mode[1]);
        }
    }
    internal Sample Read()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return ReadOnce(); }
            catch (TimeoutException) { if (attempt == 2) throw; Thread.Sleep(60 * (attempt + 1)); }
        }
    }
    private Sample ReadOnce()
    {
        var s = new Sample();
        Locked(delegate {
            s.CpuC = Register(Cpu); s.GpuC = Register(Gpu);
            try {
                for (int fan = 1; fan >= 0; fan--)
                {
                    Select(fan);
                    s.Rpm[fan] = Tachometer();
                    s.Mode[fan] = Register(FanMode);
                }
            } finally { Select(0); }
        });
        return s;
    }
    internal bool HasLatchedManualMode()
    {
        bool found = false;
        Locked(delegate {
            try {
                for (int fan = 1; fan >= 0; fan--) {
                    Select(fan);
                    byte mode = Register(FanMode);
                    found |= mode == Level7 || mode == FullSpeed;
                }
            } finally { Select(0); }
        });
        return found;
    }
    internal void SetBoth(byte mode)
    {
        if (mode != Level7 && mode != FullSpeed && mode != Automatic) throw new ArgumentOutOfRangeException("mode");
        Locked(delegate {
            Exception error = null;
            try {
                for (int fan = 1; fan >= 0; fan--)
                {
                    try {
                        Select(fan); Put(FanMode, mode);
                        // Seek the same bank again before checking the command.
                        Select(fan);
                        byte actual = Register(FanMode);
                        if (mode == Automatic ? (actual & 0xf8) != Automatic : actual != mode)
                            throw new IOException("Fan " + (fan + 1) + " rejected mode " + mode + " (read " + actual + ").");
                    } catch (Exception ex) { error = ex; }
                }
            } finally { Select(0); }
            if (error != null) throw error;
        });
    }
    public void Dispose() { if (device != null) device.Dispose(); ecLock.Dispose(); }
}
