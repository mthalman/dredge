using System.Diagnostics;
using System.Formats.Tar;
using System.Text.Json;
using static Valleysoft.Dredge.Tests.SemanticCompareFixture;

namespace Valleysoft.Dredge.Tests;

public sealed class ComparePerformanceTests
{
    public static bool PerformanceEnabled => Environment.GetEnvironmentVariable("DREDGE_MEASURE_COMPARE") == "1";
    public static bool SmokeEnabled => Environment.GetEnvironmentVariable("DREDGE_COMPARE_SMOKE") == "1";

    [Fact(Skip = "Set DREDGE_COMPARE_SMOKE=1 to write fixed archive smoke-test inputs.", SkipUnless = nameof(SmokeEnabled))]
    public async Task WriteSmokeInputs()
    {
        var (baseline, target) = Fixtures("small");
        string root = Path.Combine(RepositoryRoot(), ".compare-smoke");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "base.tar"), Export(baseline, "dredge-406-base:fixture"), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(root, "target.tar"), Export(target, "dredge-406-target:fixture"), TestContext.Current.CancellationToken);
        using var fixture = new SemanticCompareFixture { UseDaemonArchive = true };
        fixture.Images["base"] = baseline;
        fixture.Images["target"] = target;
        fixture.Images["docker://target"] = target;
        var complete = await fixture.CompareAsync("base", "target");
        var partial = await fixture.CompareAsync("base", "docker://target");
        await File.WriteAllTextAsync(Path.Combine(root, "full-report.json"), JsonHelper.Serialize(complete), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "partial-report.json"), JsonHelper.Serialize(partial), TestContext.Current.CancellationToken);
    }

    [Fact(Skip = "Set DREDGE_MEASURE_COMPARE=1 for explicit resource measurements.", SkipUnless = nameof(PerformanceEnabled))]
    public async Task MeasureFixedFixtures()
    {
        List<Measurement> measurements = [];
        foreach (string name in new[] { "small", "dependencies", "large" })
        {
            var (baseline, target) = Fixtures(name);
            foreach (string source in new[] { "registry", "mixed" })
            {
                foreach (string format in new[] { "text", "json" })
                {
                    for (int repeat = 0; repeat < 3; repeat++)
                    {
                        using var fixture = new SemanticCompareFixture { UseDaemonArchive = true };
                        fixture.Images["base"] = baseline;
                        string current = source == "registry" ? "target" : "docker://target";
                        fixture.Images[current] = target;
                        foreach (string cache in new[] { "cold", "warm" })
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            long allocated = GC.GetTotalAllocatedBytes(true);
                            long rss = Process.GetCurrentProcess().WorkingSet64;
                            long peakCache = 0, peakScratch = 0, peakDisk = 0;
                            using CancellationTokenSource sampling = new();
                            Task sample = Task.Run(async () =>
                            {
                                while (!sampling.IsCancellationRequested)
                                {
                                    rss = Math.Max(rss, Process.GetCurrentProcess().WorkingSet64);
                                    peakCache = Math.Max(peakCache, Bytes(Path.Combine(fixture.Root, "cache")));
                                    peakScratch = Math.Max(peakScratch, Bytes(Path.Combine(fixture.Root, "daemon-export")));
                                    peakDisk = Math.Max(peakDisk, Bytes(fixture.Root));
                                    try { await Task.Delay(5, sampling.Token); }
                                    catch (OperationCanceledException) { break; }
                                }
                            });
                            using var output = new StringWriter();
                            var command = fixture.Command(output);
                            var parse = command.Parse(["base", current, "--output", format, "--limit", "1"]);
                            Assert.Empty(parse.Errors);
                            command.Options.SetParseResult(parse);
                            Stopwatch elapsed = Stopwatch.StartNew();
                            CompareResult report;
                            try
                            {
                                report = await command.GetResultAsync(TestContext.Current.CancellationToken);
                                if (format == "json") { output.Write(JsonHelper.Serialize(report)); }
                                else { CompareTextRenderer.Write(output, report, 1); }
                                elapsed.Stop();
                            }
                            finally
                            {
                                await sampling.CancelAsync();
                                await sample;
                            }
                            long managed = GC.GetTotalAllocatedBytes(true) - allocated;
                            long retained = Bytes(Path.Combine(fixture.Root, "cache"));
                            Assert.Equal(source == "registry" ? 2 : 1, report.ExitCode);
                            Assert.Equal(0, Bytes(Path.Combine(fixture.Root, "daemon-export")));
                            measurements.Add(new(name, source, format, cache, repeat,
                                elapsed.Elapsed.TotalMilliseconds, rss, managed,
                                Math.Max(peakCache, retained), peakScratch, peakDisk, retained,
                                baseline.Layers.Select(Digest).ToArray(), target.Layers.Select(Digest).ToArray(),
                                baseline.Layers.Sum(static l => l.Length), target.Layers.Sum(static l => l.Length),
                                output.GetStringBuilder().Length, report.Files.Count));
                        }
                    }
                }
            }
        }
        string outputPath = Path.Combine(RepositoryRoot(), "docs", "measurements", "image-compare-v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var budgets = measurements.GroupBy(static m => (m.Fixture, m.Source, m.Format, m.Cache))
            .Select(static g => new Budget(g.Key.Fixture, g.Key.Source, g.Key.Format, g.Key.Cache,
                Ceiling(g.Select(static m => m.ElapsedMs)),
                (long)Ceiling(g.Select(static m => (double)m.PeakWorkingSetBytes)),
                (long)Ceiling(g.Select(static m => (double)m.ManagedAllocatedBytes)),
                (long)Ceiling(g.Select(static m => (double)m.PeakDiskBytes)),
                (long)Ceiling(g.Select(static m => (double)m.RetainedCacheBytes)))).ToArray();
        if (Environment.GetEnvironmentVariable("DREDGE_COMPARE_CHECK_BUDGETS") == "1")
        {
            using JsonDocument previous = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
            var previousBudgets = JsonSerializer.Deserialize<Budget[]>(previous.RootElement.GetProperty("budgets").GetRawText())!;
            foreach (var measurement in measurements)
            {
                Budget budget = previousBudgets.Single(b => b.Fixture == measurement.Fixture && b.Source == measurement.Source &&
                    b.Format == measurement.Format && b.Cache == measurement.Cache);
                Assert.InRange(measurement.ElapsedMs, 0, budget.ElapsedMs);
                Assert.InRange(measurement.PeakWorkingSetBytes, 0, budget.PeakWorkingSetBytes);
                Assert.InRange(measurement.ManagedAllocatedBytes, 0, budget.ManagedAllocatedBytes);
                Assert.InRange(measurement.PeakDiskBytes, 0, budget.PeakDiskBytes);
                Assert.InRange(measurement.RetainedCacheBytes, 0, budget.RetainedCacheBytes);
            }
            return;
        }
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            environment = new { os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), processorCount = Environment.ProcessorCount },
            measurements,
            budgets
        }, new JsonSerializerOptions { WriteIndented = true }), TestContext.Current.CancellationToken);

        static double Ceiling(IEnumerable<double> measurements)
        {
            double[] samples = measurements.ToArray();
            return samples.Max() + (samples.Max() - samples.Min());
        }
    }

    [Fact]
    public async Task FixtureSizeAndSerializationAssertionsDoNotDependOnTiming()
    {
        using var fixture = new SemanticCompareFixture();
        var (baseline, target) = Fixtures("small");
        fixture.Images["base"] = baseline;
        fixture.Images["target"] = target;
        var report = await fixture.CompareAsync("base", "target");
        Assert.True(report.Complete);
        Assert.Equal(16, report.Files.Count);
        Assert.Equal(128 * 256, report.Sizes.Single(static s => s.Name == "effectiveLiveContentBytes").BaseBytes);
        Assert.Equal(0, report.Sizes.Single(static s => s.Name == "hiddenContentBytes").BaseBytes);
        Assert.Equal(16 * 256, report.Sizes.Single(static s => s.Name == "hiddenContentBytes").TargetBytes);
        Assert.Equal("sha256:5c06921bcbcd87572a6b4223381a3a9acb3938cf526f3e55d84b2b1a8058ddc2", Digest(baseline.Layers[0]));
        long start = GC.GetAllocatedBytesForCurrentThread();
        string json = JsonHelper.Serialize(report);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        string baselinePath = Path.Combine(RepositoryRoot(), "docs", "measurements", "image-compare-v1.json");
        if (File.Exists(baselinePath))
        {
            using JsonDocument budgetDocument = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath, TestContext.Current.CancellationToken));
            long budget = budgetDocument.RootElement.GetProperty("budgets").EnumerateArray()
                .Single(static b => b.GetProperty("Fixture").GetString() == "small" &&
                    b.GetProperty("Source").GetString() == "registry" &&
                    b.GetProperty("Format").GetString() == "json" &&
                    b.GetProperty("Cache").GetString() == "warm").GetProperty("ManagedAllocatedBytes").GetInt64();
            Assert.InRange(allocated, json.Length * 2L, budget);
        }
    }

    internal static (Snapshot Base, Snapshot Target) Fixtures(string name)
    {
        int count = name switch { "small" => 128, "dependencies" => 4000, _ => 20000 };
        int width = name == "large" ? 1024 : 256;
        List<TarItem> entries = [];
        for (int i = 0; i < count; i++)
        {
            string path = name == "dependencies" ? $"app/node_modules/pkg{i / 4:D4}/file{i % 4}" : $"data/file{i:D5}";
            entries.Add(new(path, new string((char)('a' + i % 26), width)));
            if (name == "dependencies" && i % 4 == 0)
            {
                entries.Add(new($"app/node_modules/pkg{i / 4:D4}/package.json", $"{{\"name\":\"pkg{i / 4:D4}\",\"version\":\"1.0.0\"}}"));
            }
        }
        if (name == "large")
        {
            for (int i = 0; i < 1000; i++) { entries.Add(new($"aliases/link{i:D4}", $"data/file{i:D5}", TarEntryType.HardLink)); }
        }
        byte[][] baselineLayers = name == "large"
            ? entries.Chunk(3500).Select(static chunk => Layer(chunk)).ToArray()
            : [Layer(entries.ToArray())];
        int changed = count / 8;
        var changes = entries.Where(static e => e.Type == TarEntryType.RegularFile && !e.Path.EndsWith("package.json", StringComparison.Ordinal))
            .Take(changed).Select(e => e with { Content = new string('z', width) }).ToArray();
        byte[][] targetLayers = [.. baselineLayers, Layer(changes)];
        return (Image(baselineLayers), Image(targetLayers));
    }

    private static long Bytes(string path)
    {
        if (!Directory.Exists(path)) { return 0; }
        try
        {
            long bytes = 0;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(file).Length; } catch (FileNotFoundException) { }
            }

            return bytes;
        }
        catch (DirectoryNotFoundException) { return 0; }
    }

    internal static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("The measurement harness must run from a Dredge checkout.");
    }

    private sealed record Budget(string Fixture, string Source, string Format, string Cache,
        double ElapsedMs, long PeakWorkingSetBytes, long ManagedAllocatedBytes, long PeakDiskBytes, long RetainedCacheBytes);

    private sealed record Measurement(
        string Fixture, string Source, string Format, string Cache, int Repeat,
        double ElapsedMs, long PeakWorkingSetBytes, long ManagedAllocatedBytes,
        long PeakCacheBytes, long PeakDaemonScratchBytes, long PeakDiskBytes, long RetainedCacheBytes,
        string[] BaseLayerDigests, string[] TargetLayerDigests,
        long BaseCompressedBytes, long TargetCompressedBytes, int OutputCharacters, int FileDifferences);
}
