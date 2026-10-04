using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace VideoArchive.Core;

public sealed class CapabilityCache(string ffmpeg, string path,
    Func<CancellationToken, Task<string>>? environmentIdentity = null,
    Func<CancellationToken, Task<List<Capability>>>? detect = null)
{
    public bool FromCache { get; private set; }
    public string Note { get; private set; } = "";
    sealed class Entry
    {
        public int Version { get; set; } = 1;
        public string Identity { get; set; } = "";
        public DateTime CheckedUtc { get; set; }
        public List<Capability> Capabilities { get; set; } = [];
    }
    static bool Complete(List<Capability> values) => values.Count == 12 &&
        new[] { "CPU", "NVIDIA", "Intel", "AMD" }.All(b => new[] { "H.264", "HEVC", "AV1" }.All(c =>
            values.Count(v => v.Backend == b && v.Codec == c && v.Encoder == CapabilityService.Encoder(b, c)) == 1)) &&
        values.Any(v => v.Available) && values.All(v => v.Error != "检测超时");

    public async Task<List<Capability>> GetAsync(bool force, CancellationToken ct)
    {
        FromCache = false;
        string identity;
        try { identity = await (environmentIdentity?.Invoke(ct) ?? IdentityAsync(ffmpeg, ct)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { identity = "uncacheable-" + Guid.NewGuid(); }
        if (!force)
        {
            try
            {
                var entry = JsonStore.Read<Entry>(path);
                if (entry?.Version == 1 && entry.Identity == identity && Complete(entry.Capabilities))
                { FromCache = true; Note = $"环境未变化，复用 {entry.CheckedUtc.ToLocalTime():yyyy-MM-dd HH:mm} 的检测结果"; return entry.Capabilities; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        var result = await (detect?.Invoke(ct) ?? new CapabilityService(ffmpeg).DetectAsync(ct));
        ct.ThrowIfCancellationRequested();
        Note = "已完成实际短帧检测";
        if (Complete(result) && !identity.StartsWith("uncacheable-"))
        {
            try { JsonStore.Write(path, new Entry { Identity = identity, CheckedUtc = DateTime.UtcNow, Capabilities = result }); Note += "，已保存环境缓存"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Note += "；缓存未保存：" + ex.Message; }
        }
        else Note += "；检测不完整，下一次启动会重试";
        return result;
    }

    // Hash the actual encoder binary, rather than relying on its path or version label.
    // Registry reads avoid launching a second hardware-detection process at startup.
    public static async Task<string> IdentityAsync(string ffmpeg, CancellationToken ct)
    {
        var binary = await Files.HashAsync(ffmpeg, ct);
        var environment = await Task.Run(() =>
        {
            var parts = new List<string> { "capability-policy-1", binary, Environment.MachineName,
                Environment.UserName, Environment.OSVersion.VersionString, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.GetEnvironmentVariable("SESSIONNAME") ?? "", Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "" };
            if (OperatingSystem.IsWindows())
            {
                using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                void Read(string keyPath, params string[] names)
                {
                    using var key = registry.OpenSubKey(keyPath);
                    if (key == null) throw new IOException("硬件环境信息无法读取");
                    parts.Add(keyPath);
                    foreach (var name in names) parts.Add(name + "=" + Convert.ToString(key.GetValue(name)));
                }
                Read(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid");
                Read(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "UBR");
                Read(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "Identifier", "VendorIdentifier");
                const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
                using var display = registry.OpenSubKey(displayClass);
                if (display == null) throw new IOException("显卡环境信息无法读取");
                foreach (var name in display.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)).Order())
                    Read(displayClass + "\\" + name, "DriverDesc", "DriverVersion", "DriverDate", "ProviderName", "MatchingDeviceId", "InfPath");
                using var adapters = registry.OpenSubKey(@"HARDWARE\DEVICEMAP\VIDEO");
                if (adapters != null) foreach (var name in adapters.GetValueNames().Order()) parts.Add(name + "=" + adapters.GetValue(name));
            }
            return string.Join("\n", parts);
        }, ct);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environment)));
    }
}
