using VideoArchive.Core;

internal static class MxfChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var ffmpeg = Files.FindTool("ffmpeg");
        var ffprobe = Files.FindTool("ffprobe");
        var root = Path.GetFullPath(Path.Combine("artifacts", "mxf-v1.3.3-qa", "media-" + Guid.NewGuid().ToString("N")[..8]));
        var source = Directory.CreateDirectory(Path.Combine(root, "MXF 素材")).FullName;
        var data = Path.Combine(root, "history");
        var output = Path.Combine(root, "MOV 成品");
        var ct = CancellationToken.None;
        var archive = new ArchiveService(ffmpeg, ffprobe, data);
        var probe = new ProbeService(ffprobe);
        var scanner = new ScanService(probe);
        var settings = new EncodeSettings { Codec = "AV1", Audio = "AAC转码", Depth = "8-bit",
            BitrateMode = "固定上限", FixedMbps = .2, Collision = "覆盖", Parallel = 2 };
        string regular = Path.Combine(source, "普通.mxf"), drop = Path.Combine(source, "跳帧.MXF");
        foreach (var (file, rate, tcValue) in new[] { (regular, "25", "01:02:03:04"), (drop, "30000/1001", "02:03:04;05") })
        {
            var created = await ProcessRunner.RunAsync(ffmpeg,
                ["-v", "error", "-f", "lavfi", "-i", $"testsrc2=s=320x180:r={rate}:d=2",
                 "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
                 "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=2",
                 "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "mpeg2video", "-q:v", "2",
                 "-pix_fmt", "yuv422p", "-c:a", "pcm_s24le", "-timecode", tcValue, file], ct);
            check(created.ExitCode == 0, "MXF fixture " + tcValue + " " + created.Error);
        }
        var before = new Dictionary<string, string>
        {
            [regular] = await Files.HashAsync(regular, ct), [drop] = await Files.HashAsync(drop, ct)
        };
        var job = await scanner.ScanAsync(source, output, settings, null, ct);
        check(job.Items.Count == 2 && job.Items.All(item => item.MxfToMov && item.Action == "压缩" &&
            item.TargetCodec == "HEVC" && item.TargetDepth == 10 && item.TargetProfile == "main10" &&
            Path.GetExtension(item.Destination) == ".mov" && item.TargetSpec.Contains("MOV")), "MXF plans MOV/Main10 even under AV1/8-bit/AAC settings");
        await archive.RunAsync(job, ct);
        foreach (var item in job.Items)
        {
            check(item.Status == "完成" && item.Action == "压缩" && File.Exists(item.Destination) &&
                item.OutputSize < item.SourceSize, "compressed MXF actually produced smaller MOV: " + item.Detail + " " + item.Log);
            var actual = await probe.ProbeAsync(item.Destination, false, ct);
            var audio = actual.Streams.Where(stream => stream.Type == "audio").ToArray();
            check(actual.Tags.GetValueOrDefault("major_brand")?.Trim() == "qt" &&
                actual.Codec == "hevc" && actual.BitDepth == 10 && actual.Chroma == "4:2:0" &&
                audio.Length == 2 && audio.All(stream => stream.Codec == "pcm_s24le" && stream.Channels == 1 && stream.SampleRate == 48000),
                "MOV contains Main10 and both original mono PCM24 tracks");
            check(await SoundHash(item.Source) == await SoundHash(item.Destination), "decoded audio hashes match in track order");
            var timecode = actual.Streams.Single(MediaPolicy.IsTimecode);
            check(timecode.Tags["timecode"] == item.MxfTimecode, "MOV timecode round-trip " + item.MxfTimecode);
        }
        var resumed = JsonStore.Read<ArchiveJob>(archive.JobPath(job))!;
        int verifiedOnResume = 0;
        void ObserveResume(PlanItem? item, string message)
        {
            if (item != null && message == "已验证之前完成的文件") Interlocked.Increment(ref verifiedOnResume);
        }
        archive.Changed += ObserveResume;
        await archive.RunAsync(resumed, ct);
        archive.Changed -= ObserveResume;
        check(verifiedOnResume == 2 && resumed.Items.All(item => item.OutputSha256 == job.Items.Single(original => original.Source == item.Source).OutputSha256 &&
            item.Status == "完成" && item.Action == "压缩"), "history resumes verified MOV paths without re-encoding");

        var inspection = new DecisionEngine();
        var media = job.Items.Single(item => item.Source == regular).Media!;
        var lowRate = settings.Snapshot(); lowRate.FixedMbps = 20;
        var originalPlan = inspection.Create(source, Path.Combine(root, "low-rate"), regular, media.Size, media.ModifiedUtc, media, lowRate);
        check(originalPlan.Action == "复制" && originalPlan.Destination.EndsWith(".mxf"), "MXF format alone does not force encoding");
        var withoutTimecode = JsonStore.Clone(media); withoutTimecode.Tags.Remove("timecode");
        var noTc = inspection.Create(source, output, regular, media.Size, media.ModifiedUtc, withoutTimecode, settings);
        var command = CommandBuilder.Build(noTc, settings, noTc.Destination);
        check(noTc.Action == "压缩" && noTc.MxfTimecode == null && !command.Contains("-timecode") &&
            command[command.IndexOf("-write_tmcd") + 1] == "0", "MXF with no timecode adds no timecode track");

        var unsupported = await new ScanService(probe, (_, _) => false).ScanAsync(source, Path.Combine(root, "no-main10"), settings, null, ct);
        check(unsupported.Items.All(item => item.Action == "复制" && Path.GetExtension(item.Destination).Equals(".mxf", StringComparison.OrdinalIgnoreCase)),
            "unavailable Main10 keeps original suffix and copy action");
        var atom = JsonStore.Clone(media); atom.Tags["operational_pattern_ul"] = "060e2b34.04010102.0d010201.10030000";
        check(inspection.Create(source, output, regular, media.Size, media.ModifiedUtc, atom, settings).Action == "复制", "OP-Atom stays an original copy");

        var issues = await scanner.ScanAsync(source, Path.Combine(root, "decisions"), settings, null, ct);
        var undecided = issues.Items.Single(item => item.Source == regular);
        undecided.Media = JsonStore.Clone(undecided.Media!);
        undecided.Media!.Streams[0].Tags["timecode"] = "05:04:03:02";
        scanner.Replan(issues, settings);
        undecided = issues.Items.Single(item => item.Source == regular);
        check(undecided.RequiresReview, "conflicting source timecodes trigger review");
        scanner.Review(issues, [undecided], "舍弃时间码");
        await archive.RunAsync(issues, ct);
        var discarded = issues.Items.Single(item => item.Source == regular);
        check(discarded.Status == "完成" && discarded.Action == "压缩", "discard-timecode choice still compresses and verifies: " + discarded.Detail);
        var noTimecode = await probe.ProbeAsync(discarded.Destination, false, ct);
        check(!noTimecode.Streams.Any(MediaPolicy.IsTimecode) && !noTimecode.Tags.ContainsKey("timecode") &&
            noTimecode.Streams.All(stream => !stream.Tags.ContainsKey("timecode")), "discard-timecode MOV has neither track nor tags");

        var fallback = await scanner.ScanAsync(source, Path.Combine(root, "fallback"), settings, null, ct);
        var failedVerify = fallback.Items.Single(item => item.Source == regular);
        failedVerify.Media = JsonStore.Clone(failedVerify.Media!);
        failedVerify.Media!.Streams.Single(stream => stream.Type == "audio" && stream.Index == 1).SampleRate = 44100;
        await archive.RunAsync(fallback, ct);
        check(failedVerify.Status == "完成" && failedVerify.Action == "复制" &&
            failedVerify.Destination == Path.Combine(fallback.OutputRoot, Path.GetFileName(regular)) &&
            !File.Exists(Path.ChangeExtension(failedVerify.Destination, ".mov")) &&
            failedVerify.OutputSha256 == before[regular], "verification fallback writes original MXF bytes under original suffix");
        var fallbackReloaded = JsonStore.Read<ArchiveJob>(archive.JobPath(fallback))!;
        await archive.RunAsync(fallbackReloaded, ct);
        var restored = fallbackReloaded.Items.Single(item => item.Source == regular);
        check(restored.Status == "完成" && restored.Destination == failedVerify.Destination &&
            restored.OutputSha256 == before[regular], "fallback destination and hash survive history reload");
        string reportPath = Path.Combine(root, "report.csv");
        ReportService.ExportCsv(fallbackReloaded, reportPath);
        check((await File.ReadAllTextAsync(reportPath, ct)).Contains(restored.Destination, StringComparison.Ordinal),
            "report records the actual fallback path");

        string conflictingMov = Path.ChangeExtension(regular, ".mov");
        await File.WriteAllTextAsync(conflictingMov, "same-name sample", ct);
        var collision = await scanner.ScanAsync(source, Path.Combine(root, "collision"), settings, null, ct);
        scanner.Replan(collision, settings);
        var names = collision.Items.Select(item => item.Destination).ToList();
        check(collision.Items.Single(item => item.Source == regular).Action == "复制" &&
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count, "same-name MOV prevents conflicting MXF output in scan and replan");
        foreach (var (file, originalHash) in before)
            check(await Files.HashAsync(file, ct) == originalHash, "original MXF unchanged: " + Path.GetFileName(file));

        async Task<string> SoundHash(string file)
        {
            var result = await ProcessRunner.RunAsync(ffmpeg, ["-v", "error", "-i", file, "-map", "0:a",
                "-c:a", "pcm_s24le", "-f", "streamhash", "-hash", "sha256", "-"], ct);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
                throw new IOException("Audio hash failed: " + result.Error);
            return result.Output;
        }
    }
}
