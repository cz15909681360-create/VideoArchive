using System.Security.Cryptography;
using System.Text;

namespace VideoArchive.Core;

public sealed class ArchiveService(string ffmpeg, string ffprobe, string dataDirectory, Func<string, long>? availableSpace = null)
{
    readonly object persistenceLock = new(), nameLock = new();
    readonly HashSet<string> reservedNames = new(StringComparer.OrdinalIgnoreCase);
    TaskCompletionSource<bool> pauseGate = NewGate(true);
    public bool Paused { get; private set; }
    public event Action<PlanItem?, string>? Changed;
    static TaskCompletionSource<bool> NewGate(bool completed)
    { var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); if (completed) gate.SetResult(true); return gate; }
    public void Pause() { lock (nameLock) { if (Paused) return; Paused = true; pauseGate = NewGate(false); } Changed?.Invoke(null, "已请求暂停，当前文件完成后暂停队列"); }
    public void Continue() { lock (nameLock) { Paused = false; pauseGate.TrySetResult(true); } Changed?.Invoke(null, "继续处理"); }
    public string JobPath(ArchiveJob job) => Path.Combine(dataDirectory, "jobs", job.Id + ".json");
    public void Save(ArchiveJob job) { lock (persistenceLock) JsonStore.Write(JobPath(job), job); }
    public async Task RunAsync(ArchiveJob job, CancellationToken ct)
    {
        job.Settings.Validate(); Files.ValidateRoots(job.SourceRoot, job.OutputRoot);
        if (job.Items.Any(i => i.Media != null) && job.PlanVersion != MediaPolicy.PlanVersion) throw new InvalidOperationException("旧任务需要重新扫描，才能使用当前时间码与Main10规则。");
        if (job.Items.Any(i => i.RequiresReview)) throw new InvalidOperationException("存在时间码待决定文件，请逐个或批量选择处理方式后开始备份。");
        Continue(); reservedNames.Clear(); foreach (var p in job.Items) reservedNames.Add(p.Destination);
        job.State = "执行中"; job.Finished = null; Save(job);
        foreach (var directory in job.Directories)
        {
            var path = Path.Combine(job.OutputRoot, directory);
            Files.CheckDestination(job.OutputRoot, Path.Combine(path, ".validation")); Directory.CreateDirectory(path);
        }
        var candidates = job.Items.Where(i => i.Action != "未处理").ToList();
        var index = -1;
        async Task Worker()
        {
            while (!ct.IsCancellationRequested)
            {
                await pauseGate.Task.WaitAsync(ct);
                int current;
                lock (nameLock) { if (Paused) continue; current = Interlocked.Increment(ref index); }
                if (current >= candidates.Count) return;
                var p = candidates[current];
                try
                {
                    if (p.Status == "完成")
                    {
                        var source = new FileInfo(p.Source);
                        if (!source.Exists || source.Length != p.SourceSize || source.LastWriteTimeUtc != p.SourceModifiedUtc)
                            throw new IOException("源文件自扫描后已变化，请重新扫描。");
                        if (File.Exists(p.Destination) && new FileInfo(p.Destination).Length == p.OutputSize && await Files.HashAsync(p.Destination, ct) == p.OutputSha256)
                        { Changed?.Invoke(p, "已验证之前完成的文件"); continue; }
                        p.Status = "待处理"; p.Detail = "之前输出缺失或已变化，重新处理";
                    }
                    await ProcessItem(job, p, ct);
                }
                catch (OperationCanceledException) { p.Status = "已取消"; p.Progress = 0; throw; }
                catch (Exception ex) { p.Status = "失败"; p.Detail = ex.Message; p.Progress = 0; }
                finally { Save(job); Changed?.Invoke(p, p.DisplayStatus); }
            }
        }
        try
        {
            await Task.WhenAll(Enumerable.Range(0, job.Settings.Parallel).Select(_ => Worker()));
            ct.ThrowIfCancellationRequested();
            job.State = job.Items.Any(i => i.Status == "失败") ? "完成（有失败）" : "完成";
        }
        catch (OperationCanceledException) { job.State = "已取消，可恢复"; }
        finally { job.Finished = DateTime.Now; Continue(); Save(job); Changed?.Invoke(null, job.State); }
    }
    async Task ProcessItem(ArchiveJob job, PlanItem p, CancellationToken ct)
    {
        if (p.Action == "跳过") { p.Status = "已跳过"; p.Progress = 100; return; }
        Files.CheckDestination(job.SourceRoot, p.Source);
        Files.CheckDestination(job.OutputRoot, p.Destination);
        if ((File.GetAttributes(p.Source) & FileAttributes.ReparsePoint) != 0) throw new IOException("源文件变为符号链接，请重新扫描。");
        await using var sourceLock = new FileStream(p.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var source = new FileInfo(p.Source);
        if (source.Length != p.SourceSize || source.LastWriteTimeUtc != p.SourceModifiedUtc) throw new IOException("源文件自扫描后已变化，请重新扫描。");
        lock (nameLock)
        {
            if (File.Exists(p.Destination))
            {
                if (job.Settings.Collision == "跳过") { p.Status = "已跳过"; p.Progress = 100; p.Detail = "输出已存在"; return; }
                if (job.Settings.Collision == "自动重命名")
                {
                    var directory = Path.GetDirectoryName(p.Destination)!; var stem = Path.GetFileNameWithoutExtension(p.Destination); var ext = Path.GetExtension(p.Destination); int suffix = 1; string candidate;
                    do { candidate = Path.Combine(directory, $"{stem} ({suffix++}){ext}"); } while (File.Exists(candidate) || reservedNames.Contains(candidate));
                    p.Destination = candidate; reservedNames.Add(candidate);
                }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(p.Destination)!);
        var necessary = Math.Max(p.EstimatedSize, Math.Min(p.SourceSize, 64 * 1024 * 1024));
        var free = (availableSpace ?? Files.FreeSpace)(p.Destination);
        if (free < necessary + 16 * 1024 * 1024) throw new IOException("输出磁盘可用空间不足。");
        var temp = Path.Combine(Path.GetDirectoryName(p.Destination)!, ".va-" + Guid.NewGuid().ToString("N") + ".partial" + Path.GetExtension(p.Destination));
        p.Status = p.Action == "压缩" ? "压缩中" : "复制中"; p.Detail = ""; Changed?.Invoke(p, p.Status);
        try
        {
            if (p.Action == "压缩")
            {
                var args = CommandBuilder.Build(p, job.Settings, temp);
                p.Log = CommandBuilder.Preview(p, job.Settings, ffmpeg) + Environment.NewLine;
                var progressOut = new Dictionary<string, string>();
                var encoded = await ProcessRunner.RunAsync(ffmpeg, args, ct, line =>
                {
                    var key = line.Split('=', 2); if (key.Length != 2) return; progressOut[key[0]] = key[1];
                    if (key[0] != "progress") return;
                    if (progressOut.TryGetValue("out_time_us", out var us)) p.Progress = Math.Clamp(ProcessRunner.Number(us) / 1_000_000 / p.Media!.Duration * 90, 0, 90);
                    p.Detail = $"{progressOut.GetValueOrDefault("fps", "—")} fps · {progressOut.GetValueOrDefault("speed", "—")}";
                    Changed?.Invoke(p, p.Detail);
                });
                p.Log += encoded.Error;
                if (encoded.ExitCode != 0) throw new IOException("编码失败：" + LastMessage(encoded.Error));
                if (!p.DropTimecode) MediaPolicy.RestoreTimecodeDescriptions(temp, p.Media!);
                p.Status = "验证中"; p.Detail = "检查规格、轨道和完整解码"; Changed?.Invoke(p, p.Detail);
                var validation = await VerifyAsync(p, temp, job.Settings, ct);
                if (validation != null || new FileInfo(temp).Length > p.SourceSize)
                {
                    File.Delete(temp); sourceLock.Position = 0;
                    await CopyAsync(sourceLock, temp, p, ct);
                    p.Detail = "改为原样保留：" + (validation ?? "成品大于原文件");
                    p.Action = "复制"; p.Reason += "；" + p.Detail;
                }
                p.Progress = 96;
            }
            else await CopyAsync(sourceLock, temp, p, ct);
            ct.ThrowIfCancellationRequested();
            p.OutputSize = new FileInfo(temp).Length; p.OutputSha256 = await Files.HashAsync(temp, ct);
            // Verify copies against a source hash while the source remains locked against writes.
            if (p.Action == "复制")
            {
                sourceLock.Position = 0; var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(sourceLock, ct));
                if (p.OutputSize != p.SourceSize || sourceHash != p.OutputSha256) throw new IOException("复制文件 SHA-256 校验失败。");
            }
            Files.RestoreTimes(temp, source.CreationTimeUtc, source.LastWriteTimeUtc);
            Files.CheckDestination(job.OutputRoot, p.Destination);
            File.Move(temp, p.Destination, job.Settings.Collision == "覆盖");
            p.Status = "完成"; p.Progress = 100;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    async Task CopyAsync(FileStream source, string temp, PlanItem p, CancellationToken ct)
    {
        p.Status = "复制中"; await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        var buffer = new byte[1024 * 1024]; long written = 0; DateTime update = DateTime.MinValue;
        while (true)
        {
            var n = await source.ReadAsync(buffer, ct); if (n == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct); written += n;
            if ((DateTime.UtcNow - update).TotalMilliseconds >= 250)
            { update = DateTime.UtcNow; p.Progress = p.SourceSize == 0 ? 95 : written * 95d / p.SourceSize; Changed?.Invoke(p, "原样复制"); }
        }
        await output.FlushAsync(ct);
    }
    public async Task<string?> VerifyAsync(PlanItem item, string output, EncodeSettings settings, CancellationToken ct)
    {
        var source = item.Media!;
        settings = MediaPolicy.EffectiveSettings(item, settings);
        var actual = await new ProbeService(ffprobe).ProbeAsync(output, item.DropFrames, ct);
        if (actual.Width != item.TargetWidth || actual.Height != item.TargetHeight) return "输出尺寸不符";
        if (actual.Codec != (settings.Codec == "H.264" ? "h264" : settings.Codec == "HEVC" ? "hevc" : "av1")) return "输出编码不符";
        if ((!item.ConvertToTenBit420 && actual.BitDepth < source.BitDepth) || actual.BitDepth != item.TargetDepth) return "输出位深不符";
        if (item.ConvertToTenBit420 && actual.PixelFormat != "yuv420p10le") return "输出未转换为10-bit 4:2:0";
        var actualProfile = actual.Streams.Single(s => s.Index == actual.VideoIndex).Profile.Replace(" ", "").ToLowerInvariant();
        if (actualProfile != MediaPolicy.ResolveProfile(settings, item.TargetDepth)) return "输出Profile未按编码和位深适配";
        if (Math.Abs(actual.Duration - source.Duration) > Math.Max(.25, 2 / source.Fps)) return "输出时长不符";
        if (!item.DropFrames && Math.Abs(actual.Fps - source.Fps) > .1 || item.DropFrames && actual.MaxFps > settings.FpsLimit * 1.005 + .02) return "输出帧率不符";
        if (source.VideoMbps > 0 && actual.VideoMbps > source.VideoMbps + .000001) return "输出平均码率高于源视频";
        if (Math.Abs(actual.Rotation - source.Rotation) % 360 != 0) return "旋转信息未保留";
        var expected = MediaPolicy.MappedStreams(source, item.DropTimecode);
        if (actual.Streams.Count != expected.Count || actual.Chapters != source.Chapters) return "应保留的轨道或章节不符";
        if (actual.Streams.Any(MediaPolicy.IsDiscardedData)) return "输出仍包含应移除的特殊数据轨";
        for (int i = 0; i < expected.Count; i++)
        {
            var a = expected[i]; var b = actual.Streams[i];
            if (a.Type != b.Type || a.AttachedPicture != b.AttachedPicture) return "轨道类型不符";
            if (a.Index != source.VideoIndex && !(a.Type == "audio" && settings.Audio != "原样保留") && a.Codec != b.Codec) return "非目标轨道被改变";
            if (MediaPolicy.IsTimecode(a) && (!MediaPolicy.IsTimecode(b) || b.ExtraData.Length < 20 || !a.ExtraData.AsSpan(0, 20).SequenceEqual(b.ExtraData.AsSpan(0, 20)))) return "时间码标志或计时规则未保留";
            if (MediaPolicy.RegenerateTimecode(source) && MediaPolicy.IsTimecode(a) && !a.TimecodePacket.SequenceEqual(b.TimecodePacket)) return "原时间码计数未保留";
            foreach (var tag in a.Tags.Where(t => ImportantTag(t.Key) && !(item.DropTimecode && t.Key.Equals("timecode", StringComparison.OrdinalIgnoreCase))))
                if (!PreservedTag(tag, b.Tags)) return "轨道元数据未可靠保留：" + tag.Key;
        }
        foreach (var tag in source.Tags.Where(t => ImportantTag(t.Key)))
        {
            if (item.DropTimecode && tag.Key.Equals("timecode", StringComparison.OrdinalIgnoreCase)) continue;
            // Some camera files repeat the timecode as a global mdta tag. With
            // cover images the muxer uses ilst instead; the verified tmcd track
            // retains this same value and is the authoritative timecode.
            if (tag.Key.Equals("timecode", StringComparison.OrdinalIgnoreCase) && actual.Streams.Any(t => MediaPolicy.IsTimecode(t) && t.Tags.GetValueOrDefault("timecode") == tag.Value)) continue;
            if (!PreservedTag(tag, actual.Tags)) return "基础元数据未可靠保留：" + tag.Key;
        }
        if (source.Hdr)
        {
            if (actual.ColorPrimaries != source.ColorPrimaries || actual.ColorTransfer != source.ColorTransfer || actual.ColorSpace != source.ColorSpace || actual.ColorRange != source.ColorRange) return "HDR色彩信息未可靠保留";
            if (source.HdrMetadata.Any(s => !actual.HdrMetadata.Contains(s))) return "HDR必要元数据未可靠保留";
        }
        var decode = await ProcessRunner.RunAsync(ffmpeg, ["-v", "error", "-xerror", "-i", output, "-map", "0:v", "-map", "0:a?", "-f", "null", "-"], ct);
        if (decode.ExitCode != 0 || !string.IsNullOrWhiteSpace(decode.Error)) throw new IOException("成品解码校验失败：" + LastMessage(decode.Error));
        return null;
    }
    static bool ImportantTag(string key) => key.ToLowerInvariant() is not ("encoder" or "major_brand" or "minor_version" or "compatible_brands" or "handler_name" or "vendor_id" or "duration" or "bps" or "number_of_frames" or "number_of_bytes" or "_statistics_writing_app" or "_statistics_writing_date_utc" or "_statistics_tags");
    static bool EquivalentTag(string key, string a, string b) => key.Equals("creation_time", StringComparison.OrdinalIgnoreCase) && DateTimeOffset.TryParse(a, out var x) && DateTimeOffset.TryParse(b, out var y) ? x == y : a == b;
    // MOV stores unspecified language as Macintosh code 32767, which ffprobe
    // omits. It is equivalent to ISO-639 'und', not a lost known language.
    static bool PreservedTag(KeyValuePair<string, string> tag, Dictionary<string, string> actual) => actual.TryGetValue(tag.Key, out var value)
        ? EquivalentTag(tag.Key, tag.Value, value)
        : tag.Key.Equals("language", StringComparison.OrdinalIgnoreCase) && tag.Value == "und";
    static string LastMessage(string error) => string.Join(" · ", error.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3)).Trim();
}

public static class ReportService
{
    public static string Summary(ArchiveJob job)
    {
        var completed = job.Items.Where(i => i.Status == "完成").ToList();
        return $"压缩 {completed.Count(i => i.Action == "压缩")} · 原样复制 {completed.Count(i => i.Action == "复制")} · 跳过 {job.Items.Count(i => i.Status == "已跳过")} · 失败 {job.Items.Count(i => i.Status == "失败")} · 待决定 {job.Items.Count(i => i.RequiresReview)} · 未处理 {job.Items.Count(i => i.Action == "未处理")}\n已验证文件：原始 {Format.Bytes(job.CompletedSourceBytes)} → 输出 {Format.Bytes(job.OutputBytes)} · 节省 {Format.Bytes(job.SavedBytes)}{(job.CompletedSourceBytes > 0 ? $"（{100d * job.SavedBytes / job.CompletedSourceBytes:0.#}%）" : "")}";
    }
    public static void ExportCsv(ArchiveJob job, string path)
    {
        static string Q(object? value) => "\"" + (value?.ToString() ?? "").Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "相对路径,源规格,目标规格,操作,原因,状态,说明,原始字节,输出字节,输出路径,SHA256" };
        lines.AddRange(job.Items.Select(i => string.Join(',', new object?[] { i.RelativePath, i.SourceSpec, i.TargetSpec, i.Action, i.Reason, i.Status, i.Detail, i.SourceSize, i.OutputSize, i.Destination, i.OutputSha256 }.Select(Q))));
        File.WriteAllLines(path, lines, new UTF8Encoding(true));
    }
}
