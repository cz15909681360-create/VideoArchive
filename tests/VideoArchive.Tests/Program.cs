using System.Text;
using VideoArchive.Core;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS " + name); }
var engine = new DecisionEngine();
var legacyConfiguration = System.Text.Json.JsonSerializer.Deserialize<AppConfiguration>("{\"Presets\":[]}", JsonStore.Options)!;
Check(legacyConfiguration.Appearance.WallpaperFile == "" && legacyConfiguration.Appearance.WallpaperOpacity == .35, "old settings load default appearance without losing presets");
var appearanceConfiguration = JsonStore.Clone(new AppConfiguration { Appearance = new() { WallpaperFile = "local-wallpaper.png", WallpaperOpacity = .65 }, Presets = [new() { Name = "原有备份方案" }] });
Check(appearanceConfiguration.Appearance.WallpaperOpacity == .65 && appearanceConfiguration.Presets.Single().Name == "原有备份方案", "wallpaper preferences coexist with encoding presets in persisted settings");
MediaInfo Media(double mbps = 45, double fps = 30, int w = 3840, int h = 2160) => new()
{
    Path = @"C:\source\clip.mp4", Size = (long)(mbps * 1_000_000 / 8 * 100), ModifiedUtc = DateTime.UtcNow, Width = w, Height = h,
    Codec = "hevc", PixelFormat = "yuv420p", Fps = fps, NominalFps = fps, MaxFps = fps, CadenceChecked = true, VideoMbps = mbps, Duration = 100,
    Streams = [new() { Index = 0, Type = "video", Codec = "hevc" }]
};
PlanItem Decide(MediaInfo m, EncodeSettings? s = null) => engine.Create(@"C:\source", @"C:\backup", m.Path, m.Size, m.ModifiedUtc, m, s ?? new() { BitrateMode = "固定上限", FixedMbps = 20, LimitFps = true, FpsLimit = 60 });
var p = Decide(Media(10), new() { BitrateMode = "固定上限", FixedMbps = 15 });
Check(p.Action == "复制", "10 Mbps below 15 Mbps: byte copy");
p = Decide(Media()); Check(p.Action == "压缩" && p.TargetFps == 30 && p.TargetMbps == 20 && !p.DropFrames, "45 Mbps/30 fps -> 20 Mbps/30 fps");
Check(Decide(Media(20.8)).Action == "复制", "10% bitrate tolerance");
Check(Decide(Media(22)).Action == "复制", "exact tolerance boundary");
Check(Decide(Media(23)).Action == "复制", "minimum savings threshold");
Check(Decide(Media(24)).Action == "压缩", "worthwhile savings");
p = Decide(Media(100, 60), new() { Limit1080 = true });
Check(p.TargetWidth == 1920 && p.TargetHeight == 1080 && p.TargetMbps == 15, "4K60 -> 1080P60 HEVC 15 Mbps");
p = Decide(Media(8, 30, 1280, 720), new() { Limit1080 = true }); Check(p.TargetWidth == 1280 && p.TargetHeight == 720, "no resolution upscaling");
p = Decide(Media(45, 60, 2160, 3840), new() { Limit1080 = true }); Check(p.TargetWidth == 1080 && p.TargetHeight == 1920, "portrait 1080P geometry");
var rotated = Media(); rotated.Rotation = -90;
p = Decide(rotated, new() { Limit1080 = true }); Check(p.TargetWidth == 1920 && p.TargetHeight == 1080, "display rotation respected");
p = Decide(Media(10, 120)); Check(p.Action == "压缩" && p.DropFrames && p.TargetFps == 60 && p.TargetMbps <= 10, "frame ceiling forces encode despite low savings");
var vfr = Media(10, 30); vfr.MaxFps = 120; vfr.Vfr = true;
Check(Decide(vfr).DropFrames, "VFR peak cadence checked, not just average");
Check(Decide(Media(10, 29.97)).TargetFps == 29.97, "fractional frame rate retained");
var lowAvc = Media(10); lowAvc.Codec = "h264";
Check(Decide(lowAvc).Action == "复制", "smart backup preserves other codec");
Check(Decide(lowAvc, new() { Unified = true }).Action == "压缩", "unified format forces encode");
var unknown = Media(); unknown.VideoMbps = 0; Check(Decide(unknown).Action == "复制", "unknown video bitrate preserves original");
var special = Media(); special.DynamicHdr = true; Check(Decide(special).Action == "复制", "dynamic HDR protected");
special = Media(); special.Streams.Add(new() { Index = 1, Type = "data", CodecTag = "rtmd", Mbps = 10 });
Check(Decide(special).Action == "压缩" && Decide(special).Reason.Contains("移除"), "camera data no longer blocks compression");
special = Media(); special.PixelFormat = "yuv422p10le"; special.BitDepth = 10;
Check(Decide(special).Action == "压缩" && Decide(special).ConvertToTenBit420 && Decide(special).TargetDepth == 10 && Decide(special).TargetProfile == "main10", "10-bit 422 automatically converts to 10-bit 420 HEVC Main10");
var highChromaSettings = new EncodeSettings { Codec = "AV1", Depth = "8-bit", EncoderPreset = "6", BitrateMode = "固定上限", FixedMbps = 20 };
var highChromaPlan = Decide(special, highChromaSettings);
var highChromaCommand = CommandBuilder.Build(highChromaPlan, highChromaSettings, @"C:\backup\high-chroma.mp4");
Check(highChromaPlan.TargetCodec == "HEVC" && highChromaPlan.TargetDepth == 10 && highChromaCommand.Contains("libx265") && highChromaCommand.Contains("main10") && highChromaCommand.Contains("medium"), "higher chroma overrides codec depth and incompatible encoder preset per file");
var high444 = Media(10); high444.PixelFormat = "yuv444p12le"; high444.BitDepth = 12;
Check(Decide(high444).Action == "压缩" && Decide(high444).TargetDepth == 10 && Decide(high444).TargetMbps <= 10, "12-bit 444 converts despite low bitrate without raising average target");
var alpha444 = JsonStore.Clone(high444); alpha444.PixelFormat = "yuva444p12le";
Check(Decide(alpha444).Action == "复制", "alpha channel remains protected during high chroma conversion");
var tenBit = Media(100, 60); tenBit.BitDepth = 10; tenBit.PixelFormat = "yuv420p10le";
Check(Decide(tenBit, new()).TargetMbps == 48, "10-bit SDR has no HDR multiplier");
tenBit.ColorTransfer = "arib-std-b67"; Check(Decide(tenBit, new()).TargetMbps == 60, "HDR multiplier");
Check(Recommendation.Get(3840, 2160, 60, "HEVC").Mbps == 48, "4K60 HEVC table");
Check(Recommendation.Get(1920, 1080, 30, "H.264").Mbps == 16, "AVC table midpoint");
Check(Recommendation.Get(1920, 1080, 60, "AV1").Mbps == 12.5, "AV1 table midpoint");
Check(Recommendation.Get(3000, 1700, 45, "HEVC").Estimated, "nonstandard recommendation labeled estimate");
var original = new EncodeSettings { Quality = 1.25, Limit1080 = true }; var snapshot = original.Snapshot(); original.Quality = .75;
Check(snapshot.Quality == 1.25 && snapshot.Limit1080, "settings snapshot is independent");
var command = CommandBuilder.Build(Decide(Media(45, 120)), new() { Backend = "NVIDIA", LimitFps = true }, @"C:\backup\a.mp4");
Check(command.Contains("passthrough") && command.Any(s => s.Contains("select=")) && !command.Contains("-r"), "no duplicate-frame rate conversion");
Check(Files.Contains(@"C:\", @"C:\folder\clip.mp4") && !Files.Contains(@"C:\source", @"C:\source-other\clip.mp4"), "drive root and directory-boundary containment");
var profileSource = Media(); profileSource.BitDepth = 10; profileSource.PixelFormat = "yuv420p10le";
Check(Decide(profileSource, new() { Profile = "main", BitrateMode = "固定上限", FixedMbps = 20 }).TargetProfile == "main10", "profile automatically adapts to source 10-bit");
Check(Decide(Media(), new() { Profile = "main10", BitrateMode = "固定上限", FixedMbps = 20 }).TargetDepth == 8, "legacy Main10 does not increase source depth");
Check(Files.BackupOutput(@"D:\素材") == @"D:\素材_已压缩", "default output is beside source");
Check(Files.BackupOutput(@"D:\素材\", @"E:\备份") == @"E:\备份\素材_已压缩", "other destination retains complete suffixed folder");
Check(!Files.Contains(Files.Normalize(@"D:\素材"), Files.BackupOutput(@"D:\素材")), "default output cannot recursively include itself");
Check(Decide(profileSource, new() { Codec = "H.264" }).TargetProfile == "high10" && Decide(Media(), new() { Codec = "AV1" }).TargetProfile == "main", "AVC and AV1 profile adaptation");
var mixed = Media(); mixed.VideoIndex = 2; mixed.Streams = [new() { Index = 0, Type = "data", CodecTag = "djmd" }, new() { Index = 1, Type = "data", CodecTag = "dbgi" }, new() { Index = 2, Type = "video", Codec = "hevc" }, new() { Index = 3, Type = "audio", Codec = "aac" }];
var mixedCommand = CommandBuilder.Build(Decide(mixed), new(), @"C:\backup\test.mp4");
Check(mixedCommand.Contains("0:2") && mixedCommand.Contains("0:3") && !mixedCommand.Contains("0:0") && !mixedCommand.Contains("0:1") && mixedCommand.Contains("-c:0"), "explicit mapping excludes camera data and remaps video index");
var incompleteTc = Media(); incompleteTc.Streams.Add(new() { Index = 1, Type = "data", CodecTag = "tmcd" });
Check(Decide(incompleteTc).RequiresReview, "unreadable timecode waits for user decision");
var reviewSettings = new EncodeSettings { BitrateMode = "固定上限", FixedMbps = 20 };
var preservedChoice = engine.Create(@"C:\source", @"C:\backup", incompleteTc.Path, incompleteTc.Size, incompleteTc.ModifiedUtc, incompleteTc, reviewSettings, "原样备份");
Check(preservedChoice.Action == "复制" && preservedChoice.Reason.Contains("用户选择"), "explicit original backup keeps source untouched");
var droppedChoice = engine.Create(@"C:\source", @"C:\backup", incompleteTc.Path, incompleteTc.Size, incompleteTc.ModifiedUtc, incompleteTc, reviewSettings, "舍弃时间码");
Check(droppedChoice.Action == "压缩" && droppedChoice.DropTimecode, "explicit timecode discard permits compression");
var discardedCommand = CommandBuilder.Build(droppedChoice, reviewSettings, @"C:\backup\discard.mp4");
Check(!discardedCommand.Contains("0:1") && !discardedCommand.Contains("-timecode") && discardedCommand.Contains("timecode=") && discardedCommand[discardedCommand.IndexOf("-write_tmcd") + 1] == "0", "discard command removes track and automatic timecode generation");
var dynamicWithTc = JsonStore.Clone(incompleteTc); dynamicWithTc.DynamicHdr = true;
Check(engine.Create(@"C:\source", @"C:\backup", dynamicWithTc.Path, dynamicWithTc.Size, dynamicWithTc.ModifiedUtc, dynamicWithTc, reviewSettings, "舍弃时间码").Action == "复制", "timecode choice does not bypass HDR protection");
var reviewJob = new ArchiveJob { SourceRoot = @"C:\source", OutputRoot = @"C:\backup", Settings = reviewSettings, PlanVersion = MediaPolicy.PlanVersion, Items = [Decide(incompleteTc), Decide(incompleteTc)] };
var reviewScanner = new ScanService(new ProbeService("unused"));
reviewScanner.Review(reviewJob, [reviewJob.Items[0]], "原样备份");
Check(reviewJob.Items[0].Action == "复制" && reviewJob.Items[1].RequiresReview, "individual decision leaves other files pending");
reviewScanner.Review(reviewJob, reviewJob.Items.Where(i => i.RequiresReview).ToArray(), "舍弃时间码");
Check(reviewJob.Items[0].TimecodeChoice == "原样备份" && reviewJob.Items[1].Action == "压缩", "batch decisions apply only selected files");
reviewScanner.Replan(reviewJob, new EncodeSettings { FixedMbps = 10, BitrateMode = "固定上限" });
reviewJob = JsonStore.Clone(reviewJob);
Check(reviewJob.Items[0].Action == "复制" && reviewJob.Items[1].DropTimecode && reviewJob.Items[1].TargetMbps == 10, "choices survive replan and persisted task reload");
reviewJob.State = "完成";
try { reviewScanner.Review(reviewJob, reviewJob.Items, "原样备份"); Check(false, "completed task review blocked"); } catch (InvalidOperationException) { Check(true, "completed task review blocked"); }
try { CommandBuilder.Build(Decide(incompleteTc), reviewSettings, @"C:\backup\pending.mp4"); Check(false, "pending command blocked"); } catch (InvalidOperationException) { Check(true, "pending command blocked"); }

var cacheTestPath = Path.GetFullPath(Path.Combine("artifacts", "capability-tests-" + Guid.NewGuid().ToString("N"), "capabilities.json"));
var identity = "environment-A"; int detects = 0;
Task<List<Capability>> FakeDetect(CancellationToken ct)
{
    detects++; return Task.FromResult((from b in new[] { "CPU", "NVIDIA", "Intel", "AMD" } from c in new[] { "H.264", "HEVC", "AV1" } select new Capability { Backend = b, Codec = c, Encoder = CapabilityService.Encoder(b, c), Available = b == "CPU", TenBit = c != "H.264" }).ToList());
}
CapabilityCache TestCache() => new("unused", cacheTestPath, _ => Task.FromResult(identity), FakeDetect);
var hardwareCache = TestCache(); await hardwareCache.GetAsync(false, CancellationToken.None);
Check(!hardwareCache.FromCache && detects == 1, "cold hardware environment performs detection");
hardwareCache = TestCache(); await hardwareCache.GetAsync(false, CancellationToken.None);
Check(hardwareCache.FromCache && detects == 1, "unchanged environment survives restart without encoder probes");
identity = "environment-B"; hardwareCache = TestCache(); await hardwareCache.GetAsync(false, CancellationToken.None);
Check(!hardwareCache.FromCache && detects == 2, "changed hardware software environment invalidates capabilities");
await hardwareCache.GetAsync(true, CancellationToken.None); Check(detects == 3, "manual force detection bypasses cache");
await File.WriteAllTextAsync(cacheTestPath, "invalid cache"); hardwareCache = TestCache(); await hardwareCache.GetAsync(false, CancellationToken.None);
Check(detects == 4, "corrupt capability cache safely redetected");
identity = "environment-C";
var timedOutCache = new CapabilityCache("unused", cacheTestPath, _ => Task.FromResult(identity), async ct => { var values = await FakeDetect(ct); values[0].Error = "检测超时"; return values; });
await timedOutCache.GetAsync(false, CancellationToken.None); hardwareCache = TestCache(); await hardwareCache.GetAsync(false, CancellationToken.None);
Check(!hardwareCache.FromCache && detects == 6, "incomplete detection does not become a valid cache");
var cacheBeforeCancellation = await File.ReadAllTextAsync(cacheTestPath);
var canceledDetection = new CapabilityCache("unused", cacheTestPath, _ => Task.FromResult(identity), _ => Task.FromCanceled<List<Capability>>(new CancellationToken(true)));
try { await canceledDetection.GetAsync(true, CancellationToken.None); Check(false, "canceled detection preserves previous cache"); }
catch (OperationCanceledException) { Check(await File.ReadAllTextAsync(cacheTestPath) == cacheBeforeCancellation, "canceled detection preserves previous cache"); }
var binaryPath = Path.Combine(Path.GetDirectoryName(cacheTestPath)!, "test-encoder.bin"); await File.WriteAllTextAsync(binaryPath, "version A");
var environmentA = await CapabilityCache.IdentityAsync(binaryPath, CancellationToken.None);
Check(environmentA == await CapabilityCache.IdentityAsync(binaryPath, CancellationToken.None), "actual machine fingerprint stable across repeated reads");
var binaryDate = File.GetLastWriteTimeUtc(binaryPath); await File.WriteAllTextAsync(binaryPath, "version B"); File.SetLastWriteTimeUtc(binaryPath, binaryDate);
Check(environmentA != await CapabilityCache.IdentityAsync(binaryPath, CancellationToken.None), "encoder binary content change invalidates actual fingerprint even with same size and timestamp");
var legacySource = Path.Combine(Path.GetDirectoryName(cacheTestPath)!, "legacy-source"); Directory.CreateDirectory(legacySource);
var legacyOutput = Files.BackupOutput(legacySource);
var legacyJob = new ArchiveJob { SourceRoot = legacySource, OutputRoot = legacyOutput, Items = [Decide(Media())] };
try { await new ArchiveService("unused", "unused", Path.GetDirectoryName(cacheTestPath)!).RunAsync(legacyJob, CancellationToken.None); Check(false, "legacy media plan requires rescan before output writes"); }
catch (InvalidOperationException) { Check(!Directory.Exists(legacyOutput), "legacy media plan requires rescan before output writes"); }

if (args.Contains("--integration"))
{
    var ffmpeg = Files.FindTool("ffmpeg"); var ffprobe = Files.FindTool("ffprobe");
    var root = Path.GetFullPath(Path.Combine("artifacts", "verification-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
    var source = Path.Combine(root, "源 素材 & 备份"); var output = Path.Combine(root, "输出"); var data = Path.Combine(root, "app-data");
    Directory.CreateDirectory(Path.Combine(source, "分类", "空目录"));
    var textFile = Path.Combine(source, "分类", "说明 & notes.txt"); await File.WriteAllTextAsync(textFile, "非视频资料必须原样保留。", Encoding.UTF8);
    var subtitle = Path.Combine(source, "分类", "字幕.srt"); await File.WriteAllTextAsync(subtitle, "1\n00:00:00,000 --> 00:00:02,000\nVideoArchive test\n", Encoding.UTF8);
    var clip = Path.Combine(source, "分类", "测试 & clip.mp4");
    async Task Run(string[] arguments)
    { var r = await ProcessRunner.RunAsync(ffmpeg, arguments, CancellationToken.None); if (r.ExitCode != 0) throw new Exception(r.Error); }
    var atomicFile = Path.Combine(root, "atomic-write.json"); JsonStore.Write(atomicFile, new EncodeSettings { FixedMbps = 1 });
    using (var reader = new FileStream(atomicFile, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var release = Task.Run(async () => { await Task.Delay(120); reader.Dispose(); });
        JsonStore.Write(atomicFile, new EncodeSettings { FixedMbps = 2 }); await release;
    }
    Check(JsonStore.Read<EncodeSettings>(atomicFile)!.FixedMbps == 2, "atomic JSON replacement tolerates temporary Windows reader lock");
    await Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-f", "lavfi", "-i", "sine=frequency=660:duration=3", "-i", subtitle, "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-c:v", "libx264", "-b:v", "8M", "-minrate", "8M", "-maxrate", "8M", "-bufsize", "16M", "-x264-params", "nal-hrd=cbr:force-cfr=1", "-c:a", "aac", "-c:s", "mov_text", "-metadata", "comment=preserve me", "-t", "3", clip]);
    var probe = new ProbeService(ffprobe); var info = await probe.ProbeAsync(clip, true, CancellationToken.None);
    var quickProbe = new ProbeService(ffprobe);
    await quickProbe.ProbeAsync(clip, false, CancellationToken.None);
    Check(quickProbe.ProcessCount == 1, "8-bit SDR scan does not decode frames");
    Check(info.Streams.Count(s => s.Type == "audio") == 2 && info.Streams.Any(s => s.Type == "subtitle"), "detect multi-audio and subtitles");
    Check(info.CadenceChecked && Math.Abs(info.Fps - 30) < .1 && info.VideoMbps > 3, "probe cadence and video bitrate");
    try { Files.ValidateRoots(source, Path.Combine(source, "nested")); Check(false, "nested root denied"); } catch (InvalidOperationException) { Check(true, "nested root denied"); }
    var s = new EncodeSettings { BitrateMode = "固定上限", FixedMbps = 1, Backend = "CPU", Collision = "覆盖" };
    var scan = new ScanService(probe); var job = await scan.ScanAsync(source, output, s, Console.WriteLine, CancellationToken.None);
    Check(job.Items.Count == 3 && job.Items.Count(i => i.Action == "压缩") == 1, "recursive scan with sidecars");
    var cacheFile = Path.Combine(root, "probe-cache.json");
    var cacheProbe = new ProbeService(ffprobe);
    var cachedScanner = new ScanService(cacheProbe, cachePath: cacheFile);
    await cachedScanner.ScanAsync(source, Files.BackupOutput(source), s, null, CancellationToken.None);
    var restartProbe = new ProbeService(ffprobe);
    var restartScanner = new ScanService(restartProbe, cachePath: cacheFile);
    var sibling = await restartScanner.ScanAsync(source, Files.BackupOutput(source), s, null, CancellationToken.None);
    Check(restartProbe.ProcessCount == 0 && restartScanner.LastCacheHits == 1, "unchanged video cache survives application restart");
    await new ArchiveService(ffmpeg, ffprobe, data).RunAsync(sibling, CancellationToken.None);
    Check(sibling.Items.All(i => i.Status == "完成") && Directory.Exists(Path.Combine(sibling.OutputRoot, "分类", "空目录")), "complete suffixed sibling backup");
    var rescan = await restartScanner.ScanAsync(source, sibling.OutputRoot, s, null, CancellationToken.None);
    Check(rescan.Items.Count == 3, "rescan excludes sibling output by directory boundary");
    File.SetLastWriteTimeUtc(clip, File.GetLastWriteTimeUtc(clip).AddSeconds(2));
    await restartScanner.ScanAsync(source, sibling.OutputRoot, s, null, CancellationToken.None);
    Check(restartProbe.ProcessCount == 1 && restartScanner.LastCacheHits == 0, "changed source invalidates persistent probe cache");
    File.SetLastWriteTimeUtc(clip, job.Items.Single(i => i.Media != null).SourceModifiedUtc);
    var encode = new ArchiveService(ffmpeg, ffprobe, data);
    await encode.RunAsync(job, CancellationToken.None);
    foreach (var i in job.Items) Console.WriteLine($"RESULT {i.RelativePath}: {i.Action} {i.Status} {i.Detail}\n{i.Log}");
    Check(job.Items.All(i => i.Status == "完成"), "complete real CPU archive");
    Check(job.Items.Single(i => i.Media != null).Action == "压缩", "CPU actually compressed rather than fallback");
    Check(Directory.Exists(Path.Combine(output, "分类", "空目录")), "empty directories mirrored");
    Check(await Files.HashAsync(textFile, CancellationToken.None) == await Files.HashAsync(Path.Combine(output, "分类", "说明 & notes.txt"), CancellationToken.None), "sidecar SHA-256 preserved");
    var outInfo = await probe.ProbeAsync(Path.Combine(output, "分类", "测试 & clip.mp4"), true, CancellationToken.None);
    Check(outInfo.Streams.Count == info.Streams.Count && outInfo.Codec == "hevc", "output tracks retained and codec changed");
    Check(outInfo.Tags.GetValueOrDefault("comment") == "preserve me", "container metadata retained");
    Check(File.GetLastWriteTimeUtc(Path.Combine(output, "分类", "测试 & clip.mp4")) == File.GetLastWriteTimeUtc(clip), "file modification time retained");
    var persisted = JsonStore.Read<ArchiveJob>(encode.JobPath(job))!;
    Check(persisted.Items.All(i => i.OutputSha256.Length == 64), "history and verified hashes persisted");
    await encode.RunAsync(persisted, CancellationToken.None); Check(persisted.Items.All(i => i.Status == "完成"), "resume verifies completed files");
    foreach (var backend in new[] { "NVIDIA", "Intel" })
    {
        var hardwareOutput = Path.Combine(root, backend);
        s.Backend = backend;
        var hardwareJob = await scan.ScanAsync(source, hardwareOutput, s, null, CancellationToken.None);
        await new ArchiveService(ffmpeg, ffprobe, data).RunAsync(hardwareJob, CancellationToken.None);
        var video = hardwareJob.Items.Single(i => i.Media != null);
        Console.WriteLine($"HARDWARE {backend}: {video.Status} {video.Detail}\n{video.Log}");
        Check(video.Status == "完成" && video.Action == "压缩", backend + " actual verified encode");
    }
    s.Backend = "CPU"; s.Collision = "跳过";
    var skip = await scan.ScanAsync(source, output, s, null, CancellationToken.None); Check(skip.Items.All(i => i.Action == "跳过"), "existing output skip policy");
    s.Collision = "自动重命名";
    var renamed = await scan.ScanAsync(source, output, s, null, CancellationToken.None); await encode.RunAsync(renamed, CancellationToken.None);
    Check(renamed.Items.All(i => i.Status == "完成" && Path.GetFileName(i.Destination).Contains("(1)")), "existing output automatic rename");
    var changed = await scan.ScanAsync(source, Path.Combine(root, "changed"), s, null, CancellationToken.None);
    await File.AppendAllTextAsync(textFile, "changed"); await encode.RunAsync(changed, CancellationToken.None);
    Check(changed.Items.Single(i => i.Source == textFile).Status == "失败", "source change detected before copying");
    var canceled = await scan.ScanAsync(source, Path.Combine(root, "cancel"), s, null, CancellationToken.None);
    using var cts = new CancellationTokenSource();
    var cancelService = new ArchiveService(ffmpeg, ffprobe, data); cancelService.Changed += (i, _) => { if (i?.Status == "压缩中") cts.Cancel(); };
    await cancelService.RunAsync(canceled, cts.Token);
    Check(canceled.State.StartsWith("已取消") && !Directory.GetFiles(canceled.OutputRoot, ".va-*", SearchOption.AllDirectories).Any(), "cancel cleans partials and preserves state");
    ReportService.ExportCsv(job, Path.Combine(root, "report.csv"));
    Check(File.ReadAllText(Path.Combine(root, "report.csv")).Contains("SHA256"), "CSV report exported");
    var diskJob = await scan.ScanAsync(source, Path.Combine(root, "no-space"), s, null, CancellationToken.None);
    await new ArchiveService(ffmpeg, ffprobe, data, _ => 0).RunAsync(diskJob, CancellationToken.None);
    Check(diskJob.Items.All(i => i.Status == "失败") && !Directory.GetFiles(diskJob.OutputRoot, "*", SearchOption.AllDirectories).Any(), "insufficient disk space leaves no output files");
    var variantDirectory = Path.Combine(root, "variants"); Directory.CreateDirectory(variantDirectory);
    var tcClip = Path.Combine(root, "timecode.mov");
    await Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=60000/1001:d=2", "-c:v", "libx264", "-b:v", "8M", "-minrate", "8M", "-maxrate", "8M", "-bufsize", "16M", "-x264-params", "nal-hrd=cbr", "-timecode", "01:00:00;00", tcClip]);
    var tcMedia = await probe.ProbeAsync(tcClip, true, CancellationToken.None);
    Check(tcMedia.Streams.Any(t => MediaPolicy.IsTimecode(t) && t.ExtraData.Length == 20), "timecode description and drop-frame flag detected");
    var tcReviewSource = Path.Combine(root, "tc-review-source"); Directory.CreateDirectory(tcReviewSource);
    var tcReviewClip = Path.Combine(tcReviewSource, "待选择时间码.mp4");
    await Run(["-v", "error", "-y", "-i", clip, "-map", "0", "-c", "copy", "-timecode", "01:00:00:00", tcReviewClip]);
    var tcReviewMedia = await probe.ProbeAsync(tcReviewClip, false, CancellationToken.None);
    var sourceHashBeforeReview = await Files.HashAsync(tcReviewClip, CancellationToken.None);
    tcReviewMedia.Streams.Single(MediaPolicy.IsTimecode).ExtraData = []; // Simulate the unsupported description, without changing the file.
    var tcReviewOutput = Files.BackupOutput(tcReviewSource);
    var tcReviewSettings = new EncodeSettings { BitrateMode = "固定上限", FixedMbps = .5, Collision = "覆盖" };
    var tcReviewItem = engine.Create(tcReviewSource, tcReviewOutput, tcReviewClip, tcReviewMedia.Size, tcReviewMedia.ModifiedUtc, tcReviewMedia, tcReviewSettings);
    var tcReviewJob = new ArchiveJob { SourceRoot = tcReviewSource, OutputRoot = tcReviewOutput, Settings = tcReviewSettings, Items = [tcReviewItem], Directories = ["."], PlanVersion = MediaPolicy.PlanVersion };
    try { await encode.RunAsync(tcReviewJob, CancellationToken.None); Check(false, "pending decision blocks all output writes"); }
    catch (InvalidOperationException) { Check(!Directory.Exists(tcReviewOutput), "pending decision blocks all output writes"); }
    reviewScanner.Review(tcReviewJob, tcReviewJob.Items.ToArray(), "舍弃时间码");
    await encode.RunAsync(tcReviewJob, CancellationToken.None);
    tcReviewItem = tcReviewJob.Items.Single();
    if (tcReviewItem.Status != "完成" || tcReviewItem.Action != "压缩") Console.WriteLine(tcReviewItem.Detail + "\n" + tcReviewItem.Log);
    Check(tcReviewItem.Status == "完成" && tcReviewItem.Action == "压缩", "explicitly discarded timecode actually compresses and passes validation");
    var discardedInfo = await probe.ProbeAsync(tcReviewItem.Destination, false, CancellationToken.None);
    Check(!discardedInfo.Streams.Any(MediaPolicy.IsTimecode) && !discardedInfo.Tags.ContainsKey("timecode") && discardedInfo.Streams.All(t => !t.Tags.ContainsKey("timecode")), "actual output has no timecode track or duplicate tags");
    Check(discardedInfo.Streams.Count(t => t.Type == "audio") == 2 && discardedInfo.Streams.Any(t => t.Type == "subtitle") && discardedInfo.Tags.GetValueOrDefault("comment") == "preserve me", "timecode discard retains audio subtitles and other metadata");
    tcReviewJob = new ArchiveJob { SourceRoot = tcReviewSource, OutputRoot = Path.Combine(root, "tc-review-copy"), Settings = tcReviewSettings, Items = [engine.Create(tcReviewSource, Path.Combine(root, "tc-review-copy"), tcReviewClip, tcReviewMedia.Size, tcReviewMedia.ModifiedUtc, tcReviewMedia, tcReviewSettings)], Directories = ["."], PlanVersion = MediaPolicy.PlanVersion };
    reviewScanner.Review(tcReviewJob, tcReviewJob.Items.ToArray(), "原样备份");
    await encode.RunAsync(tcReviewJob, CancellationToken.None);
    Check(tcReviewJob.Items.Single().Status == "完成" && await Files.HashAsync(tcReviewJob.Items.Single().Destination, CancellationToken.None) == sourceHashBeforeReview, "explicit original choice retains whole file including timecode by SHA256");
    Check(await Files.HashAsync(tcReviewClip, CancellationToken.None) == sourceHashBeforeReview, "timecode choices leave original media unchanged");
    foreach (var container in new[] { ".mov", ".mp4" })
    foreach (var fpsCap in new[] { false, true })
    {
        var tcSettings = new EncodeSettings { Backend = "CPU", BitrateMode = "固定上限", FixedMbps = .5, LimitFps = fpsCap, FpsLimit = 30 };
        var tcInputDir = Path.Combine(root, "tc-input-" + container[1..] + "-" + fpsCap); Directory.CreateDirectory(tcInputDir);
        var tcInput = Path.Combine(tcInputDir, "clip" + container); File.Copy(tcClip, tcInput); var tcInputInfo = await probe.ProbeAsync(tcInput, true, CancellationToken.None);
        var tcOutput = Files.BackupOutput(tcInputDir);
        var tcItem = engine.Create(tcInputDir, tcOutput, tcInput, tcInputInfo.Size, tcInputInfo.ModifiedUtc, tcInputInfo, tcSettings);
        var tcJob = new ArchiveJob { SourceRoot = tcInputDir, OutputRoot = tcOutput, Settings = tcSettings, Items = [tcItem], Directories = ["."], PlanVersion = MediaPolicy.PlanVersion };
        await new ArchiveService(ffmpeg, ffprobe, data).RunAsync(tcJob, CancellationToken.None);
        if (tcItem.Status != "完成" || tcItem.Action != "压缩") Console.WriteLine(tcItem.Detail + "\n" + tcItem.Log);
        Check(tcItem.Status == "完成" && tcItem.Action == "压缩", container + " timecode preserved through compression, fps limit=" + fpsCap);
        var preservedTc = await probe.ProbeAsync(tcItem.Destination, false, CancellationToken.None);
        Check(preservedTc.Streams.Single(MediaPolicy.IsTimecode).Tags["timecode"] == "01:00:00;00", container + " drop-frame timecode interpretation retained, fps limit=" + fpsCap);
    }
    var chromaSource = Path.Combine(root, "high-chroma-input"); Directory.CreateDirectory(chromaSource);
    foreach (var pixel in new[] { "yuv422p10le", "yuv444p10le", "yuv444p12le" })
        await Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=1", "-pix_fmt", pixel, "-c:v", "ffv1", Path.Combine(chromaSource, pixel + ".mkv")]);
    foreach (var backend in new[] { "CPU", "NVIDIA", "Intel" })
    {
        var chromaSettings = new EncodeSettings { Backend = backend, Codec = "AV1", Depth = "8-bit", BitrateMode = "固定上限", FixedMbps = .6 };
        var chromaJob = await scan.ScanAsync(chromaSource, Path.Combine(root, "high-chroma-" + backend), chromaSettings, null, CancellationToken.None);
        Check(chromaJob.Items.All(i => i.Action == "压缩" && i.TargetCodec == "HEVC" && i.TargetDepth == 10 && i.TargetProfile == "main10"), backend + " plans all high chroma sources as 10-bit HEVC Main10");
        await encode.RunAsync(chromaJob, CancellationToken.None);
        foreach (var chromaItem in chromaJob.Items)
        {
            if (chromaItem.Status != "完成" || chromaItem.Action != "压缩") Console.WriteLine(chromaItem.Detail + "\n" + chromaItem.Log);
            var actualChroma = await probe.ProbeAsync(chromaItem.Destination, false, CancellationToken.None);
            Check(chromaItem.Status == "完成" && chromaItem.Action == "压缩" && actualChroma.PixelFormat == "yuv420p10le" && actualChroma.Codec == "hevc" && actualChroma.BitDepth == 10, backend + " actual " + chromaItem.Media!.PixelFormat + " -> 10-bit 420 Main10");
        }
    }
    var slow = Path.Combine(variantDirectory, "120fps.mp4");
    await Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=120:d=2", "-c:v", "libx264", "-b:v", "8M", "-minrate", "8M", "-maxrate", "8M", "-bufsize", "16M", "-x264-params", "nal-hrd=cbr", slow]);
    var variable = Path.Combine(variantDirectory, "vfr.mkv");
    await Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=120:d=2", "-vf", "select='lt(t,1)+gte(t,1)*not(mod(n,4))'", "-fps_mode", "vfr", "-c:v", "libx264", "-b:v", "8M", variable]);
    var ten = Path.Combine(variantDirectory, "10bit.mkv");
    await Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=2", "-pix_fmt", "yuv420p10le", "-c:v", "libx265", "-b:v", "8M", "-x265-params", "log-level=error", ten]);
    var hlg = Path.Combine(variantDirectory, "HLG.mkv");
    await Run(["-v", "error", "-y", "-i", ten, "-c:v", "libx265", "-b:v", "8M", "-x265-params", "log-level=error:colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc", "-color_primaries", "bt2020", "-color_trc", "arib-std-b67", "-colorspace", "bt2020nc", hlg]);
    var hdr = Path.Combine(variantDirectory, "HDR10.mkv");
    await Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=2", "-pix_fmt", "yuv420p10le", "-c:v", "libx265", "-b:v", "8M", "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", "-x265-params", "log-level=error:hdr10=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400", hdr]);
    var rotation = Path.Combine(variantDirectory, "rotation.mp4");
    await Run(["-v", "error", "-y", "-display_rotation:v:0", "90", "-noautorotate", "-i", slow, "-c", "copy", rotation]);
    var high = Path.Combine(variantDirectory, "1440p.mp4");
    await Run(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=2560x1440:r=30:d=0.8", "-c:v", "libx264", "-preset", "ultrafast", "-b:v", "12M", high]);
    var variantSettings = new EncodeSettings { BitrateMode = "固定上限", FixedMbps = .6, LimitFps = true, FpsLimit = 60, Limit1080 = true, Collision = "覆盖" };
    var variants = await scan.ScanAsync(variantDirectory, Path.Combine(root, "variants-out"), variantSettings, null, CancellationToken.None);
    var vfrPlan = variants.Items.Single(i => i.Source == variable);
    Check(vfrPlan.Media!.CadenceChecked && vfrPlan.DropFrames, "real VFR peak cadence causes frame dropping");
    await encode.RunAsync(variants, CancellationToken.None);
    foreach (var i in variants.Items) Console.WriteLine($"VARIANT {i.RelativePath}: {i.Action} {i.Status} {i.Detail}\n{i.Log}");
    Check(variants.Items.All(i => i.Status == "完成"), "frame, rotation, bit-depth and HDR variants completed");
    var reduced = await probe.ProbeAsync(Path.Combine(variants.OutputRoot, "120fps.mp4"), true, CancellationToken.None);
    Check(reduced.MaxFps <= 60.1 && reduced.Streams.All(i => i.Type != "audio"), "actual 120->60 frame drop, no audio supported");
    var actualVfr = await probe.ProbeAsync(Path.Combine(variants.OutputRoot, "vfr.mkv"), true, CancellationToken.None);
    Check(actualVfr.MaxFps <= 60.1, "VFR output peak cadence under ceiling");
    var actualTen = await probe.ProbeAsync(Path.Combine(variants.OutputRoot, "10bit.mkv"), false, CancellationToken.None);
    Check(actualTen.BitDepth == 10, "actual 10-bit SDR preserved");
    var actualHlg = await probe.ProbeAsync(Path.Combine(variants.OutputRoot, "HLG.mkv"), false, CancellationToken.None);
    Check(actualHlg.BitDepth == 10 && actualHlg.ColorTransfer == "arib-std-b67", "HLG preserved or safely copied");
    var beforeHdr = await probe.ProbeAsync(hdr, false, CancellationToken.None);
    var afterHdr = await probe.ProbeAsync(Path.Combine(variants.OutputRoot, "HDR10.mkv"), false, CancellationToken.None);
    Check(beforeHdr.HdrMetadata.Count >= 2 && beforeHdr.HdrMetadata.All(afterHdr.HdrMetadata.Contains), "HDR10 mastering and light metadata preserved");
    var actualRotation = await probe.ProbeAsync(Path.Combine(variants.OutputRoot, "rotation.mp4"), false, CancellationToken.None);
    Check(Math.Abs(actualRotation.Rotation) == 90, "actual rotation retained");
    var scaled = await probe.ProbeAsync(Path.Combine(variants.OutputRoot, "1440p.mp4"), false, CancellationToken.None);
    Check(scaled.Width == 1920 && scaled.Height == 1080 && variants.Items.Single(i => i.Source == high).Action == "压缩", "actual proportional 1080P downscale");
    var pauseJob = await scan.ScanAsync(source, Path.Combine(root, "pause"), s, null, CancellationToken.None);
    var pauseService = new ArchiveService(ffmpeg, ffprobe, data); int started = 0;
    pauseService.Changed += (i, message) =>
    {
        if (i != null && i.Status == "压缩中" && Interlocked.Increment(ref started) == 1)
        { pauseService.Pause(); _ = Task.Run(async () => { await Task.Delay(1000); pauseService.Continue(); }); }
    };
    await pauseService.RunAsync(pauseJob, CancellationToken.None);
    Check(pauseJob.Items.All(i => i.Status == "完成"), "pause finishes current file and resumes queue");
    var parallelSettings = s.Snapshot(); parallelSettings.Parallel = 2;
    var parallelJob = await scan.ScanAsync(source, Path.Combine(root, "parallel"), parallelSettings, null, CancellationToken.None);
    await encode.RunAsync(parallelJob, CancellationToken.None);
    Check(parallelJob.Items.All(i => i.Status == "完成"), "bounded parallel backup");
    var badSource = Path.Combine(root, "bad-source"); Directory.CreateDirectory(badSource); await File.WriteAllTextAsync(Path.Combine(badSource, "damaged.mp4"), "not a valid video");
    var badJob = await scan.ScanAsync(badSource, Path.Combine(root, "bad-copy"), s, null, CancellationToken.None);
    await encode.RunAsync(badJob, CancellationToken.None);
    Check(badJob.Items.Single().Action == "复制" && badJob.Items.Single().Reason.Contains("探测失败") && badJob.Items.Single().Status == "完成", "damaged input preserved with explicit warning");
    foreach (var backend in new[] { "CPU", "NVIDIA", "Intel" })
    {
        foreach (var codec in new[] { "H.264", "AV1" })
        {
            var combination = new EncodeSettings { Backend = backend, Codec = codec, BitrateMode = "固定上限", FixedMbps = 1, Collision = "覆盖" };
            var combinationJob = await scan.ScanAsync(source, Path.Combine(root, "matrix-" + backend + "-" + codec), combination, null, CancellationToken.None);
            await encode.RunAsync(combinationJob, CancellationToken.None);
            var video = combinationJob.Items.Single(i => i.Media != null);
            if (video.Status != "完成") Console.WriteLine(video.Detail + "\n" + video.Log);
            Check(video.Status == "完成" && video.Action == "压缩", backend + " " + codec + " VBR actual encode");
        }
        foreach (var mode in new[] { "CBR", "恒定质量" })
        {
            var combination = new EncodeSettings { Backend = backend, RateMode = mode, BitrateMode = "固定上限", FixedMbps = 1, Collision = "覆盖" };
            var combinationJob = await scan.ScanAsync(source, Path.Combine(root, "rate-" + backend + "-" + mode), combination, null, CancellationToken.None);
            await encode.RunAsync(combinationJob, CancellationToken.None);
            var video = combinationJob.Items.Single(i => i.Media != null);
            if (video.Status != "完成") Console.WriteLine(video.Detail + "\n" + video.Log);
            Check(video.Status == "完成" && video.Action == "压缩", backend + " " + mode + " actual encode");
        }
    }
    Console.WriteLine("INTEGRATION_ARTIFACTS=" + root);
}
if (args.Contains("--high-chroma-check"))
{
    var userJobPath = Directory.GetFiles(Path.Combine(JsonStore.DataDirectory, "jobs"), "*.json").OrderByDescending(File.GetLastWriteTimeUtc).First();
    var userJob = JsonStore.Read<ArchiveJob>(userJobPath)!;
    var sample = userJob.Items.Where(i => i.Media != null && MediaPolicy.NeedsTenBit420(i.Media)).OrderBy(i => i.SourceSize).First();
    var ffmpeg = Files.FindTool("ffmpeg"); var ffprobe = Files.FindTool("ffprobe");
    var root = Path.GetFullPath(Path.Combine("artifacts", "real-high-chroma-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
    var originalHash = await Files.HashAsync(sample.Source, CancellationToken.None);
    var media = await new ProbeService(ffprobe).ProbeAsync(sample.Source, false, CancellationToken.None);
    var settings = new EncodeSettings { Backend = "NVIDIA", BitrateMode = "固定上限", FixedMbps = 15 };
    var item = engine.Create(userJob.SourceRoot, Path.Combine(root, "output"), sample.Source, media.Size, media.ModifiedUtc, media, settings);
    var realJob = new ArchiveJob { SourceRoot = userJob.SourceRoot, OutputRoot = Path.Combine(root, "output"), Settings = settings, PlanVersion = MediaPolicy.PlanVersion, Items = [item], Directories = ["."] };
    var scanner = new ScanService(new ProbeService(ffprobe));
    Check(item.RequiresReview, "real Panasonic high-chroma timecode requests user choice");
    scanner.Review(realJob, realJob.Items.ToArray(), "舍弃时间码");
    item = realJob.Items.Single();
    Check(item.Action == "压缩" && item.TargetCodec == "HEVC" && item.TargetProfile == "main10", "real Panasonic high-chroma now eligible for Main10 compression");
    await new ArchiveService(ffmpeg, ffprobe, Path.Combine(root, "data")).RunAsync(realJob, CancellationToken.None);
    Console.WriteLine($"REAL_HIGH_CHROMA {item.Status} {item.Action} {item.Detail}\n{item.Log}");
    var actual = await new ProbeService(ffprobe).ProbeAsync(item.Destination, false, CancellationToken.None);
    Check(item.Status == "完成" && item.Action == "压缩" && actual.PixelFormat == "yuv420p10le" && actual.BitDepth == 10 && actual.Codec == "hevc" && !actual.Streams.Any(MediaPolicy.IsTimecode), "real Panasonic compresses to 10-bit 420 Main10 without timecode");
    Check(actual.Streams.Count(t => t.Type == "audio") == media.Streams.Count(t => t.Type == "audio") && actual.Tags.GetValueOrDefault("com.panasonic.Semi-Pro.metadata.xml") == media.Tags.GetValueOrDefault("com.panasonic.Semi-Pro.metadata.xml"), "real Panasonic audio and remaining metadata retained");
    Check(await Files.HashAsync(sample.Source, CancellationToken.None) == originalHash, "real Panasonic source hash unchanged");
    Console.WriteLine("REAL_HIGH_CHROMA_ARTIFACTS=" + root);
}
if (args.Contains("--capability-check"))
{
    var ffmpeg = Files.FindTool("ffmpeg");
    var realCachePath = Path.GetFullPath(Path.Combine("artifacts", "capability-check-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), "capabilities.json"));
    var watch = System.Diagnostics.Stopwatch.StartNew(); int realDetections = 0;
    var realCache = new CapabilityCache(ffmpeg, realCachePath, detect: async ct => { realDetections++; return await new CapabilityService(ffmpeg).DetectAsync(ct); });
    var realCapabilities = await realCache.GetAsync(false, CancellationToken.None);
    Check(realCapabilities.Count == 12 && !realCache.FromCache && realDetections == 1, "actual cold capability detection completes and saves");
    Console.WriteLine("COLD_CAPABILITIES_SECONDS=" + watch.Elapsed.TotalSeconds.ToString("0.000")); watch.Restart();
    realCache = new CapabilityCache(ffmpeg, realCachePath, detect: _ => throw new Exception("Unexpected encoder probe on unchanged environment"));
    var cachedCapabilities = await realCache.GetAsync(false, CancellationToken.None);
    Check(realCache.FromCache && realDetections == 1 && cachedCapabilities.Select(v => (v.Encoder, v.Available, v.TenBit)).SequenceEqual(realCapabilities.Select(v => (v.Encoder, v.Available, v.TenBit))), "actual second launch uses identical cached capabilities without short encodes");
    Console.WriteLine("WARM_CAPABILITIES_SECONDS=" + watch.Elapsed.TotalSeconds.ToString("0.000"));
    JsonStore.Write(Path.Combine(Path.GetDirectoryName(realCachePath)!, "result.json"), new { realCache.Note, Available = cachedCapabilities.Where(c => c.Available).Select(c => c.Encoder) });
}
if (args.Contains("--scan-benchmark"))
{
    var jobPath = Directory.GetFiles(Path.Combine(JsonStore.DataDirectory, "jobs"), "*.json").OrderByDescending(File.GetLastWriteTimeUtc).First();
    var userJob = JsonStore.Read<ArchiveJob>(jobPath)!;
    var benchmarkRoot = Path.GetFullPath(Path.Combine("artifacts", "scan-benchmark-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
    Directory.CreateDirectory(benchmarkRoot);
    var cacheFile = Path.Combine(benchmarkRoot, "probe-cache.json");
    var probe = new ProbeService(Files.FindTool("ffprobe"));
    var scanner = new ScanService(probe, cachePath: cacheFile);
    var settings = userJob.Settings.Snapshot(); settings.LimitFps = false;
    var cold = await scanner.ScanAsync(userJob.SourceRoot, Files.BackupOutput(userJob.SourceRoot), settings, null, CancellationToken.None);
    Console.WriteLine($"COLD_SCAN files={cold.Items.Count}, videos={cold.Items.Count(i => i.Media != null)}, seconds={scanner.LastScanTime.TotalSeconds:0.000}, processes={probe.ProcessCount}, compress={cold.Items.Count(i => i.Action == "压缩")}");
    var restartedProbe = new ProbeService(Files.FindTool("ffprobe"));
    var restartedScanner = new ScanService(restartedProbe, cachePath: cacheFile);
    var warm = await restartedScanner.ScanAsync(userJob.SourceRoot, Files.BackupOutput(userJob.SourceRoot), settings, null, CancellationToken.None);
    Console.WriteLine($"WARM_SCAN files={warm.Items.Count}, seconds={restartedScanner.LastScanTime.TotalSeconds:0.000}, hits={restartedScanner.LastCacheHits}, processes={restartedProbe.ProcessCount}");
    Check(cold.Items.Count == warm.Items.Count && cold.Items.Select(i => (i.RelativePath, i.Action, i.TargetProfile)).SequenceEqual(warm.Items.Select(i => (i.RelativePath, i.Action, i.TargetProfile))), "persistent-cache scan returns identical plans");
    JsonStore.Write(Path.Combine(benchmarkRoot, "plan.json"), cold);
}
if (args.Contains("--camera-check"))
{
    using var inventory = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine("artifacts", "data-assessment-inventory.json")));
    var ffmpeg = Files.FindTool("ffmpeg"); var ffprobe = Files.FindTool("ffprobe");
    var root = Path.GetFullPath(Path.Combine("artifacts", "camera-check-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))); Directory.CreateDirectory(root);
    var probe = new ProbeService(ffprobe);
    foreach (var signature in new[] { "rtmd", "djmd+dbgi", "tmcd", "djmd+dbgi+tmcd" })
    {
        var entry = inventory.RootElement.EnumerateArray().Where(e => e.GetProperty("Signature").GetString() == signature).OrderBy(e => e.GetProperty("Size").GetInt64()).First();
        var sourcePath = entry.GetProperty("Source").GetString()!;
        var info = await probe.ProbeAsync(sourcePath, false, CancellationToken.None);
        var settings = new EncodeSettings { Backend = "NVIDIA", BitrateMode = "固定上限", FixedMbps = 5 };
        var output = Path.Combine(root, signature.Replace('+', '_'));
        var sourceDir = Path.GetDirectoryName(sourcePath)!;
        var item = engine.Create(sourceDir, output, sourcePath, info.Size, info.ModifiedUtc, info, settings);
        Check(item.Action == "压缩", "real " + signature + " can be planned for compression");
        var job = new ArchiveJob { SourceRoot = sourceDir, OutputRoot = output, Settings = settings, Items = [item], Directories = ["."], PlanVersion = MediaPolicy.PlanVersion };
        await new ArchiveService(ffmpeg, ffprobe, Path.Combine(root, "data")).RunAsync(job, CancellationToken.None);
        Console.WriteLine("CAMERA " + signature + " " + item.Status + " " + item.Detail + "\n" + item.Log);
        Check(item.Status == "完成" && item.Action == "压缩", "real " + signature + " compresses without original-copy fallback");
        var actual = await probe.ProbeAsync(item.Destination, false, CancellationToken.None);
        Check(!actual.Streams.Any(MediaPolicy.IsDiscardedData), "real " + signature + " output removes non-timecode data");
        if (info.Streams.Any(MediaPolicy.IsTimecode)) Check(actual.Streams.Single(MediaPolicy.IsTimecode).Tags["timecode"] == info.Streams.Single(MediaPolicy.IsTimecode).Tags["timecode"], "real original timecode preserved for " + signature);
    }
    Console.WriteLine("CAMERA_ARTIFACTS=" + root);
}
Console.WriteLine($"ALL {checks} CHECKS PASSED");
