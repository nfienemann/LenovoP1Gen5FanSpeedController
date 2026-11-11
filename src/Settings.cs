using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Win32;

internal sealed class Settings
{
    internal const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\ThinkPadP1FanPolicy";
    private const string ValueName = "TemperaturePolicyV1";
    internal static readonly Settings Default = new Settings(70, 65, 15);
    internal readonly int EngageAbove, ReleaseAt, HoldSeconds;
    internal readonly bool FullSpeed;
    internal string FanModeName { get { return FullSpeed ? "full-speed" : "level7"; } }

    internal Settings(int engage, int release, int hold, bool fullSpeed = false)
    {
        if (release < 1 || engage > 126 || release >= engage)
            throw new ArgumentException("Use 1 <= release-at < engage-above <= 126 degrees C.");
        if (hold < 0) throw new ArgumentException("hold-seconds must be zero or greater.");
        EngageAbove = engage; ReleaseAt = release; HoldSeconds = hold; FullSpeed = fullSpeed;
    }
    private static int Number(string s)
    {
        int result;
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out result))
            throw new ArgumentException("Expected a nonnegative whole number, got '" + s + "'.");
        return result;
    }
    internal string Encode()
    {
        string thermal = string.Format(CultureInfo.InvariantCulture, "{0}/{1}/{2}", EngageAbove, ReleaseAt, HoldSeconds);
        // Keep the original v1.0 representation for every regulated policy.
        return FullSpeed ? thermal + "/full-speed" : thermal;
    }
    private static bool ParseMode(string name)
    {
        if (name == "level7") return false;
        if (name == "full-speed") return true;
        throw new ArgumentException("Expected --fan-mode level7 or full-speed.");
    }
    internal static Settings Decode(string raw)
    {
        if (raw == null) return Default;
        string[] fields = raw.Split('/');
        if (fields.Length != 3 && fields.Length != 4) throw new InvalidDataException("Invalid saved policy. Run config reset.");
        return new Settings(Number(fields[0]), Number(fields[1]), Number(fields[2]), fields.Length == 4 && ParseMode(fields[3]));
    }
    internal static string ReadRaw()
    {
        using (var key = Registry.LocalMachine.OpenSubKey(ServiceKey + @"\Parameters"))
        {
            object value = key == null ? null : key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value == null ? null : (value as string ?? "invalid registry value type");
        }
    }
    internal static Settings Edit(Settings previous, string[] args)
    {
        if (args.Length < 4 || args.Length % 2 != 0) throw new ArgumentException("Supply one or more option/value pairs. See help.");
        int engage = previous.EngageAbove, release = previous.ReleaseAt, hold = previous.HoldSeconds;
        bool fullSpeed = previous.FullSpeed;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 2; i < args.Length; i += 2)
        {
            if (!seen.Add(args[i])) throw new ArgumentException("Duplicate option " + args[i]);
            string value = args[i + 1];
            switch (args[i])
            {
                case "--engage-above": engage = Number(value); break;
                case "--release-at": release = Number(value); break;
                case "--hold-seconds": hold = Number(value); break;
                case "--fan-mode": fullSpeed = ParseMode(value); break;
                default: throw new ArgumentException("Unknown option " + args[i]);
            }
        }
        return new Settings(engage, release, hold, fullSpeed);
    }
    public override string ToString()
    {
        return string.Format("Engage above {0} C | Release at or below {1} C | Hold cool for {2} seconds | Fan mode: {3}",
            EngageAbove, ReleaseAt, HoldSeconds, FanModeName);
    }
    internal static void Command(string[] args)
    {
        if (args.Length == 2 && args[1] == "show") { Console.WriteLine(Decode(ReadRaw())); return; }
        bool reset = args.Length == 2 && args[1] == "reset";
        if (!reset && (args.Length < 2 || args[1] != "set")) throw new ArgumentException("Use config show, set, or reset. See help.");
        using (var service = Registry.LocalMachine.OpenSubKey(ServiceKey, true))
        {
            if (service == null) throw new InvalidOperationException("Install the service before saving settings.");
            using (var mutex = new Mutex(false, @"Global\ThinkPadP1FanPolicy.Settings"))
            {
                try { if (!mutex.WaitOne(5000)) throw new IOException("Another configuration command is busy. Try again."); }
                catch (AbandonedMutexException) { }
                try {
                    Settings settings = reset ? Default : Edit(Decode(ReadRaw()), args);
                    using (var key = service.CreateSubKey("Parameters")) {
                        if (reset) key.DeleteValue(ValueName, false);
                        else key.SetValue(ValueName, settings.Encode(), RegistryValueKind.String);
                    }
                    Console.WriteLine(settings);
                } finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
