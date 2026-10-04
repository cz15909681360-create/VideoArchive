using System.Text.Json;

namespace VideoArchive.Core;

public sealed class ProbeService(string ffprobe)
{
    public string CacheIdentity => Files.Normalize(ffprobe) + "|" + new FileInfo(ffprobe).LastWriteTimeUtc.Ticks;
    public int ProcessCount => Volatile.Read(ref processCount);
    int processCount;
    Task<ProcessResult> Run(IEnumerable<string> args, CancellationToken ct, Action<string>? line = null)
    { Interlocked.Increment(ref processCount); return ProcessRunner.RunAsync(ffprobe, args, ct, line); }
    static byte[] ParseExtraData(string dump)
    {
        var bytes = new List<byte>();
        foreach (var line in dump.Split('\n'))
        {
            var colon = line.IndexOf(':'); if (colon < 0) continue;
            var rest = line[(colon + 1)..].TrimStart(); var end = rest.IndexOf("  ", StringComparison.Ordinal);
            var hex = (end < 0 ? rest : rest[..end]).Replace(" ", "").Trim();
            if (hex.Length % 2 != 0 || hex.Any(c => !Uri.IsHexDigit(c))) continue;
            bytes.AddRange(Convert.FromHexString(hex));
        }
        return bytes.ToArray();
    }
    public static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.ToString() : "";
    static double Num(JsonElement e, string name) => ProcessRunner.Number(Text(e, name));
    static Dictionary<string, string> Tags(JsonElement e) => e.TryGetProperty("tags", out var t) ? t.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ToString()) : [];
    static void ReadSideData(JsonElement e, MediaInfo info)
    {
        if (!e.TryGetProperty("side_data_list", out var sides)) return;
        foreach (var side in sides.EnumerateArray())
        {
            var type = Text(side, "side_data_type");
            if (type.Contains("DOVI", StringComparison.OrdinalIgnoreCase) || type.Contains("Dolby", StringComparison.OrdinalIgnoreCase) || type.Contains("2094") || type.Contains("Dynamic HDR", StringComparison.OrdinalIgnoreCase)) info.DynamicHdr = true;
            if (type is "Mastering display metadata" or "Content light level metadata")
            {
                var canonical = JsonSerializer.Serialize(side.EnumerateObject().OrderBy(p => p.Name).ToDictionary(p => p.Name, p => p.Value.ToString()));
                if (!info.HdrMetadata.Contains(canonical)) info.HdrMetadata.Add(canonical);
            }
            if (type == "Display Matrix") info.Rotation = (int)Num(side, "rotation");
        }
    }
    public async Task<MediaInfo> ProbeAsync(string path, bool checkCadence, CancellationToken ct)
    {
        var f = new FileInfo(path);
        var result = await Run(["-v", "error", "-show_format", "-show_streams", "-show_chapters", "-show_data", "-of", "json", path], ct);
        if (result.ExitCode != 0) throw new IOException("媒体探测失败：" + result.Error.Trim());
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;
        var info = new MediaInfo { Path = path, Size = f.Length, CreatedUtc = f.CreationTimeUtc, ModifiedUtc = f.LastWriteTimeUtc };
        if (root.TryGetProperty("format", out var format))
        { info.Container = Text(format, "format_name"); info.Duration = Num(format, "duration"); info.Tags = Tags(format); }
        if (root.TryGetProperty("chapters", out var chapters)) info.Chapters = chapters.GetArrayLength();
        bool videoFound = false;
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            var stream = new MediaStream { Index = (int)Num(s, "index"), Type = Text(s, "codec_type"), Codec = Text(s, "codec_name"), CodecTag = Text(s, "codec_tag_string"), Profile = Text(s, "profile"), FrameRate = Text(s, "avg_frame_rate"), Mbps = Num(s, "bit_rate") / 1_000_000, Tags = Tags(s), AttachedPicture = s.TryGetProperty("disposition", out var disp) && Num(disp, "attached_pic") == 1 };
            if (MediaPolicy.IsTimecode(stream)) { stream.ExtraData = ParseExtraData(Text(s, "extradata")); stream.SampleCount = (long)Num(s, "nb_frames"); }
            info.Streams.Add(stream);
            if (stream.Type != "video" || stream.AttachedPicture || videoFound) continue;
            videoFound = true; info.VideoIndex = stream.Index; info.Codec = stream.Codec;
            info.Width = (int)Num(s, "width"); info.Height = (int)Num(s, "height");
            info.PixelFormat = Text(s, "pix_fmt"); info.SampleAspectRatio = Text(s, "sample_aspect_ratio");
            var rawDepth = (int)Num(s, "bits_per_raw_sample");
            info.BitDepth = rawDepth > 0 ? rawDepth : info.PixelFormat.Contains("12") ? 12 : info.PixelFormat.Contains("10") || info.PixelFormat.Contains("p010") ? 10 : 8;
            info.Fps = ProcessRunner.Rational(Text(s, "avg_frame_rate"));
            info.NominalFps = ProcessRunner.Rational(Text(s, "r_frame_rate"));
            info.TimeBase = ProcessRunner.Rational(Text(s, "time_base"));
            if (info.Fps <= 0) info.Fps = info.NominalFps;
            info.MaxFps = Math.Max(info.Fps, info.NominalFps);
            info.Vfr = Math.Abs(info.NominalFps - info.Fps) > .02;
            info.VideoMbps = Num(s, "bit_rate") / 1_000_000;
            if (info.Duration <= 0) info.Duration = Num(s, "duration");
            info.ColorPrimaries = Text(s, "color_primaries"); info.ColorTransfer = Text(s, "color_transfer"); info.ColorSpace = Text(s, "color_space"); info.ColorRange = Text(s, "color_range");
            info.Interlaced = Text(s, "field_order") is "tt" or "bb" or "tb" or "bt";
            if (stream.Tags.TryGetValue("rotate", out var rotate)) info.Rotation = (int)ProcessRunner.Number(rotate);
            ReadSideData(s, info);
        }
        if (!videoFound || info.Width <= 0 || info.Height <= 0) throw new IOException("未找到有效视频画面。");
        if (MediaPolicy.RegenerateTimecode(info))
        {
            foreach (var timecode in info.Streams.Where(MediaPolicy.IsTimecode))
            {
                var packet = await Run(["-v", "error", "-select_streams", timecode.Index.ToString(), "-read_intervals", "%+#1", "-show_packets", "-show_data", "-show_entries", "packet=data", "-of", "json", path], ct);
                if (packet.ExitCode == 0)
                {
                    using var data = JsonDocument.Parse(packet.Output);
                    if (data.RootElement.TryGetProperty("packets", out var ps) && ps.GetArrayLength() == 1) timecode.TimecodePacket = ParseExtraData(Text(ps[0], "data"));
                }
            }
        }
        // Ordinary 8-bit SDR needs only container headers. HDR/10-bit sources
        // still inspect decoded frame side data to protect mastering/dynamic HDR.
        if (info.Hdr || info.BitDepth > 8 || info.ColorPrimaries == "bt2020")
        {
            var frames = await Run(["-v", "error", "-select_streams", info.VideoIndex.ToString(), "-read_intervals", "%+#12", "-show_frames", "-show_entries", "frame=side_data_list", "-of", "json", path], ct);
            if (frames.ExitCode == 0)
            {
                using var frameDoc = JsonDocument.Parse(frames.Output);
                if (frameDoc.RootElement.TryGetProperty("frames", out var fs)) foreach (var frame in fs.EnumerateArray()) ReadSideData(frame, info);
            }
        }
        if (info.VideoMbps <= 0 || checkCadence)
        {
            long bytes = 0, timestampCount = 0; double minimumDuration = double.MaxValue, maximumDuration = 0, first = double.MaxValue, last = double.MinValue, previous = double.NaN, minimumInterval = double.MaxValue, maximumInterval = 0;
            var presentationTimes = new PriorityQueue<double, double>();
            void ReadPresentationTime(double t)
            {
                if (!double.IsNaN(previous) && t > previous + .0000001) { minimumInterval = Math.Min(minimumInterval, t - previous); maximumInterval = Math.Max(maximumInterval, t - previous); }
                previous = t;
            }
            var packets = await Run(["-v", "error", "-select_streams", info.VideoIndex.ToString(), "-show_packets", "-show_entries", "packet=pts_time,duration_time,size", "-of", "compact=p=0:nk=0", path], ct, line =>
            {
                var fields = line.Split('|').Select(s => s.Split('=', 2)).Where(s => s.Length == 2).ToDictionary(s => s[0], s => s[1]);
                if (fields.TryGetValue("size", out var size)) bytes += (long)ProcessRunner.Number(size);
                if (fields.TryGetValue("pts_time", out var pts) && pts != "N/A")
                {
                    var t = ProcessRunner.Number(pts); first = Math.Min(first, t); last = Math.Max(last, t);
                    timestampCount++;
                    if (checkCadence) { presentationTimes.Enqueue(t, t); if (presentationTimes.Count > 64) ReadPresentationTime(presentationTimes.Dequeue()); }
                }
                if (fields.TryGetValue("duration_time", out var dur))
                { var d = ProcessRunner.Number(dur); if (d > 0) { minimumDuration = Math.Min(minimumDuration, d); maximumDuration = Math.Max(maximumDuration, d); } }
            });
            if (packets.ExitCode == 0)
            {
                while (presentationTimes.Count > 0) ReadPresentationTime(presentationTimes.Dequeue());
                var duration = info.Duration > 0 ? info.Duration : last - first;
                if (info.VideoMbps <= 0 && duration > 0) info.VideoMbps = bytes * 8.0 / duration / 1_000_000;
                // PTS gaps represent the delivered picture cadence. Encoders may retain a 120fps
                // packet-duration hint after dropping alternate pictures, so duration alone is wrong.
                if (checkCadence && minimumInterval < double.MaxValue)
                {
                    info.MaxFps = 1 / (minimumInterval + Math.Max(info.TimeBase, .000001));
                    if (timestampCount > 1 && last > first) info.Fps = (timestampCount - 1) / (last - first);
                    info.CadenceChecked = true;
                    info.Vfr = maximumInterval - minimumInterval > Math.Max(.0001, info.TimeBase * 1.1);
                }
                else if (checkCadence && minimumDuration < double.MaxValue)
                { info.MaxFps = 1 / minimumDuration; info.CadenceChecked = true; info.Vfr = maximumDuration - minimumDuration > .0001; }
            }
        }
        return info;
    }
}
