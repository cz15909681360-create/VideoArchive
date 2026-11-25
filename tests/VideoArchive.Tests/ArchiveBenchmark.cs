using System.Collections.Concurrent;
using System.Diagnostics;
using VideoArchive.Core;

static class ArchiveBenchmark
{
    public static async Task RunAsync(string source, string work, string report, string[] candidates)
    {
        if (candidates.Any(mode => !System.Text.RegularExpressions.Regex.IsMatch(mode, @"^[1-8](?:-overlap)?(?:-cuda)?$")) ||
            candidates.Distinct().Count() != candidates.Length)
            throw new ArgumentException("Use unique options in the form 1, 2-overlap, 4-cuda or 2-overlap-cuda.");
        source = Path.GetFullPath(source);
        work = Path.GetFullPath(work);
        var ffmpeg = Files.FindTool("ffmpeg");
        var probe = new ProbeService(Files.FindTool("ffprobe"));
        await probe.PrepareAsync(CancellationToken.None);
        var results = new List<object>();
        var reference = new Dictionary<string, string>();
        for (int round = 0; round < 3; round++)
        {
            foreach (var mode in candidates.Skip(round % candidates.Length).Concat(candidates.Take(round % candidates.Length)))
            {
                var fields = mode.Split('-');
                int parallel = int.Parse(fields[0]);
                string run = Path.Combine(work, $"{round + 1}-{mode}");
                if (Directory.Exists(run)) throw new IOException("Benchmark output already exists: " + run);
                var settings = new EncodeSettings { Backend = "NVIDIA", Codec = "HEVC", RateMode = "VBR", Quality = 1.25, Parallel = parallel };
                var job = await new ScanService(probe).ScanAsync(source, Path.Combine(run, "output"), settings, null, CancellationToken.None);
                if (job.Items.Count == 0 || job.Items.Any(item => item.Action != "压缩"))
                    throw new InvalidOperationException("Benchmark requires every input to qualify for compression. " +
                        string.Join(" | ", job.Items.Where(item => item.Action != "压缩").Select(item => item.RelativePath + " " + item.Reason)));

                var service = new ArchiveService(Files.FindTool("ffmpeg"), Files.FindTool("ffprobe"), Path.Combine(run, "state"),
                    execution: new(fields.Contains("overlap"), fields.Contains("cuda")));
                var totals = new ConcurrentDictionary<string, double>();
                service.StageTimed += (stage, t) => totals.AddOrUpdate(stage, t.TotalSeconds, (_, seconds) => seconds + t.TotalSeconds);
                var watch = Stopwatch.StartNew();
                await service.RunAsync(job, CancellationToken.None);
                watch.Stop();
                var failures = job.Items.Where(item => item.Status != "完成" || item.Action != "压缩")
                    .Select(item => item.RelativePath + " " + item.DisplayStatus + " " + item.Log).ToList();
                var discrepancies = new List<string>();
                int hardware = 0;
                foreach (var item in job.Items.Where(item => item.Status == "完成" && item.Action == "压缩"))
                {
                    if (item.Log.Contains("-hwaccel:") && !item.Log.Contains("使用软件解码重试")) hardware++;
                    if (item.VerifiedVideo == null) throw new InvalidOperationException("Verified output specification missing.");
                    // Matching compressed packets, timings and track order imply identical decoded video. Every run
                    // has already decoded and validated all streams before publishing its output.
                    var packets = await ProcessRunner.RunAsync(ffmpeg, ["-v", "error", "-i", item.Destination, "-map", "0",
                        "-codec", "copy", "-f", "framehash", "-"], CancellationToken.None);
                    if (packets.ExitCode != 0) throw new IOException("Output comparison failed: " + packets.Error);
                    if (!reference.TryAdd(item.RelativePath, packets.Output) && reference[item.RelativePath] != packets.Output)
                        discrepancies.Add(item.RelativePath);
                }
                results.Add(new { mode, round = round + 1, count = job.Items.Count, wallSeconds = watch.Elapsed.TotalSeconds,
                    videoSeconds = job.Items.Sum(item => item.Media?.Duration ?? 0), stageSeconds = totals, hardware, failures, discrepancies });
                JsonStore.Write(report, results);
                Console.WriteLine($"{round + 1} {mode}: {watch.Elapsed.TotalSeconds:F2}s / {job.Items.Count} files, {failures.Count} failures, {discrepancies.Count} differences, {hardware} CUDA");
            }
        }
    }
}
