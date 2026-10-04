using System.Buffers.Binary;
using System.Text;

namespace VideoArchive.Core;

public static class MediaPolicy
{
    public const int PlanVersion = 4;
    public static bool IsTimecode(MediaStream stream) => stream.CodecTag.Equals("tmcd", StringComparison.OrdinalIgnoreCase);
    public static bool IsDiscardedData(MediaStream stream) => stream.Type is "data" or "unknown" && !IsTimecode(stream);
    public static List<MediaStream> MappedStreams(MediaInfo media, bool dropTimecode = false) => media.Streams.Where(s => (s.Type is not ("data" or "unknown") || IsTimecode(s)) && (!dropTimecode || !IsTimecode(s))).ToList();
    public static bool RegenerateTimecode(MediaInfo media) => Path.GetExtension(media.Path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) && media.Streams.Any(IsTimecode);
    public static List<MediaStream> EncodingStreams(MediaInfo media, bool dropTimecode = false) => MappedStreams(media, dropTimecode).Where(s => !RegenerateTimecode(media) || !IsTimecode(s)).ToList();
    public static string ResolveProfile(EncodeSettings settings, int depth) => settings.Codec switch
    {
        "HEVC" => depth > 8 ? "main10" : "main",
        "H.264" => depth > 8 ? "high10" : "high",
        _ => "main"
    };
    public static bool NeedsTenBit420(MediaInfo media) => media.BitDepth >= 10 &&
        (media.PixelFormat.Contains("422") || media.PixelFormat.Contains("444") || media.PixelFormat.Contains("440") || media.BitDepth > 10 && media.PixelFormat.StartsWith("yuv"));
    public static EncodeSettings EffectiveSettings(PlanItem item, EncodeSettings settings)
    {
        if (item.TargetCodec.Length == 0 || item.TargetCodec == settings.Codec) return settings;
        var effective = settings.Snapshot(); effective.Codec = item.TargetCodec; effective.Profile = "自动"; effective.EncoderPreset = "自动";
        return effective;
    }

    // FFmpeg 8.1 copies tmcd packets but recreates its sample description without
    // the original flags. Restore its 20-byte description, including drop-frame,
    // clock and frame-number interpretation; this does not alter the video.
    public static void RestoreTimecodeDescriptions(string path, MediaInfo source)
    {
        var originals = source.Streams.Where(IsTimecode).ToArray();
        if (originals.Length == 0) return;
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var header = new byte[16];
        long position = 0;
        while (position + 8 <= file.Length)
        {
            file.Position = position; file.ReadExactly(header.AsSpan(0, 8));
            long size = BinaryPrimitives.ReadUInt32BigEndian(header); int headerSize = 8;
            if (size == 1) { file.ReadExactly(header.AsSpan(8, 8)); size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8))); headerSize = 16; }
            if (size == 0) size = file.Length - position;
            if (size < headerSize || size > file.Length - position) throw new IOException("成品容器结构无效。");
            if (Encoding.ASCII.GetString(header, 4, 4) == "moov")
            {
                if (size > 128 * 1024 * 1024) throw new IOException("时间码容器索引过大，无法安全校验。");
                var moov = new byte[(int)size]; file.Position = position; file.ReadExactly(moov);
                var descriptions = new List<(int Description, long Packet, List<(int Position, int Header, string Type)> Headers)>();
                void Visit(int start, int end, List<int> trackDescriptions, List<long> chunks, List<(int Position, int Header, string Type)> trackHeaders)
                {
                    for (int p = start; p + 8 <= end;)
                    {
                        long length = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p)); int h = 8;
                        if (length == 1 && p + 16 <= end) { length = checked((long)BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(p + 8))); h = 16; }
                        if (length == 0) length = end - p;
                        if (length < h || length > end - p) throw new IOException("时间码索引结构无效。");
                        var type = Encoding.ASCII.GetString(moov, p + 4, 4);
                        if (type == "trak")
                        {
                            var td = new List<int>(); var tc = new List<long>(); var th = new List<(int Position, int Header, string Type)>(); Visit(p + h, p + (int)length, td, tc, th);
                            foreach (var d in td) descriptions.Add((d, tc.Count == 1 ? tc[0] : -1, th));
                        }
                        else if (type is "mdia" or "minf" or "stbl") Visit(p + h, p + (int)length, trackDescriptions, chunks, trackHeaders);
                        else if (type is "tkhd" or "mdhd") trackHeaders.Add((p, h, type));
                        else if (type is "stco" or "co64")
                        {
                            if (length < h + 8) throw new IOException("时间码索引不完整。");
                            var count = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p + h + 4));
                            if (count == 1)
                            {
                                if (length < h + 8 + (type == "stco" ? 4 : 8)) throw new IOException("时间码数据偏移不完整。");
                                chunks.Add(type == "stco" ? BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(p + h + 8)) : checked((long)BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(p + h + 8))));
                            }
                        }
                        else if (type == "stsd")
                        {
                            for (int entry = p + h + 8; entry + 8 <= p + length;)
                            {
                                int entrySize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(entry)));
                                if (entrySize < 8 || entrySize > p + length - entry) throw new IOException("时间码描述无效。");
                                if (Encoding.ASCII.GetString(moov, entry + 4, 4) == "tmcd")
                                { if (entrySize < 36) throw new IOException("时间码描述不完整。"); trackDescriptions.Add(entry + 16); }
                                entry += entrySize;
                            }
                        }
                        p += (int)length;
                    }
                }
                Visit(headerSize, moov.Length, [], [], []);
                if (descriptions.Count != originals.Length) throw new IOException("时间码轨道数量不符。");
                for (int i = 0; i < originals.Length; i++)
                {
                    if (originals[i].ExtraData.Length < 20) throw new IOException("源时间码描述不完整，请重新扫描。");
                    file.Position = position + descriptions[i].Description; file.Write(originals[i].ExtraData.AsSpan(0, 20));
                    if (RegenerateTimecode(source))
                    {
                        var dataOffset = descriptions[i].Packet;
                        if (originals[i].TimecodePacket.Length != 4 || dataOffset < 0 || dataOffset > file.Length - 4) throw new IOException("无法恢复原时间码计数。");
                        file.Position = dataOffset; file.Write(originals[i].TimecodePacket);
                        foreach (var (headerPosition, headerLength, type) in descriptions[i].Headers)
                        {
                            int payload = headerPosition + headerLength; bool version1 = moov[payload] == 1;
                            var tags = originals[i].Tags;
                            if (tags.TryGetValue("creation_time", out var date) && DateTimeOffset.TryParse(date, out var created))
                            {
                                var seconds = checked((ulong)(created - new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds);
                                var value = new byte[version1 ? 8 : 4];
                                if (version1) BinaryPrimitives.WriteUInt64BigEndian(value, seconds); else BinaryPrimitives.WriteUInt32BigEndian(value, checked((uint)seconds));
                                file.Position = position + payload + 4; file.Write(value);
                            }
                            if (type == "mdhd" && tags.TryGetValue("language", out var language) && language.Length == 3 && language.All(c => c is >= 'a' and <= 'z'))
                            {
                                var packed = (ushort)((language[0] - 0x60) << 10 | (language[1] - 0x60) << 5 | language[2] - 0x60);
                                var value = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(value, packed);
                                file.Position = position + payload + (version1 ? 32 : 20); file.Write(value);
                            }
                        }
                    }
                }
                file.Flush(true); return;
            }
            position += size;
        }
        throw new IOException("成品缺少时间码容器索引。");
    }
}
