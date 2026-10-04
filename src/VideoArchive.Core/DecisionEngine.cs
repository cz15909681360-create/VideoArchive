namespace VideoArchive.Core;

public static class Recommendation
{
    // HEVC defaults and AVC/AV1 range midpoints from the agreed requirements.
    static readonly double[] Areas = [1280d * 720, 1920d * 1080, 2560d * 1440, 3840d * 2160, 5312d * 2988, 6144d * 3456, 7680d * 4320];
    static readonly double[][] Hevc = [[4, 6, 9.4], [10, 15, 22], [15, 25, 38], [32, 48, 75], [45, 65, 100], [50, 75, 115], [80, 130, 204]];
    static readonly double[][] Avc = [[6.5, 9.5, 14.9], [16, 24, 36.5], [25, 37.5, 57.5], [50, 75, 115], [70, 100, 157.5], [82.5, 120, 182.5], [130, 195, 306]];
    static readonly double[][] Av1 = [[3.5, 5.5, 8.6], [8, 12.5, 19.5], [13, 21.5, 31.5], [27, 41, 64], [37.5, 54, 84], [43.5, 64, 100], [68.5, 110, 173]];
    static double Rate(double[] row, double fps) => fps <= 30 ? row[0] : fps <= 60 ? row[0] + (row[1] - row[0]) * (fps - 30) / 30 : fps <= 120 ? row[1] + (row[2] - row[1]) * (fps - 60) / 60 : row[2] * Math.Pow(fps / 120, .65);
    public static (double Mbps, bool Estimated) Get(int w, int h, double fps, string codec)
    {
        var table = codec == "H.264" ? Avc : codec == "AV1" ? Av1 : Hevc;
        var area = (double)w * h;
        for (int i = 0; i < Areas.Length; i++)
        {
            if (area == Areas[i]) return (Rate(table[i], fps), fps > 60 || i is 4 or 5 || fps > 30 && fps != 60);
            if (area < Areas[i])
            {
                if (i == 0) return (Math.Max(.15, Rate(table[0], fps) * area / Areas[0]), true);
                var a = Rate(table[i - 1], fps); var b = Rate(table[i], fps);
                return (a + (b - a) * (area - Areas[i - 1]) / (Areas[i] - Areas[i - 1]), true);
            }
        }
        return (Rate(table[^1], fps) * area / Areas[^1], true);
    }
}

