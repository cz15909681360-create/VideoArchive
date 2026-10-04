using System.Text.Json;

namespace VideoArchive.Core;

public static class CommandBuilder
{
    public static List<string> Build(PlanItem item, EncodeSettings s, string temporaryOutput)
    {
        var m = item.Media ?? throw new InvalidOperationException("缺少视频信息。");
        s = MediaPolicy.EffectiveSettings(item, s);
        if (item.RequiresReview) throw new InvalidOperationException("请先选择时间码异常文件的处理方式。");
        var streams = MediaPolicy.EncodingStreams(m, item.DropTimecode);
        var outputIndex = streams.FindIndex(x => x.Index == m.VideoIndex);
        var spec = ":" + outputIndex;
        var encoder = CapabilityService.Encoder(s.Backend, s.Codec);
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin", "-y", "-noautorotate", "-i", item.Source };
        void Add(string key, string value) { args.Add(key); args.Add(value); }
        foreach (var stream in streams) Add("-map", "0:" + stream.Index);
        Add("-map_metadata", "0"); Add("-map_chapters", "0"); Add("-c", "copy");
        for (int i = 0; i < streams.Count; i++) Add("-map_metadata:s:" + i, "0:s:" + streams[i].Index);
        if (item.DropTimecode)
        {
            Add("-metadata", "timecode=");
            for (int i = 0; i < streams.Count; i++) Add("-metadata:s:" + i, "timecode=");
        }
        Add("-c" + spec, encoder); Add("-fps_mode" + spec, "passthrough");
        var filters = new List<string>();
        if (m.Width != item.TargetWidth || m.Height != item.TargetHeight) filters.Add($"scale={item.TargetWidth}:{item.TargetHeight}:flags=lanczos");
        if (item.DropFrames) filters.Add($"select='isnan(prev_selected_t)+gte(t-prev_selected_t,1/{ProcessRunner.N(s.FpsLimit)}-0.000001)'");
        filters.Add("format=" + (item.TargetDepth == 10 ? s.Backend == "CPU" ? "yuv420p10le" : "p010le" : "yuv420p"));
        Add("-filter" + spec, string.Join(',', filters));
        var preset = s.EncoderPreset == "自动" ? s.Backend == "NVIDIA" ? "p6" : s.Backend == "CPU" && s.Codec == "AV1" ? "6" : s.Backend == "AMD" ? "quality" : "medium" : s.EncoderPreset;
        Add(s.Backend == "AMD" ? "-quality" : "-preset", preset);
        var profile = MediaPolicy.ResolveProfile(s, item.TargetDepth);
        if (s.Codec != "AV1") Add("-profile" + spec, profile);
        if (s.RateMode == "恒定质量")
        {
            if (s.Backend == "CPU") Add("-crf", ProcessRunner.N(s.Cq));
            else if (s.Backend == "NVIDIA") { Add("-rc", "vbr"); Add("-cq", ProcessRunner.N(s.Cq)); Add("-b" + spec, "0"); }
            else if (s.Backend == "Intel") Add("-global_quality", ProcessRunner.N(s.Cq));
            else { Add("-rc", "cqp"); Add("-qp_i", ProcessRunner.N(s.Cq)); Add("-qp_p", ProcessRunner.N(s.Cq)); }
        }
        else
        {
            Add("-b" + spec, ProcessRunner.N(item.TargetMbps) + "M");
            if (s.Backend == "NVIDIA") Add("-rc", s.RateMode == "CBR" ? "cbr" : "vbr");
            if (s.Backend == "AMD") Add("-rc", s.RateMode == "CBR" ? "cbr" : "vbr_peak");
            if (s.RateMode == "CBR") Add("-minrate" + spec, ProcessRunner.N(item.TargetMbps) + "M");
        }
        var peak = s.RateMode == "CBR" ? item.TargetMbps : item.PeakMbps;
        // SVT-AV1 accepts a max bitrate only in CRF mode, not in its ABR/VBR mode.
        if (!(s.Backend == "CPU" && s.Codec == "AV1" && s.RateMode == "VBR"))
        { Add("-maxrate" + spec, ProcessRunner.N(peak) + "M"); Add("-bufsize" + spec, ProcessRunner.N(peak * 2) + "M"); }
        if (s.Backend == "NVIDIA")
        {
            if (s.Aq) Add("-spatial-aq", "1");
            if (s.LookAhead) Add("-rc-lookahead", "20");
        }
        foreach (var (key, value) in new[] { ("color_primaries", m.ColorPrimaries), ("color_trc", m.ColorTransfer), ("colorspace", m.ColorSpace), ("color_range", m.ColorRange) })
            if (value is not ("" or "unknown" or "unspecified")) Add("-" + key + spec, value);
        if (s.Backend == "CPU" && s.Codec == "HEVC")
        {
            var parameters = new List<string> { "log-level=error" };
            if (m.ColorPrimaries is not ("" or "unknown" or "unspecified")) parameters.Add("colorprim=" + m.ColorPrimaries);
            if (m.ColorTransfer is not ("" or "unknown" or "unspecified")) parameters.Add("transfer=" + m.ColorTransfer);
            if (m.ColorSpace is not ("" or "unknown" or "unspecified")) parameters.Add("colormatrix=" + m.ColorSpace);
            if (m.Hdr && m.ColorTransfer == "smpte2084") parameters.Add("hdr10=1");
            foreach (var metadata in m.HdrMetadata)
            {
                using var doc = JsonDocument.Parse(metadata); var d = doc.RootElement;
                string Get(string k) => ProbeService.Text(d, k);
                string Chromatic(string k) => Math.Round(ProcessRunner.Rational(Get(k)) * 50000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (Get("side_data_type") == "Mastering display metadata") parameters.Add($"master-display=G({Chromatic("green_x")},{Chromatic("green_y")})B({Chromatic("blue_x")},{Chromatic("blue_y")})R({Chromatic("red_x")},{Chromatic("red_y")})WP({Chromatic("white_point_x")},{Chromatic("white_point_y")})L({Math.Round(ProcessRunner.Rational(Get("max_luminance")) * 10000)},{Math.Round(ProcessRunner.Rational(Get("min_luminance")) * 10000)})");
                if (Get("side_data_type") == "Content light level metadata") parameters.Add($"max-cll={Get("max_content")},{Get("max_average")}");
            }
            Add("-x265-params", string.Join(':', parameters));
        }
        if (s.Audio != "原样保留") { Add("-c:a", "aac"); Add("-b:a", s.AudioKbps + "k"); }
        if (Path.GetExtension(item.Destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(item.Destination).Equals(".mov", StringComparison.OrdinalIgnoreCase))
        {
            // MP4 cover images live in ilst; use_metadata_tags selects mdta and
            // silently omits those images. Keep ilst for camera thumbnails.
            Add("-movflags", m.Streams.Any(t => t.AttachedPicture) ? "+faststart" : "+faststart+use_metadata_tags");
            Add("-write_tmcd", !item.DropTimecode && m.Streams.Any(MediaPolicy.IsTimecode) ? "1" : "0");
            // MP4 refuses mapped codec-none tmcd streams. Let the muxer allocate
            // a valid track, then restore the source description and counter.
            if (!item.DropTimecode && MediaPolicy.RegenerateTimecode(m)) Add("-timecode", m.Streams.Single(MediaPolicy.IsTimecode).Tags["timecode"]);
            if (s.Codec == "HEVC") Add("-tag" + spec, "hvc1");
        }
        Add("-progress", "pipe:1"); args.Add("-nostats"); args.Add(temporaryOutput);
        return args;
    }
    public static string Preview(PlanItem p, EncodeSettings s, string executable) => string.Join(' ', new[] { executable }.Concat(Build(p, s, p.Destination)).Select(v => "\"" + v.Replace("\"", "\\\"") + "\""));
}
