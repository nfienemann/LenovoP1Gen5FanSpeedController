using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.ServiceProcess;
using System.Threading;

internal sealed class FanPolicy
{
    private double? coolSince;
    internal Settings Limits = Settings.Default;
    internal void Reset() { coolSince = null; High = false; }
    internal void Configure(Settings limits)
    {
        if (Limits.EngageAbove != limits.EngageAbove || Limits.ReleaseAt != limits.ReleaseAt ||
            Limits.HoldSeconds != limits.HoldSeconds) coolSince = null;
        Limits = limits;
    }
    internal byte TargetMode
    {
        get {
            if (!High) return ThinkPad.Automatic;
            return Limits.FullSpeed ? ThinkPad.FullSpeed : ThinkPad.Level7;
        }
    }
    internal bool High { get; private set; }
    internal static bool Valid(int c) { return c >= 1 && c < 128; }
    internal bool Update(int cpu, int gpu, double now)
    {
        bool hot = (Valid(cpu) && cpu > Limits.EngageAbove) || (Valid(gpu) && gpu > Limits.EngageAbove);
        if (hot) High = true;
        if (!Valid(cpu) || !Valid(gpu))
        {
            coolSince = null;
            // When one sensor is missing the other can still request maximum.
            // Otherwise yield to the firmware, which also knows the GPU power state.
            High = hot;
        }
        else if (High && cpu <= Limits.ReleaseAt && gpu <= Limits.ReleaseAt)
        {
            if (!coolSince.HasValue) coolSince = now;
            if (now - coolSince.Value >= Limits.HoldSeconds) High = false;
        }
        else coolSince = null;
        return High;
    }
}

// A two-fan write can fail after the first fan accepted the command. In that
// case the next automatic handoff must run even if the last known mode was auto.
internal sealed class FanOutput
{
    private readonly Action<byte> write;
    internal byte Mode { get; private set; }
    internal bool NeedsRestore { get; private set; }
    internal FanOutput(Action<byte> write) { this.write = write; Mode = ThinkPad.Automatic; }
    internal bool Apply(byte target, ThinkPad.Sample feedback = null)
    {
        if (!NeedsRestore && Mode == target &&
            (target == ThinkPad.Automatic || feedback == null || feedback.BothAt(target))) return false;
        NeedsRestore = true;
        write(target);
        Mode = target;
        NeedsRestore = false;
        return true;
    }
}

internal static class Nvidia
{
    // Read the discrete GPU through NVIDIA's display driver.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr handle);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr handle, uint sensor, out uint c);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    internal static int Temperature()
    {
        try {
            if (nvmlInit_v2() != 0) return -1;
            try {
                IntPtr gpu; uint value;
                return nvmlDeviceGetHandleByIndex_v2(0, out gpu) == 0 &&
                    nvmlDeviceGetTemperature(gpu, 0, out value) == 0 ? (int)value : -1;
            } finally { nvmlShutdown(); }
        } catch (DllNotFoundException) { return -1; }
          catch (EntryPointNotFoundException) { return -1; }
    }
}