public sealed class DecisionEngine
{
    static string SourceCodec(string codec) => codec switch { "h264" => "H.264", "hevc" => "HEVC", "av1" => "AV1", _ => codec };
    public static string? TimecodeIssue(MediaInfo m)
    {
        var timecodes = m.Streams.Where(MediaPolicy.IsTimecode).ToList();
        if (timecodes.Count > 0 && Path.GetExtension(m.Path).ToLowerInvariant() is not (".mp4" or ".mov")) return "源容器尚未验证保留时间码";
        if (timecodes.Any(t => t.ExtraData.Length < 20)) return $"时间码描述格式尚未验证（{timecodes.Min(t => t.ExtraData.Length)}字节）";
        if (timecodes.Any(t => !t.Tags.ContainsKey("timecode"))) return "时间码起始值未知";
        if (MediaPolicy.RegenerateTimecode(m) && (timecodes.Count != 1 || timecodes[0].SampleCount != 1 || timecodes[0].TimecodePacket.Length != 4)) return "非常规MP4时间码无法可靠保留";
        return null;
    }
    public static string? Protection(MediaInfo m, EncodeSettings s, bool ignoreTimecode = false)
    {
        if (m.DynamicHdr) return "动态HDR / Dolby Vision 信息无法可靠保留";
        if (!ignoreTimecode && TimecodeIssue(m) is { } issue) return issue;
        if (m.Streams.Count(x => x.Type == "video" && !x.AttachedPicture) != 1) return "包含多个视频画面轨道";
        if (m.Interlaced) return "隔行素材采用保守备份";
        var convert420 = MediaPolicy.NeedsTenBit420(m);
        if ((!convert420 && (m.BitDepth > 10 || m.PixelFormat.Contains("422") || m.PixelFormat.Contains("444"))) || m.PixelFormat.StartsWith("gbr") || m.PixelFormat.Contains("yuva")) return "特殊色度或透明通道需保留";
        if (m.SampleAspectRatio is not ("1:1" or "" or "N/A")) return "非方形像素素材采用保守备份";
        if (m.Hdr && (s.Codec == "H.264" || s.Depth == "8-bit") && !convert420) return "所选参数不能可靠保留HDR";
        if (m.Hdr && m.BitDepth < 10) return "HDR位深信息不一致，采用保守备份";
        if (m.BitDepth > 8 && s.Depth == "8-bit" && !convert420) return "避免降低源素材位深";
        var ext = Path.GetExtension(m.Path).ToLowerInvariant();
        if (ext is not (".mp4" or ".mov" or ".mkv")) return "源容器的转码兼容性未验证";
        if (ext is ".mp4" or ".mov")
        {
            if (m.Streams.Any(x => x.Type == "subtitle" && x.Codec != "mov_text")) return "源容器无法可靠保留字幕轨";
            if (ext == ".mov" && s.Codec == "AV1") return "MOV / AV1 组合兼容性未验证";
        }
        if (m.Duration <= 0 || m.Fps <= 0) return "时长或帧率未知，采用保守备份";
        return null;
    }
    public PlanItem Create(string sourceRoot, string outputRoot, string source, long size, DateTime modified, MediaInfo? m, EncodeSettings s, string timecodeChoice = "")
    {
        var relative = Path.GetRelativePath(sourceRoot, source);
        var p = new PlanItem { Source = source, RelativePath = relative, Destination = Path.Combine(outputRoot, relative), SourceSize = size, SourceModifiedUtc = modified, Media = m, EstimatedSize = size, Reason = m == null ? "配套资料原样备份" : "已满足目标规格" };
        if (m == null) return p;
        p.ReviewIssue = TimecodeIssue(m) ?? "";
        p.TimecodeChoice = p.ReviewIssue.Length > 0 ? timecodeChoice : "";
        p.ConvertToTenBit420 = MediaPolicy.NeedsTenBit420(m);
        p.TargetCodec = p.ConvertToTenBit420 ? "HEVC" : s.Codec;
        s = MediaPolicy.EffectiveSettings(p, s);
        p.TargetWidth = m.Width; p.TargetHeight = m.Height; p.TargetFps = m.Fps; p.TargetDepth = p.ConvertToTenBit420 || s.Depth == "10-bit" ? 10 : m.BitDepth;
        p.TargetProfile = MediaPolicy.ResolveProfile(s, p.TargetDepth);
        bool downsize = false;
        if (s.Limit1080)
        {
            var rotated = Math.Abs(m.Rotation) % 180 == 90;
            var displayW = rotated ? m.Height : m.Width; var displayH = rotated ? m.Width : m.Height;
            double factor = Math.Min(1, Math.Min((displayW >= displayH ? 1920d : 1080d) / displayW, (displayW >= displayH ? 1080d : 1920d) / displayH));
            if (factor < 1) { p.TargetWidth = Math.Max(2, (int)(m.Width * factor) / 2 * 2); p.TargetHeight = Math.Max(2, (int)(m.Height * factor) / 2 * 2); downsize = true; }
        }
        p.DropFrames = s.LimitFps && Math.Max(m.MaxFps, m.Fps) > s.FpsLimit + .02;
        if (p.DropFrames) p.TargetFps = Math.Min(m.Fps, s.FpsLimit);
        var recommendation = Recommendation.Get(p.TargetWidth, p.TargetHeight, p.TargetFps, s.Codec);
        var requested = s.BitrateMode == "智能推荐" ? recommendation.Mbps * s.Quality * (m.Hdr ? 1.25 : 1) : s.FixedMbps;
        p.TargetMbps = m.VideoMbps > 0 ? Math.Min(m.VideoMbps, requested) : requested;
        p.PeakMbps = p.TargetMbps * s.MaxMultiplier;
        p.Estimated = s.BitrateMode == "智能推荐" && recommendation.Estimated || s.RateMode == "恒定质量";
        if (p.ReviewIssue.Length > 0)
        {
            if (p.TimecodeChoice == "原样备份") { p.Reason = "用户选择原样备份：" + p.ReviewIssue; return p; }
            if (!p.DropTimecode) { p.Action = "待决定"; p.Reason = p.ReviewIssue + "；请选择原样备份或舍弃时间码后按方案压缩"; return p; }
        }
        var protection = Protection(m, s, p.DropTimecode);
        if (protection != null) { p.Reason = protection + "，原样保留"; return p; }
        if (s.LimitFps && !m.CadenceChecked) { p.Reason = "无法可靠确认帧率上限，原样保留"; return p; }
        var forced = p.ConvertToTenBit420 || downsize || p.DropFrames || s.Unified && SourceCodec(m.Codec) != s.Codec || s.Audio != "原样保留" && m.Streams.Any(x => x.Type == "audio");
        if (m.VideoMbps <= 0) { p.Reason = "视频码率未知，避免无依据重编码"; return p; }
        var removedBytes = m.Streams.Where(MediaPolicy.IsDiscardedData).Sum(t => t.Mbps) * 1_000_000 / 8 * m.Duration;
        var audioBytes = Math.Max(0, m.Size - (m.VideoMbps * 1_000_000 / 8 * m.Duration) - removedBytes);
        var estimate = (long)Math.Min(m.Size, p.TargetMbps * 1_000_000 / 8 * m.Duration + audioBytes);
        var savings = m.Size > 0 ? 100d * (m.Size - estimate) / m.Size : 0;
        bool highRate = m.VideoMbps > requested * (1 + s.Tolerance / 100);
        if (!forced && (!highRate || savings < s.MinSavings))
        { p.Reason = highRate ? $"预计收益{ savings:0.#}%低于{ s.MinSavings:0.#}%" : $"已满足码率上限（容差{s.Tolerance:0.#}%）"; return p; }
        p.Action = "压缩"; p.EstimatedSize = estimate;
        var reasons = new List<string>();
        if (p.ConvertToTenBit420) reasons.Add($"{m.BitDepth}-bit {m.PixelFormat} 转为10-bit 4:2:0 HEVC Main10");
        if (downsize) reasons.Add("已启用最高1080P");
        if (p.DropFrames) reasons.Add($"帧率超过{s.FpsLimit:0.###}");
        if (highRate) reasons.Add("码率超过目标");
        if (s.Unified && SourceCodec(m.Codec) != s.Codec) reasons.Add("统一编码格式");
        if (s.Audio != "原样保留") reasons.Add("音频转码");
        if (m.Streams.Any(MediaPolicy.IsDiscardedData)) reasons.Add("移除相机/特殊数据轨");
        if (p.DropTimecode) reasons.Add("用户选择舍弃时间码");
        else if (m.Streams.Any(MediaPolicy.IsTimecode)) reasons.Add("保留tmcd及原计时规则");
        p.Reason = string.Join("、", reasons);
        return p;
    }
}
