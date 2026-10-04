using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VideoArchive.Core;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
    public static T? Read<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default;
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".new-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(temp, path, true); break; }
            catch (Exception ex) when (attempt < 4 && (ex is IOException or UnauthorizedAccessException) && (ex.HResult & 0xffff) is 5 or 32 or 33)
            { Thread.Sleep(50 * (attempt + 1)); }
        }
    }
    public static string DataDirectory => Environment.GetEnvironmentVariable("VIDEOARCHIVE_TEST_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoArchive");
}

public static class Files
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong totalFree);
    public static long FreeSpace(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetDiskFreeSpaceEx(Path.GetDirectoryName(path)!, out var free, out _, out _)) throw new IOException("无法查询输出磁盘空间。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            return (long)Math.Min(free, (ulong)long.MaxValue);
        }
        return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
    }
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Contains(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static string BackupFolderName(string source)
    {
        var name = Path.GetFileName(Normalize(source));
        return (string.IsNullOrEmpty(name) ? "视频备份" : name) + "_已压缩";
    }
    public static string BackupOutput(string source, string? otherFolder = null)
    {
        var parent = otherFolder ?? Directory.GetParent(Normalize(source))?.FullName ?? throw new InvalidOperationException("盘根目录没有相邻文件夹，请选择其他输出位置。");
        return Path.Combine(Normalize(parent), BackupFolderName(source));
    }
    public static void ValidateRoots(string source, string output)
    {
        var s = Normalize(source); var o = Normalize(output);
        if (!Directory.Exists(s)) throw new InvalidOperationException("源文件夹不存在。");
        if (Contains(o, s) || Contains(s, o))
            throw new InvalidOperationException("源目录与输出目录不能相同或互相嵌套。");
        foreach (var root in new[] { s, o })
        {
            for (var d = new DirectoryInfo(root); d != null; d = d.Parent)
                if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("源目录和输出目录及其上级不能经过目录联接或符号链接。");
        }
    }
    public static void CheckDestination(string root, string destination)
    {
        var absolute = Normalize(destination);
        if (!Contains(Normalize(root), absolute)) throw new IOException("输出路径超出备份根目录。");
        for (var d = new DirectoryInfo(Path.GetDirectoryName(absolute)!); d != null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("输出路径经过目录联接或符号链接。");
        if (File.Exists(absolute) && (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) throw new IOException("输出文件是符号链接。");
    }
    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
    }
    public static void RestoreTimes(string path, DateTime created, DateTime modified)
    { File.SetCreationTimeUtc(path, created); File.SetLastWriteTimeUtc(path, modified); }
    public static string FindTool(string name)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe");
        if (File.Exists(bundled)) return bundled;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        { var p = Path.Combine(dir.Trim('"'), name + ".exe"); if (File.Exists(p)) return p; }
        return "";
    }
}

public sealed record ProcessResult(int ExitCode, string Output, string Error);
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct, Action<string>? line = null)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("请在设置中配置有效的 FFmpeg / FFprobe 路径。", executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = new StringBuilder();
        var outputTask = Task.Run(async () => { while (await process.StandardOutput.ReadLineAsync(ct) is { } s) { if (line != null) line(s); else stdout.AppendLine(s); } }, ct);
        var errorTask = Task.Run(async () => { var b = new StringBuilder(); while (await process.StandardError.ReadLineAsync(ct) is { } s) { b.AppendLine(s); if (b.Length > 64000) b.Remove(0, b.Length - 32000); } return b.ToString(); }, ct);
        await process.WaitForExitAsync(ct);
        await outputTask;
        return new(process.ExitCode, stdout.ToString(), await errorTask);
    }
    public static double Number(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : 0;
    public static double Rational(string? value)
    { var parts = (value ?? "").Split('/'); return parts.Length == 2 ? Number(parts[1]) is > 0 and var d ? Number(parts[0]) / d : 0 : Number(value); }
    public static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed class Capability
{
    public string Backend { get; set; } = "";
    public string Codec { get; set; } = "";
    public string Encoder { get; set; } = "";
    public bool Available { get; set; }
    public bool TenBit { get; set; }
    public bool Cbr { get; set; }
    public string Error { get; set; } = "";
    public string Help { get; set; } = "";
}
public sealed class CapabilityService(string ffmpeg)
{
    public static string Encoder(string backend, string codec) => (backend, codec) switch
    {
        ("CPU", "H.264") => "libx264", ("CPU", "HEVC") => "libx265", ("CPU", "AV1") => "libsvtav1",
        ("NVIDIA", "H.264") => "h264_nvenc", ("NVIDIA", "HEVC") => "hevc_nvenc", ("NVIDIA", "AV1") => "av1_nvenc",
        ("Intel", "H.264") => "h264_qsv", ("Intel", "HEVC") => "hevc_qsv", ("Intel", "AV1") => "av1_qsv",
        ("AMD", "H.264") => "h264_amf", ("AMD", "HEVC") => "hevc_amf", ("AMD", "AV1") => "av1_amf",
        _ => throw new InvalidOperationException("未知编码组合。")
    };
    public async Task<List<Capability>> DetectAsync(CancellationToken ct)
    {
        var all = new List<Capability>();
        var encoders = await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-encoders"], ct);
        foreach (var backend in new[] { "CPU", "NVIDIA", "Intel", "AMD" })
            foreach (var codec in new[] { "H.264", "HEVC", "AV1" })
            {
                var c = new Capability { Backend = backend, Codec = codec, Encoder = Encoder(backend, codec), Cbr = !(backend == "CPU" && codec == "AV1") };
                all.Add(c);
                if (!encoders.Output.Contains(c.Encoder + " ", StringComparison.Ordinal)) { c.Error = "FFmpeg 未包含此编码器"; continue; }
                var help = await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-h", "encoder=" + c.Encoder], ct);
                c.Help = help.Output + help.Error;
                c.TenBit = c.Help.Contains("p010") || c.Help.Contains("yuv420p10");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    var probe = await ProcessRunner.RunAsync(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=s=1280x720:r=30:d=0.1", "-frames:v", "1", "-an", "-c:v", c.Encoder, "-f", "null", "-"], timeout.Token);
                    c.Available = probe.ExitCode == 0; c.Error = c.Available ? "" : probe.Error.Trim();
                    if (c.Available && c.TenBit)
                    {
                        var depthProbe = await ProcessRunner.RunAsync(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=s=1280x720:r=30:d=0.1", "-frames:v", "1", "-an", "-pix_fmt", backend == "CPU" ? "yuv420p10le" : "p010le", "-c:v", c.Encoder, "-f", "null", "-"], timeout.Token);
                        c.TenBit = depthProbe.ExitCode == 0;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { c.TenBit = false; c.Error = "检测超时"; }
            }
        return all;
    }
}