internal static class Controller
{
    internal const string Name = "ThinkPadP1FanPolicy";
    private static readonly object logLock = new object();
    internal static void Log(string message)
    {
        string line = DateTimeOffset.Now.ToString("o") + " " + message;
        Console.WriteLine(line);
        lock (logLock)
        {
            try {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "controller.log");
                if (File.Exists(file) && new FileInfo(file).Length > 2000000)
                {
                    File.Copy(file, file + ".previous", true);
                    File.Delete(file);
                }
                File.AppendAllText(file, line + Environment.NewLine);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    internal static void Restore(ThinkPad pc)
    {
        Exception last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { pc.SetBoth(ThinkPad.Automatic); return; }
            catch (Exception ex) { last = ex; Thread.Sleep(250); }
        }
        throw new IOException("Could not verify automatic mode on both fans.", last);
    }
    internal static void CheckForOtherController()
    {
        if (Process.GetProcessesByName("TPFanControl").Length > 0 ||
            Process.GetProcessesByName("FanControl").Length > 0 ||
            Process.GetProcessesByName("FanControl.Service").Length > 0)
            throw new InvalidOperationException("An active FanControl or TPFanControl process must release the fans before the hardware test.");
    }
    private static void Validate(ThinkPad pc)
    {
        CheckForOtherController();
        var first = pc.Read();
        Log("Baseline " + first);
        if (!first.BothAuto) throw new IOException("Expected Lenovo automatic mode before commissioning.");
        int gpuReference = Nvidia.Temperature();
        Log("Independent NVIDIA reading " + gpuReference + " C.");
        if (!FanPolicy.Valid(first.CpuC) || (!FanPolicy.Valid(first.GpuC) && !FanPolicy.Valid(gpuReference)))
            throw new IOException("Temperature inputs could not be validated.");
        if (FanPolicy.Valid(first.GpuC) && FanPolicy.Valid(gpuReference) && Math.Abs(first.GpuC - gpuReference) > 12)
            throw new IOException("GPU EC sensor disagrees with NVIDIA. Check sensor mapping.");
        // Windows THM0 evaluates TMP0 in this machine's DSDT.
        using (var search = new System.Management.ManagementObjectSearcher(
            "SELECT Name,HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
        using (var counters = search.Get())
        {
            var zone = counters.Cast<System.Management.ManagementObject>().First(z =>
                Convert.ToString(z["Name"]).EndsWith("THM0", StringComparison.OrdinalIgnoreCase));
            double reference = Convert.ToDouble(zone["HighPrecisionTemperature"]) / 10.0 - 273.2;
            Log("Windows THM0 " + reference.ToString("F1", CultureInfo.InvariantCulture) + " C.");
            if (Math.Abs(reference - first.CpuC) > 20) throw new IOException("CPU thermal-zone readings disagree.");
        }
        int[] baseline = (int[])first.Rpm.Clone();
        for (int i = 0; i < 3; i++)
        {
            Thread.Sleep(1000);
            var sample = pc.Read();
            if (!sample.BothAuto || sample.Rpm.Any(r => r < 0 || r >= 12000))
                throw new IOException("Invalid baseline fan data: " + sample);
            for (int f = 0; f < 2; f++) baseline[f] = Math.Min(baseline[f], sample.Rpm[f]);
        }
        int consecutive = 0;
        var recent = new System.Collections.Generic.Queue<int[]>();
        bool separate = false;
        try {
            pc.SetBoth(ThinkPad.Level7);
            for (int i = 0; i < 55; i++)
            {
                Thread.Sleep(1000);
                var s = pc.Read();
                Log("Level 7 test " + s);
                if (!s.BothAt(ThinkPad.Level7)) throw new IOException("Firmware did not retain the regulated level 7 command.");
                if (s.Rpm.Any(r => r >= 12000)) throw new IOException("Invalid tachometer reading.");
                separate |= Math.Abs(s.Rpm[0] - s.Rpm[1]) > 40;
                bool response = Enumerable.Range(0, 2).All(f => s.Rpm[f] > 1500 && s.Rpm[f] >= baseline[f] + 150);
                consecutive = response ? consecutive + 1 : 0;
                recent.Enqueue(s.Rpm);
                if (recent.Count > 6) recent.Dequeue();
                bool settled = recent.Count == 6 && Enumerable.Range(0, 2).All(f =>
                    recent.Max(pair => pair[f]) - recent.Min(pair => pair[f]) < s.Rpm[f] * 0.04);
                if (i >= 12 && separate && consecutive >= 6 && settled) return;
            }
            throw new IOException("Both tachometers did not independently demonstrate a sustained speed increase. Service was not enabled.");
        } finally {
            Restore(pc);
            Log("Both automatic-mode writes verified; checking tachometers after handoff.");
            var after = pc.Read();
            Log("Returned to Lenovo " + after);
            if (!after.BothAuto) throw new IOException("Automatic handoff did not persist.");
        }
    }
    private static void TestPolicy()
    {
        var p = new FanPolicy();
        if (p.Update(70, 60, 0) || !p.Update(71, 60, 1) || !p.Update(68, 65, 2) ||
            !p.Update(65, 65, 3) || !p.Update(65, 65, 17) || !p.Update(65, 66, 18) ||
            !p.Update(65, 65, 19) || !p.Update(65, 65, 33) || p.Update(65, 65, 34) ||
            !p.Update(45, 71, 35) || !p.Update(128, 71, 36) || p.Update(128, 65, 37) ||
            p.Update(60, 255, 38) || !p.Update(71, 255, 39))
            throw new IOException("Policy boundary regression.");
        var custom = Settings.Edit(Settings.Default, new[] {"config","set","--engage-above","75","--release-at","69","--hold-seconds","3"});
        p.Reset(); p.Configure(Settings.Decode(custom.Encode()));
        if (p.Update(75,65,0) || !p.Update(60,76,1) || !p.Update(69,69,2) || p.Update(69,69,5))
            throw new IOException("Custom threshold regression.");
        p.Update(76,60,6); p.Update(68,68,7);
        p.Configure(Settings.Edit(custom, new[] {"config","set","--hold-seconds","0"}));
        if (!p.High || p.Update(68,68,8)) throw new IOException("Live reconfiguration regression.");
        foreach (string bad in new[] {"65/65/1","70/71/0","70/65/-1","999/60/0","broken"}) {
            try { Settings.Decode(bad); } catch (ArgumentException) { continue; } catch (InvalidDataException) { continue; }
            throw new IOException("Accepted invalid config " + bad);
        }
        Console.WriteLine("PASS: CPU/GPU entry, 70 C boundary, dead band, 15 s dwell, interrupted dwell, unavailable sensors.");
        TestFanModes();
    }
    private static void TestFanModes()
    {
        var legacy = Settings.Decode("70/65/15");
        var faster = Settings.Edit(legacy, new[] { "config", "set", "--fan-mode", "full-speed" });
        var regulated = Settings.Edit(faster, new[] { "config", "set", "--fan-mode", "level7" });
        if (Settings.Decode(null).FullSpeed || legacy.FullSpeed || regulated.Encode() != legacy.Encode() ||
            faster.Encode() != "70/65/15/full-speed" || !Settings.Decode(faster.Encode()).FullSpeed ||
            !Settings.Edit(faster, new[] {"config","set","--hold-seconds","20"}).FullSpeed)
            throw new IOException("Fan-mode persistence or legacy migration regression.");
        foreach (string bad in new[] {"64", "0x40", "full", ""}) {
            try { Settings.Edit(legacy, new[] {"config","set","--fan-mode",bad}); }
            catch (ArgumentException) { continue; }
            throw new IOException("Accepted an unknown fan mode.");
        }
        var policy = new FanPolicy();
        var writes = new System.Collections.Generic.List<byte>();
        var output = new FanOutput(writes.Add);
        // Only an in-memory action sees these commands. No hardware or registry is opened.
        policy.Configure(faster);
        policy.Update(70, 70, 0); output.Apply(policy.TargetMode);
        policy.Update(68, 71, 1); output.Apply(policy.TargetMode);
        policy.Update(67, 68, 2); output.Apply(policy.TargetMode);
        var feedback = new ThinkPad.Sample();
        feedback.Mode[0] = feedback.Mode[1] = ThinkPad.FullSpeed;
        output.Apply(policy.TargetMode, feedback);
        feedback.Mode[1] = ThinkPad.Level7;
        output.Apply(policy.TargetMode, feedback);
        policy.Update(65, 65, 3);
        policy.Configure(regulated); output.Apply(policy.TargetMode);
        policy.Configure(faster); output.Apply(policy.TargetMode);
        policy.Update(65, 65, 17); output.Apply(policy.TargetMode);
        policy.Update(65, 65, 18); output.Apply(policy.TargetMode);
        policy.Configure(Settings.Default);
        policy.Update(71, 65, 19); output.Apply(policy.TargetMode);
        if (!writes.SequenceEqual(new byte[] {0x40, 0x40, 7, 0x40, 0x80, 7}))
            throw new IOException("Unexpected fan command sequence in simulation.");
        bool restored = false;
        var partial = new FanOutput(delegate(byte mode) {
            if (mode == ThinkPad.FullSpeed) throw new IOException("Simulated partial write.");
            restored = mode == ThinkPad.Automatic;
        });
        try { partial.Apply(ThinkPad.FullSpeed); } catch (IOException) { }
        if (!partial.NeedsRestore) throw new IOException("Lost partial fan write.");
        partial.Apply(ThinkPad.Automatic);
        if (!restored || partial.NeedsRestore) throw new IOException("Automatic-mode recovery failed in simulation.");
        Console.WriteLine("PASS: level7 default, full-speed opt-in, persistence, live opt-out, hysteresis, readback and recovery (simulated).");
    }
    public static int Main(string[] args)
    {
        try {
            if (args.Length == 0 || (args.Length == 1 && (args[0] == "help" || args[0] == "--help"))) {
                Console.WriteLine("Unofficial ThinkPad P1 Gen 5 fan controller\n" +
                    "identify        Check model and firmware compatibility\nselftest        Test the policy without hardware access\n" +
                    "config show     Display the saved machine-wide policy (defaults before installation)\n" +
                    "config set      Change one or more values from Administrator PowerShell\n" +
                    "                --engage-above C  --release-at C  --hold-seconds SECONDS\n" +
                    "                --fan-mode level7|full-speed (default: level7)\n" +
                    "                Full-speed disables closed-loop RPM regulation and can exceed hardware limits.\n" +
                    "config reset    Restore level7 and 70 C / 65 C / 15 seconds (administrator)\n" +
                    "Use Setup.ps1 for installation or removal.");
                return 0;
            }
            if (args[0] == "config") { Settings.Command(args); return 0; }
            if (args.Length == 1 && args[0] == "selftest") { TestPolicy(); return 0; }
            if (args.Length == 1 && args[0] == "identify") { ThinkPad.CheckMachine(); Console.WriteLine("Matched inspected P1 Gen 5 ACPI table."); return 0; }
            if (args.Length == 1 && args[0] == "validate") {
                using (var svc = new ServiceController(Name)) {
                    ServiceControllerStatus status;
                    try { status = svc.Status; }
                    catch (InvalidOperationException ex) {
                        var reason = ex.InnerException as System.ComponentModel.Win32Exception;
                        if (reason == null || reason.NativeErrorCode != 1060) throw;
                        status = ServiceControllerStatus.Stopped;
                    }
                    if (status != ServiceControllerStatus.Stopped) throw new IOException("Stop the fan service before a hardware test.");
                }
                using (var pc = new ThinkPad()) Validate(pc); return 0;
            }
            if (args.Length == 1 && args[0] == "service") { ServiceBase.Run(new FanService()); return 0; }
            Console.Error.WriteLine("Use the included installer to run the hardware validation and enable the service.");
            return 2;
        } catch (UnauthorizedAccessException ex) { Console.Error.WriteLine("Access denied. Run commands that change settings from Administrator PowerShell. " + ex.Message); return 1; }
          catch (SecurityException ex) { Console.Error.WriteLine("Administrator PowerShell is required for this operation. " + ex.Message); return 1; }
          catch (Exception ex) {
              if (args.Length > 0 && (args[0] == "validate" || args[0] == "service")) Log("ERROR " + ex.Message);
              else Console.Error.WriteLine(ex.Message);
              return 1;
          }
    }
}

internal sealed class FanService : ServiceBase
{
    private readonly object gate = new object();
    private readonly ManualResetEvent stop = new ManualResetEvent(false);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private Thread thread;
    private ThinkPad pc;
    private readonly FanPolicy policy = new FanPolicy();
    private string previousSettings;
    private bool settingsLoaded;
    private bool suspended, checkStartupMode = true;
    private FanOutput output;
    private double lastTick = -10, lastLogged = -100, commandTime, lastFanCheck = -100;
    private PerformanceCounter thermal;
    internal FanService()
    {
        ServiceName = Controller.Name;
        CanShutdown = true; CanHandlePowerEvent = true; AutoLog = false;
    }
    protected override void OnStart(string[] args)
    {
        pc = new ThinkPad();
        output = new FanOutput(delegate(byte mode) {
            if (mode == ThinkPad.Automatic) Controller.Restore(pc);
            else pc.SetBoth(mode);
        });
        thermal = new PerformanceCounter("Thermal Zone Information", "High Precision Temperature", @"\_TZ.THM0", true);
        thread = new Thread(Run) { IsBackground = true };
        thread.Start();
    }
    private void Relinquish()
    {
        policy.Reset();
        if (output.Apply(ThinkPad.Automatic)) {
            Controller.Log("Released both fans to Lenovo.");
        }
    }
    private void Tick()
    {
        double now = clock.Elapsed.TotalSeconds;
        if (output.NeedsRestore || now - lastTick > 6) Relinquish();
        lastTick = now;
        if (checkStartupMode) {
            // An interrupted two-fan write can leave just one fan latched.
            // Recover before reading temperatures or relying on tachometers.
            if (pc.HasLatchedManualMode()) Controller.Restore(pc);
            checkStartupMode = false;
        }
        string raw = Settings.ReadRaw();
        if (!settingsLoaded || raw != previousSettings) {
            try {
                var limits = Settings.Decode(raw);
                policy.Configure(limits);
                Controller.Log("Applied policy: " + limits);
            } catch (Exception ex) {
                var last = policy.Limits;
                policy.Configure(new Settings(last.EngageAbove, last.ReleaseAt, last.HoldSeconds));
                Controller.Log("Invalid saved policy. Using level7 with the last valid thresholds. " + ex.Message);
            }
            previousSettings = raw;
            settingsLoaded = true;
        }
        // Normal thermal polling uses the operating system and display driver.
        // Only fall back to the EC or access the fan mux when needed.
        int cpu = -1;
        try { cpu = (int)Math.Round((thermal.RawValue - 2732) / 10.0); }
        catch (InvalidOperationException) { }
        int gpu = Nvidia.Temperature();
        ThinkPad.Sample feedback = null;
        if (!FanPolicy.Valid(cpu) || !FanPolicy.Valid(gpu))
        {
            feedback = pc.Read();
            if (!FanPolicy.Valid(cpu)) cpu = feedback.CpuC;
            if (!FanPolicy.Valid(gpu)) gpu = feedback.GpuC;
        }
        bool high = policy.Update(cpu, gpu, now);
        byte target = policy.TargetMode;
        if (high && output.Mode == target && now - lastFanCheck >= 6)
        {
            feedback = feedback ?? pc.Read();
            lastFanCheck = now;
            if (now - commandTime > 15 && feedback.Rpm.Any(r => r < 500 || r >= 12000))
                throw new IOException("Fan tachometer feedback lost at maximum.");
        }
        if (high && output.Apply(target, feedback))
        {
            commandTime = now; lastFanCheck = now;
            Controller.Log("Engaged " + policy.Limits.FanModeName + ". CPU=" + cpu + "C GPU=" + gpu + "C.");
        }
        else if (!high && output.Mode != ThinkPad.Automatic) Relinquish();
        if (now - lastLogged >= 30)
        {
            var state = output.Mode == ThinkPad.Automatic ? "AUTO" : (output.Mode == ThinkPad.FullSpeed ? "FULL" : "MAX");
            feedback = pc.Read();
            Controller.Log(state + " Windows CPU=" + cpu + "C NVIDIA/effective GPU=" + gpu + "C. " + feedback);
            lastLogged = now;
        }
    }
    private void Run()
    {
        try {
            do {
                lock (gate)
                {
                    if (!suspended) try { Tick(); }
                    catch (Exception ex) {
                        Controller.Log("Sample or control failure: " + ex.Message);
                        try { Relinquish(); } catch (Exception e) { Controller.Log(e.Message); }
                    }
                }
            } while (!stop.WaitOne(2000));
        } finally {
            lock (gate) { try { Relinquish(); } catch (Exception e) { Controller.Log(e.Message); } }
        }
    }
    protected override bool OnPowerEvent(PowerBroadcastStatus status)
    {
        lock (gate)
        {
            if (status == PowerBroadcastStatus.Suspend)
            {
                suspended = true;
                try { Relinquish(); } catch (Exception e) { Controller.Log(e.Message); }
            }
            else if (status == PowerBroadcastStatus.ResumeAutomatic || status == PowerBroadcastStatus.ResumeSuspend ||
                     status == PowerBroadcastStatus.ResumeCritical) { suspended = false; lastTick = -10; }
        }
        return true;
    }
    protected override void OnStop()
    {
        stop.Set();
        if (thread != null) thread.Join();
        if (thermal != null) thermal.Dispose();
        if (pc != null) pc.Dispose();
    }
    protected override void OnShutdown() { OnStop(); }
}
