using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace VideoArchive.Core;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
    public void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class EncodeSettings : Observable
{
    string backend = "CPU", codec = "HEVC", rateMode = "VBR", bitrateMode = "智能推荐", profile = "自动", depth = "随源", encoderPreset = "自动", audio = "原样保留", collision = "跳过";
    double quality = 1, fixedMbps = 20, maxMultiplier = 1.5, tolerance = 10, minSavings = 15, cq = 22, fpsLimit = 60;
    bool limit1080, limitFps, unified, aq = true, lookAhead;
    int audioKbps = 320, parallel = 1;
    public string Backend { get => backend; set => Set(ref backend, value); }
    public string Codec { get => codec; set => Set(ref codec, value); }
    public string RateMode { get => rateMode; set => Set(ref rateMode, value); }
    public string BitrateMode { get => bitrateMode; set => Set(ref bitrateMode, value); }
    public string Profile { get => profile; set => Set(ref profile, value); }
    public string Depth { get => depth; set => Set(ref depth, value); }
    public string EncoderPreset { get => encoderPreset; set => Set(ref encoderPreset, value); }
    public string Audio { get => audio; set => Set(ref audio, value); }
    public string Collision { get => collision; set => Set(ref collision, value); }
    public double Quality { get => quality; set => Set(ref quality, value); }
    public double FixedMbps { get => fixedMbps; set => Set(ref fixedMbps, value); }
    public double MaxMultiplier { get => maxMultiplier; set => Set(ref maxMultiplier, value); }
    public double Tolerance { get => tolerance; set => Set(ref tolerance, value); }
    public double MinSavings { get => minSavings; set => Set(ref minSavings, value); }
    public double Cq { get => cq; set => Set(ref cq, value); }
    public double FpsLimit { get => fpsLimit; set => Set(ref fpsLimit, value); }
    public bool Limit1080 { get => limit1080; set => Set(ref limit1080, value); }
    public bool LimitFps { get => limitFps; set => Set(ref limitFps, value); }
    public bool Unified { get => unified; set => Set(ref unified, value); }
    public bool Aq { get => aq; set => Set(ref aq, value); }
    public bool LookAhead { get => lookAhead; set => Set(ref lookAhead, value); }
    public int AudioKbps { get => audioKbps; set => Set(ref audioKbps, value); }
    public int Parallel { get => parallel; set => Set(ref parallel, value); }
    public EncodeSettings Snapshot() => JsonStore.Clone(this);
    public void Validate()
    {
        if (Backend is not ("CPU" or "NVIDIA" or "Intel" or "AMD") || Codec is not ("H.264" or "HEVC" or "AV1") || RateMode is not ("VBR" or "CBR" or "恒定质量") || BitrateMode is not ("智能推荐" or "固定上限") || Depth is not ("随源" or "8-bit" or "10-bit") || Audio is not ("原样保留" or "AAC转码") || Collision is not ("跳过" or "覆盖" or "自动重命名")) throw new InvalidOperationException("设置中包含未知选项，请重新选择参数。");
        if (!double.IsFinite(FixedMbps) || FixedMbps <= 0 || !double.IsFinite(Quality) || Quality <= 0 ||
            !double.IsFinite(MaxMultiplier) || MaxMultiplier < 1 || !double.IsFinite(FpsLimit) || FpsLimit <= 0 ||
            !double.IsFinite(Tolerance) || Tolerance < 0 || Tolerance > 100 || !double.IsFinite(MinSavings) || MinSavings < 0 || MinSavings >= 100 ||
            !double.IsFinite(Cq) || Cq < 0 || Cq > (Codec == "AV1" && Backend == "CPU" ? 63 : 51) || Parallel < 1 || Parallel > 8 || AudioKbps < 32 || AudioKbps > 1024)
            throw new InvalidOperationException("参数无效：请检查码率、质量值、帧率、容差、收益、音频码率和并行数（1–8）。");
    }
    [JsonIgnore] public string Summary => $"{Codec} · {Backend} · {Depth} · {RateMode} · {(BitrateMode == "智能推荐" ? $"智能 ×{Quality:0.##}" : $"≤ {FixedMbps:0.##} Mbps")} · {(Limit1080 ? "最高1080P" : "原尺寸")} · {(LimitFps ? $"最高{FpsLimit:0.###} fps" : "原帧率")} · 音频{Audio}";
}

public sealed class MediaStream
{
    public int Index { get; set; }
    public string Type { get; set; } = "";
    public string Codec { get; set; } = "";
    public string CodecTag { get; set; } = "";
    public string Profile { get; set; } = "";
    public string FrameRate { get; set; } = "";
    public double Mbps { get; set; }
    public byte[] ExtraData { get; set; } = [];
    public byte[] TimecodePacket { get; set; } = [];
    public long SampleCount { get; set; }
    public Dictionary<string, string> Tags { get; set; } = [];
    public bool AttachedPicture { get; set; }
}

public sealed class MediaInfo
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string Container { get; set; } = "";
    public string Codec { get; set; } = "";
    public int VideoIndex { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int BitDepth { get; set; } = 8;
    public string PixelFormat { get; set; } = "";
    public string SampleAspectRatio { get; set; } = "1:1";
    public double Fps { get; set; }
    public double NominalFps { get; set; }
    public double TimeBase { get; set; }
    public double MaxFps { get; set; }
    public bool CadenceChecked { get; set; }
    public bool Vfr { get; set; }
    public double VideoMbps { get; set; }
    public double Duration { get; set; }
    public int Rotation { get; set; }
    public string ColorPrimaries { get; set; } = "";
    public string ColorTransfer { get; set; } = "";
    public string ColorSpace { get; set; } = "";
    public string ColorRange { get; set; } = "";
    public bool Interlaced { get; set; }
    public bool DynamicHdr { get; set; }
    public List<string> HdrMetadata { get; set; } = [];
    public List<MediaStream> Streams { get; set; } = [];
    public Dictionary<string, string> Tags { get; set; } = [];
    public int Chapters { get; set; }
    [JsonIgnore] public bool Hdr => ColorTransfer is "smpte2084" or "arib-std-b67";
    [JsonIgnore] public string Spec => $"{Width}×{Height} · {Fps:0.###}fps{(Vfr ? " VFR" : "")} · {Codec.ToUpperInvariant()} · {(VideoMbps > 0 ? $"{VideoMbps:0.##}M" : "码率未知")}{(Hdr ? " · HDR" : "")}";
}

public sealed class PlanItem : Observable
{
    public string Source { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Destination { get; set; } = "";
    public long SourceSize { get; set; }
    public DateTime SourceModifiedUtc { get; set; }
    public MediaInfo? Media { get; set; }
    public bool VideoCandidate { get; set; }
    public string Action { get; set; } = "复制";
    public string Reason { get; set; } = "";
    public string ReviewIssue { get; set; } = "";
    public string TimecodeChoice { get; set; } = "";
    bool reviewSelected;
    [JsonIgnore] public bool ReviewSelected { get => reviewSelected; set => Set(ref reviewSelected, value); }
    [JsonIgnore] public bool CanReview => ReviewIssue.Length > 0 && Status != "完成" && Action != "跳过";
    [JsonIgnore] public bool DropTimecode => TimecodeChoice == "舍弃时间码";
    [JsonIgnore] public bool RequiresReview => Action == "待决定";
    [JsonIgnore] public string ReviewLabel => TimecodeChoice.Length == 0 ? "请选择" : TimecodeChoice;
    public int TargetWidth { get; set; }
    public int TargetHeight { get; set; }
    public int TargetDepth { get; set; }
    public string TargetProfile { get; set; } = "";
    public string TargetCodec { get; set; } = "";
    public bool ConvertToTenBit420 { get; set; }
    public double TargetFps { get; set; }
    public bool DropFrames { get; set; }
    public double TargetMbps { get; set; }
    public double PeakMbps { get; set; }
    public bool Estimated { get; set; }
    public long EstimatedSize { get; set; }
    public string OutputSha256 { get; set; } = "";
    public long OutputSize { get; set; }
    public string Log { get; set; } = "";
    string status = "待处理", detail = ""; double progress;
    public string Status { get => status; set { Set(ref status, value); Raise(nameof(DisplayStatus)); } }
    public string Detail { get => detail; set { Set(ref detail, value); Raise(nameof(DisplayStatus)); } }
    public double Progress { get => progress; set => Set(ref progress, value); }
    [JsonIgnore] public string SourceSpec => Media?.Spec ?? (VideoCandidate ? "无法探测的视频" : "配套文件");
    [JsonIgnore] public string TargetSpec => Action is "压缩" or "待决定" ? $"{TargetWidth}×{TargetHeight} · {(DropFrames ? $"≤{TargetFps:0.###}" : $"{TargetFps:0.###}")}fps · {TargetMbps:0.##}M · {TargetCodec} {TargetProfile}{(ConvertToTenBit420 ? " · 10-bit 4:2:0" : "")}{(Estimated ? "（估算）" : "")}{(RequiresReview ? "（待确认）" : "")}" : Action == "复制" ? "原样保留" : "—";
    [JsonIgnore] public string EstimateText => Format.Bytes(EstimatedSize);
    [JsonIgnore] public string DisplayStatus => string.IsNullOrEmpty(Detail) ? Status : $"{Status} · {Detail}";
}

public sealed class ArchiveJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Started { get; set; } = DateTime.Now;
    public DateTime? Finished { get; set; }
    public string SourceRoot { get; set; } = "";
    public string OutputRoot { get; set; } = "";
    public string State { get; set; } = "已扫描";
    public int PlanVersion { get; set; }
    public EncodeSettings Settings { get; set; } = new();
    public List<PlanItem> Items { get; set; } = [];
    public List<string> Directories { get; set; } = [];
    public List<string> Notices { get; set; } = [];
    [JsonIgnore] public long SourceBytes => Items.Where(i => i.Action != "未处理").Sum(i => i.SourceSize);
    [JsonIgnore] public long CompletedSourceBytes => Items.Where(i => i.Status == "完成").Sum(i => i.SourceSize);
    [JsonIgnore] public long OutputBytes => Items.Where(i => i.Status == "完成").Sum(i => i.OutputSize);
    [JsonIgnore] public long SavedBytes => CompletedSourceBytes - OutputBytes;
    [JsonIgnore] public string Title => $"{Started:yyyy-MM-dd HH:mm} · {System.IO.Path.GetFileName(SourceRoot)} · {State}";
}

public sealed class Preset { public string Id { get; set; } = Guid.NewGuid().ToString("N"); public string Name { get; set; } = ""; public EncodeSettings Settings { get; set; } = new(); public override string ToString() => Name; }
public sealed class AppConfiguration
{
    public AppearanceSettings Appearance { get; set; } = new();
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public string DefaultPresetId { get; set; } = "";
    public List<Preset> Presets { get; set; } = [];
}
public sealed class AppearanceSettings
{
    public string WallpaperFile { get; set; } = "";
    public double WallpaperOpacity { get; set; } = .35;
}
public static class Format
{
    public static string Bytes(long n) { string[] units = ["B", "KB", "MB", "GB", "TB"]; double v = n; int u = 0; while (Math.Abs(v) >= 1000 && u < 4) { v /= 1000; u++; } return $"{v:0.##} {units[u]}"; }
    public static string Time(double seconds) => seconds < 0 || !double.IsFinite(seconds) ? "—" : TimeSpan.FromSeconds(seconds).ToString(@"d\.hh\:mm\:ss");
}
