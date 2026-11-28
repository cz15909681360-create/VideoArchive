using System.Buffers.Binary;

namespace VideoArchive.Core;

internal static class MxfPolicy
{
    public static bool IsInput(MediaInfo media) =>
        Path.GetExtension(media.Path).Equals(".mxf", StringComparison.OrdinalIgnoreCase);

    public static string? Protection(MediaInfo media)
    {
        string label = (media.Tags.GetValueOrDefault("operational_pattern_ul") ?? "").Replace(".", "");
        // OP1a: one package, one item. Ignore the UL registry version and qualifier bytes.
        if (media.Container != "mxf" || label.Length != 32 || !label.All(Uri.IsHexDigit) ||
            !label[..14].Equals("060e2b34040101", StringComparison.OrdinalIgnoreCase) ||
            !label[16..28].Equals("0d0102010101", StringComparison.OrdinalIgnoreCase))
            return "未确认是单文件 MXF OP1a，采用保守备份";
        if (media.Streams.Any(s => s.AttachedPicture || s.Type is not ("video" or "audio" or "data" or "unknown")))
            return "MXF 包含尚未验证的附加轨道";
        if (media.Streams.Any(s => s.Type == "audio" &&
            (s.Channels <= 0 || s.SampleRate <= 0 || s.Codec is not ("pcm_s16le" or "pcm_s16be" or "pcm_s24le" or "pcm_s24be" or "aac"))))
            return "MXF 音轨无法确认可原样写入 MOV";
        return null;
    }

    static IEnumerable<string> Timecodes(MediaInfo media) => media.Tags
        .Concat(media.Streams.SelectMany(stream => stream.Tags))
        .Where(tag => tag.Key.Equals("timecode", StringComparison.OrdinalIgnoreCase))
        .Select(tag => tag.Value);

    static double VideoRate(MediaInfo media) =>
        ProcessRunner.Rational(media.Streams.FirstOrDefault(stream => stream.Index == media.VideoIndex)?.FrameRate);

    public static (string? Timecode, string? Issue) PlanTimecode(MediaInfo media, bool dropFrames)
    {
        var values = Timecodes(media).Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length == 0 && !media.Streams.Any(MediaPolicy.IsTimecode)) return (null, null);
        if (values.Length != 1 || media.Streams.Any(MediaPolicy.IsTimecode))
            return (null, "MXF 时间码来源不唯一或结构尚未验证");
        if (dropFrames || media.Vfr) return (null, "变帧率或限帧时无法可靠保留 MXF 时间码计数");
        string tc = values[0];
        double rate = VideoRate(media);
        if (rate is < 1 or > 60 || Math.Abs(rate - media.Fps) > .001 || !ValidTimecode(tc, rate))
            return (null, "无法确认 MXF 时间码起点、帧率和计数规则");
        return (tc, null);
    }

    static bool ValidTimecode(string value, double rate)
    {
        if (value.Length != 11 || value[2] != ':' || value[5] != ':' || value[8] is not (':' or ';'))
            return false;
        int ReadPair(int index) => char.IsAsciiDigit(value[index]) && char.IsAsciiDigit(value[index + 1])
            ? (value[index] - '0') * 10 + value[index + 1] - '0' : -1;
        int hour = ReadPair(0), minute = ReadPair(3), second = ReadPair(6), frame = ReadPair(9);
        int nominal = (int)Math.Round(rate);
        if (hour is < 0 or >= 24 || minute is < 0 or >= 60 || second is < 0 or >= 60 || frame < 0 || frame >= nominal)
            return false;
        if (value[8] == ':') return true;
        if (Math.Abs(rate - 30000d / 1001) > .00001 && Math.Abs(rate - 60000d / 1001) > .00001)
            return false;
        return minute % 10 == 0 || second != 0 || frame >= nominal / 15;
    }

    public static bool VerifyTimecode(PlanItem item, MediaInfo actual)
    {
        var tracks = actual.Streams.Where(MediaPolicy.IsTimecode).ToArray();
        if (item.MxfTimecode == null) return tracks.Length == 0 && !Timecodes(actual).Any();
        if (tracks.Length != 1 || tracks[0].ExtraData.Length < 20 ||
            tracks[0].Tags.GetValueOrDefault("timecode") != item.MxfTimecode) return false;
        var description = tracks[0].ExtraData.AsSpan();
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(description[4..]);
        uint scale = BinaryPrimitives.ReadUInt32BigEndian(description[8..]);
        uint duration = BinaryPrimitives.ReadUInt32BigEndian(description[12..]);
        double sourceRate = VideoRate(item.Media!);
        return duration > 0 && Math.Abs((double)scale / duration - sourceRate) < .00001 &&
            description[16] == (int)Math.Round(sourceRate) &&
            ((flags & 1) != 0) == (item.MxfTimecode[8] == ';');
    }
}
