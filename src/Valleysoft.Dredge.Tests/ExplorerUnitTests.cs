using System.Runtime.InteropServices;
using System.Text;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerClipboardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeClipboardUsesAnOwnedWindowAndReleasesItOnFailure(bool throws)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Validates Windows clipboard owner lifetime without opening the clipboard.");
        IntPtr owner = IntPtr.Zero;
        int attempts = 0;
        bool Open(IntPtr window)
        {
            Assert.NotEqual(IntPtr.Zero, window);
            Assert.NotEqual(0u, GetWindowThreadProcessId(window, out uint processId));
            Assert.Equal((uint)Environment.ProcessId, processId);
            if (owner != IntPtr.Zero)
            {
                Assert.Equal(owner, window);
            }
            owner = window;
            attempts++;
            if (throws)
            {
                throw new InvalidOperationException("Simulated clipboard failure");
            }
            return false;
        }

        if (throws)
        {
            Assert.Equal("Simulated clipboard failure",
                Assert.Throws<InvalidOperationException>(() => Clipboard.TryWriteWindows("command", Open)).Message);
        }
        else
        {
            Assert.False(Clipboard.TryWriteWindows("command", Open));
        }
        Assert.True(attempts > 0);
        Assert.NotEqual(IntPtr.Zero, owner);
        Assert.Equal(0u, GetWindowThreadProcessId(owner, out _));
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}

// The UI tests prove the explorer works without Terminal.Gui's Markdown and
// syntax-highlighting dependencies only if those assemblies are really absent.
public sealed class ExplorerDependencyTests
{
    [Theory]
    [InlineData("Markdig")]
    [InlineData("Onigwrap")]
    [InlineData("TextMateSharp")]
    [InlineData("TextMateSharp.Grammars")]
    public void ExcludedTerminalGuiDependencyIsNotDeployed(string assembly)
    {
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, assembly + ".dll")),
            $"{assembly}.dll is deployed; keep its ExcludeAssets entries in sync with Valleysoft.Dredge.csproj.");
    }
}

public sealed class ExplorerKeyMapTests
{
    [Fact]
    public void DefaultsCoverEveryActionWithDistinctKeys()
    {
        KeyAction[] actions = Enum.GetValues<KeyAction>();
        Assert.Equal(actions.Length, actions.Select(static action => KeyMap.Default[action]).Distinct().Count());
        Assert.Equal(KeyAction.Quit, KeyMap.Default.Lookup('q'));
        Assert.Null(KeyMap.Default.Lookup('Z'));
    }

}

public sealed class ExplorerSettingsTests
{
    [Theory]
    [InlineData(null, false, "Dark")]
    [InlineData("", false, "Dark")]
    [InlineData("dark", false, "Dark")]
    [InlineData("light", false, "Light")]
    [InlineData("light", true, "NoColor")]
    [InlineData("bogus", true, "NoColor")]
    public void ParsesTheme(string? setting, bool noColor, string expected) =>
        Assert.Equal(expected, Theme.Parse(setting, noColor).ToString());

    [Fact]
    public void RejectsUnknownTheme() =>
        Assert.Throws<InvalidOperationException>(static () => Theme.Parse("Dark", false));

    [Fact]
    public void ValidatesMouse()
    {
        ExploreSettings settings = new();
        Assert.True(settings.IsMouseEnabled());

        settings.Mouse = "false";
        Assert.False(settings.IsMouseEnabled());

        settings.Mouse = "yes";
        Assert.Throws<InvalidOperationException>(() => settings.IsMouseEnabled());
    }

    [Theory]
    [InlineData(true, false, ClipboardMode.Native)]
    [InlineData(true, true, ClipboardMode.Osc52)]
    [InlineData(false, false, ClipboardMode.Osc52)]
    [InlineData(false, true, ClipboardMode.Osc52)]
    internal void ClipboardPrefersTheLocalWindowsClipboard(bool windows, bool remote, ClipboardMode expected) =>
        Assert.Equal(expected, Clipboard.Resolve(windows, remote));

    [Theory]
    [InlineData("SSH_CONNECTION", true)]
    [InlineData("SSH_CLIENT", true)]
    [InlineData("SSH_TTY", true)]
    [InlineData("TERM", false)]
    public void SshSessionsCountAsRemote(string variable, bool remote) =>
        Assert.Equal(remote, Clipboard.IsRemoteSession(name => name == variable ? "x" : null));

    [Fact]
    public void SettingsAreAddressableByPath()
    {
        AppSettings settings = new();
        settings.SetProperty(new Queue<string>(["explore", "theme"]), "light");
        Assert.Equal("light", settings.GetProperty(new Queue<string>(["explore", "theme"])));
        Assert.Equal("light", settings.Explore.Theme);
    }
}

public sealed class ExplorerHostTests
{
    [Fact]
    public void DescribeCountsSharedPrefixAndNewLayersOnce()
    {
        (int shared, long download) = ExplorerHost.Describe(
            ["a", "b", "c"], ["a", "b", "d", "d", "c", "e"], [1, 2, 4, 4, 8, 16]);
        Assert.Equal(2, shared);
        Assert.Equal(20, download);
    }

    [Fact]
    public void DecodeDetectsBinaryAndInvalidText()
    {
        Assert.Equal("Binary file; no preview.", ExplorerHost.Decode([0x41, 0x00, 0x42], false).Message);
        Assert.Equal("Not UTF-8 text; no preview.", ExplorerHost.Decode([0xC3, 0x28], false).Message);
    }

    [Fact]
    public void DecodeSplitsLinesAndDropsTrailingNewline()
    {
        (List<string>? lines, string? message, long bytes) = ExplorerHost.Decode(Encoding.UTF8.GetBytes("a\r\nb\n"), false);
        Assert.Equal(["a", "b"], lines);
        Assert.Null(message);
        Assert.Equal(5, bytes);
    }

    [Fact]
    public void DecodeDropsACharacterSplitByTruncation()
    {
        byte[] euro = Encoding.UTF8.GetBytes("ok€");
        (List<string>? lines, string? message, _) = ExplorerHost.Decode(euro[..^1], true);
        Assert.Equal(["ok"], lines);
        Assert.StartsWith("Showing the first", message);
    }

    [Theory]
    [InlineData("app/package.json", "json")]
    [InlineData("src/Dockerfile", "dockerfile")]
    [InlineData("etc/os-release", null)]
    public void PicksPreviewLanguage(string path, string? language) =>
        Assert.Equal(language, ExplorerHost.LanguageFor(path));

    [Fact]
    public void FormatsOsc52() =>
        Assert.Equal("\u001b]52;c;aGk=\a", Clipboard.Osc52("aGk="));

    [Fact]
    public void LimitedStreamKeepsThePrefixAndStops()
    {
        using LimitedStream stream = new(4);
        stream.Write([1, 2, 3]);
        Assert.False(stream.Truncated);
        Assert.Throws<EndOfStreamException>(() => stream.Write([4, 5]));
        Assert.True(stream.Truncated);
        Assert.Equal([1, 2, 3, 4], stream.ToArray());
    }
}

public sealed class ExplorerTagTests
{
    [Fact]
    public void OrdersCurrentTagFirstThenByName() =>
        Assert.Equal(["2.0", "1.0", "latest"], TagPicker.Order(["latest", "2.0", "1.0", "2.0"], "2.0"));

    [Theory]
    [InlineData("registry.test/app:1.0", "1.0")]
    [InlineData("localhost:5000/app", "latest")]
    [InlineData("app@sha256:0123456789abcdef0123", "sha256:0123456789ab")]
    public void LabelsReferences(string reference, string label) =>
        Assert.Equal(label, ExplorerTags.Label(reference));
}

public sealed class TextDiffTests
{
    [Fact]
    public void ReportsInsertionsAndDeletionsWithLineNumbers()
    {
        IReadOnlyList<DiffLine> lines = TextDiff.Diff(["a", "b", "c"], ["a", "x", "c", "d"])!;
        Assert.Equal(
        [
            new DiffLine(DiffOp.Same, 1, 1, "a"),
            new DiffLine(DiffOp.Delete, 2, null, "b"),
            new DiffLine(DiffOp.Insert, null, 2, "x"),
            new DiffLine(DiffOp.Same, 3, 3, "c"),
            new DiffLine(DiffOp.Insert, null, 4, "d"),
        ], lines);
    }

    [Fact]
    public void HandlesEmptySides()
    {
        Assert.Empty(TextDiff.Diff([], [])!);
        Assert.All(TextDiff.Diff([], ["a", "b"])!, static line => Assert.Equal(DiffOp.Insert, line.Op));
        Assert.All(TextDiff.Diff(["a"], [])!, static line => Assert.Equal(DiffOp.Delete, line.Op));
    }

    [Fact]
    public void GivesUpPastTheEditLimit() =>
        Assert.Null(TextDiff.Diff(["a", "b", "c"], ["x", "y", "z"], maxEdits: 3));

    [Theory]
    [InlineData(2000, true)]
    [InlineData(4000, false)]
    public void LargeRewritesUseBoundedWorkingMemory(int count, bool fits)
    {
        string[] before = [.. Enumerable.Range(0, count).Select(static i => $"old-{i}")];
        string[] after = [.. Enumerable.Range(0, count).Select(static i => $"new-{i}")];
        TextDiff.Diff(["warmup"], ["changed"]);
        long start = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<DiffLine>? result = TextDiff.Diff(before, after);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(bytes < 8_000_000, $"Diff allocated {bytes:N0} bytes.");
        Assert.Equal(fits, result is not null);
        if (result is not null)
        {
            Assert.Equal(before, result.Where(static line => line.Op != DiffOp.Insert).Select(static line => line.Text));
            Assert.Equal(after, result.Where(static line => line.Op != DiffOp.Delete).Select(static line => line.Text));
        }
    }

    [Fact]
    public void ReconstructionAcrossTraceBlocksPreservesMinimumEditsAndLineNumbers()
    {
        Random random = new(531);
        for (int sample = 0; sample < 12; sample++)
        {
            string[] before = [.. Enumerable.Range(0, 90).Select(_ => random.Next(5).ToString())];
            string[] after = [.. Enumerable.Range(0, 95).Select(_ => random.Next(5).ToString())];
            int[,] edits = new int[before.Length + 1, after.Length + 1];
            for (int i = 0; i <= before.Length; i++) edits[i, 0] = i;
            for (int j = 0; j <= after.Length; j++) edits[0, j] = j;
            for (int i = 1; i <= before.Length; i++)
            {
                for (int j = 1; j <= after.Length; j++)
                {
                    edits[i, j] = before[i - 1] == after[j - 1]
                        ? edits[i - 1, j - 1] : 1 + Math.Min(edits[i - 1, j], edits[i, j - 1]);
                }
            }
            IReadOnlyList<DiffLine> result = TextDiff.Diff(before, after)!;
            Assert.Equal(edits[before.Length, after.Length], result.Count(static line => line.Op != DiffOp.Same));
            Assert.Equal(before, result.Where(static line => line.Op != DiffOp.Insert).Select(static line => line.Text));
            Assert.Equal(after, result.Where(static line => line.Op != DiffOp.Delete).Select(static line => line.Text));
            Assert.Equal(Enumerable.Range(1, before.Length), result.Where(static line => line.OldLine is not null).Select(static line => line.OldLine!.Value));
            Assert.Equal(Enumerable.Range(1, after.Length), result.Where(static line => line.NewLine is not null).Select(static line => line.NewLine!.Value));
        }
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    public void InsertionsAtCheckpointBoundariesRetainCommonLines(int count)
    {
        string[] after = ["prefix", .. Enumerable.Range(0, count).Select(static i => $"insert-{i}"), "suffix"];
        IReadOnlyList<DiffLine> result = TextDiff.Diff(["prefix", "suffix"], after, maxEdits: count)!;
        Assert.Equal(count, result.Count(static line => line.Op == DiffOp.Insert));
        Assert.Equal(after, result.Select(static line => line.Text));
        Assert.Equal(new(DiffOp.Same, 2, after.Length, "suffix"), result[^1]);
        Assert.Null(TextDiff.Diff(["prefix", "suffix"], after, maxEdits: count - 1));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(10_512)]
    public void CancellationInterruptsMatchingAndReconstruction(int cancelAfterReads)
    {
        using CancellationTokenSource source = new();
        CancelingLines before = new(source, cancelAfterReads);
        string[] after = [.. Enumerable.Repeat("same", before.Count)];
        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(() =>
            TextDiff.Diff(before, after, cancellationToken: source.Token));
        Assert.Equal(source.Token, exception.CancellationToken);
        Assert.True(before.Reads < cancelAfterReads + 512);
    }

    [Fact]
    public void PreCanceledDiffAndRenderingDoNotStartWork()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => TextDiff.Diff([], [], cancellationToken: source.Token));
        Assert.Throws<OperationCanceledException>(() => new TextDiffContent("path", [], null, source.Token));
    }

    [Fact]
    public void CustomEditLimitCannotRemoveMemoryBound()
    {
        string[] before = [.. Enumerable.Repeat("before", 10_000)];
        string[] after = [.. Enumerable.Repeat("after", 10_000)];
        long start = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(TextDiff.Diff(before, after, maxEdits: int.MaxValue));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - start < 8_000_000);
    }

    private sealed class CancelingLines : IReadOnlyList<string>
    {
        private readonly CancellationTokenSource source;
        private readonly int cancelAfterReads;

        public CancelingLines(CancellationTokenSource source, int cancelAfterReads)
        {
            this.source = source;
            this.cancelAfterReads = cancelAfterReads;
        }

        public int Count => 10_000;
        public int Reads { get; private set; }
        public string this[int index]
        {
            get
            {
                if (++Reads == cancelAfterReads) source.Cancel();
                return "same";
            }
        }

        public IEnumerator<string> GetEnumerator() => Enumerable.Repeat("same", Count).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

public sealed class PackageFileListerTests
{
    private static Task<IReadOnlyList<string>> List(
        InstalledPackageEcosystem ecosystem, string name, string[] paths, Dictionary<string, string>? files = null) =>
        PackageFileLister.ListAsync(ecosystem, name, paths,
            (path, _) => Task.FromResult(files?.GetValueOrDefault(path)), CancellationToken.None);

    [Fact]
    public void NpmExcludesNestedPackages()
    {
        IReadOnlyList<string> files = PackageFileLister.ListNpm(
        ["app/node_modules/left-pad", "app/node_modules/other/node_modules/left-pad"],
        [
            "app/node_modules/left-pad/index.js",
            "app/node_modules/left-pad/node_modules/other/index.js",
            "app/node_modules/other/node_modules/left-pad/package.json",
            "app/node_modules/left-padding/index.js",
        ]);
        Assert.Equal(["app/node_modules/left-pad/index.js", "app/node_modules/other/node_modules/left-pad/package.json"], files);
    }

    [Fact]
    public async Task NpmOwnershipRequiresInstallationRootsInsteadOfMetadataReads()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(static () =>
            PackageFileLister.ListAsync(InstalledPackageEcosystem.Npm, "left-pad", [],
                static (_, _) => throw new InvalidOperationException("npm ownership must use cached installation roots."),
                CancellationToken.None));
    }

    [Fact]
    public async Task DpkgReadsTheArchQualifiedList()
    {
        IReadOnlyList<string> files = await List(InstalledPackageEcosystem.Dpkg, "libc6",
            ["var/lib/dpkg/info/libc6:amd64.list", "var/lib/dpkg/info/libc6-dev.list"],
            new() { ["var/lib/dpkg/info/libc6:amd64.list"] = "/.\n/lib\n/lib/libc.so.6\r\n" });
        Assert.Equal(["lib", "lib/libc.so.6"], files);
    }

    [Fact]
    public async Task DpkgUnionsAllArchitecturesWithoutDuplicates()
    {
        Dictionary<string, string> manifests = new()
        {
            ["var/lib/dpkg/info/libfoo:amd64.list"] = "/usr/share/foo\n/usr/lib/x86_64/foo.so\n",
            ["var/lib/dpkg/info/libfoo:i386.list"] = "/usr/share/foo\n/usr/lib/i386/foo.so\n",
            ["var/lib/dpkg/info/libfoobar.list"] = "/unrelated"
        };
        Assert.Equal(["usr/lib/i386/foo.so", "usr/lib/x86_64/foo.so", "usr/share/foo"],
            await List(InstalledPackageEcosystem.Dpkg, "libfoo", [.. manifests.Keys], manifests));
    }

    [Fact]
    public async Task PipUnionsAllEnvironmentRoots()
    {
        Dictionary<string, string> manifests = new()
        {
            ["usr/lib/python3/site-packages/foo-1.dist-info/RECORD"] = "foo/__init__.py,,\n",
            ["opt/venv/lib/python3/site-packages/foo-2.dist-info/RECORD"] = "foo/__init__.py,,\n"
        };
        Assert.Equal(["opt/venv/lib/python3/site-packages/foo/__init__.py", "usr/lib/python3/site-packages/foo/__init__.py"],
            await List(InstalledPackageEcosystem.Pip, "foo", [.. manifests.Keys], manifests));
    }

    [Fact]
    public void ApkListsOnlyTheNamedPackage()
    {
        const string installed = "P:musl\nF:lib\nR:ld-musl.so.1\n\nP:busybox\nF:bin\nR:busybox\nF:etc\nR:motd\n";
        Assert.Equal(["bin/busybox", "etc/motd"], PackageFileLister.ParseApkInstalled(installed, "busybox"));
    }

    [Fact]
    public async Task PipResolvesRecordPathsRelativeToSitePackages()
    {
        const string record = "requests/__init__.py,sha256=x,1\n\"requests/a,b.py\",,\n../../../bin/req,,\n";
        IReadOnlyList<string> files = await List(InstalledPackageEcosystem.Pip, "Requests",
            ["usr/lib/python3/site-packages/requests-2.0.dist-info/RECORD"],
            new() { ["usr/lib/python3/site-packages/requests-2.0.dist-info/RECORD"] = record });
        Assert.Equal(["usr/bin/req", "usr/lib/python3/site-packages/requests/__init__.py", "usr/lib/python3/site-packages/requests/a,b.py"], files);
    }

    [Fact]
    public async Task PipOwnershipUsesCanonicalPackageNames()
    {
        const string record = "site/friendly__bard-1.dist-info/RECORD";
        Assert.Equal(["site/friendly_bard/__init__.py"],
            await List(InstalledPackageEcosystem.Pip, "Friendly-._.Bard", [record],
                new() { [record] = "friendly_bard/__init__.py,,\n" }));
    }
}

public sealed class InstructionTextTests
{
    [Theory]
    [InlineData(null, "(no history)")]
    [InlineData("  ", "(no history)")]
    [InlineData("RUN /bin/sh -c apt-get update # buildkit", "RUN apt-get update")]
    [InlineData("/bin/sh -c #(nop)  CMD [\"bash\"]", "CMD [\"bash\"]")]
    [InlineData("/bin/sh -c #(nop) COPY file:abc in /app ", "COPY file:abc /app")]
    [InlineData("|1 ARG=x /bin/sh -c make   all", "RUN make all")]
    [InlineData("COPY . /app # buildkit", "COPY . /app")]
    public void Normalizes(string? createdBy, string expected) =>
        Assert.Equal(expected, InstructionText.Normalize(createdBy));
}

public sealed class ExplorerInsightsTests
{
    private const long Mb = 1_000_000;

    [Fact]
    public void GroupsLargeDeletionsAndMergesSmallOverwrites()
    {
        ImageAnalysisResult analysis = ImageAnalysis.Analyze(
        [
            Layer(File("app/big.bin", 2 * Mb, "a"), File("app/small", 10, "b")),
            Layer(File("app/small", 12, "c")),
            Layer([], whiteouts: ["app/big.bin"]),
        ]);
        ExplorerInsightsResult result = ExplorerInsights.Build(analysis, ["COPY . /app", "RUN x", "RUN rm"], null);

        Assert.Equal(2 * Mb + 10, result.HiddenBytes);
        ExplorerFinding deleted = Assert.Single(result.Findings, static finding => finding.Kind == ExplorerFindingKind.Deleted);
        Assert.Equal([0, 2], deleted.Layers);
        Assert.Equal(["app/big.bin"], deleted.Roots);
        Assert.Equal("Build in a separate stage", deleted.FixLabel);
        Assert.Equal("deleted in layer 2", deleted.NoteFor(0));
        Assert.Equal("deletes layer 0", deleted.NoteFor(2));
        ExplorerFinding small = Assert.Single(result.Findings, static finding => finding.Title == "Other small overwrites");
        Assert.Equal(10, small.Bytes);
        Assert.Equal(deleted, result.Findings[0]);
    }

    [Fact]
    public void SeparatesBaseImageFindings()
    {
        ImageAnalysisResult analysis = ImageAnalysis.Analyze(
        [
            Layer(File("usr/a", 6 * Mb, "a"), File("usr/b", 3 * Mb, "b"), File("usr/c", 2 * Mb, "c")),
            Layer(File("usr/c", 2 * Mb, "c2")),
            Layer(File("usr/a", 6 * Mb, "a2"), File("usr/b", 3 * Mb, "b2")),
        ]);
        ExplorerInsightsResult result = ExplorerInsights.Build(analysis, ["", "", ""], baseLayerCount: 2);

        ExplorerFinding fromBase = Assert.Single(result.Findings, static finding => finding.Kind == ExplorerFindingKind.FromBase);
        Assert.Equal(2 * Mb, fromBase.Bytes);
        Assert.Equal(fromBase, result.Findings[^1]);
        // Layer 2 replaced 9 MB of base files in one group, above the churn threshold.
        Assert.Single(result.Findings, static finding => finding.Kind == ExplorerFindingKind.BaseReplaced && finding.Bytes == 9 * Mb);
        Assert.Equal(0, result.BaseChurnBytes);
    }

    [Fact]
    public void SmallBaseReplacementsCountAsChurn()
    {
        ImageAnalysisResult analysis = ImageAnalysis.Analyze(
        [
            Layer(File("etc/passwd", 100, "a")),
            Layer(File("etc/passwd", 120, "b")),
        ]);
        ExplorerInsightsResult result = ExplorerInsights.Build(analysis, ["", ""], baseLayerCount: 1);
        Assert.Empty(result.Findings);
        Assert.Equal(100, result.BaseChurnBytes);
    }

    [Fact]
    public void PotentialSavingsAreListedAfterCertainFindings()
    {
        ImageAnalysisResult analysis = ImageAnalysis.Analyze(
        [
            Layer(File("root/.npm/_cacache/x", 50 * Mb, "a"), File("app/data", 2 * Mb, "d")),
            Layer(File("app/data", 2 * Mb, "d")),
        ]);
        ExplorerInsightsResult result = ExplorerInsights.Build(analysis, ["RUN npm ci", "COPY . /app"], null);
        Assert.Equal(ExplorerFindingKind.Identical, result.Findings[0].Kind);
        ExplorerFinding npm = result.Findings[1];
        Assert.Equal(ExplorerFindingKind.Potential, npm.Kind);
        Assert.Equal("npm cache left in the image", npm.Title);
        Assert.Equal(50 * Mb, result.PotentialBytes);
        Assert.Equal("left behind", npm.NoteFor(0));
        Assert.DoesNotContain(ExplorerInsights.Build(analysis, ["", ""], null, includePotential: false).Findings,
            static finding => finding.Kind == ExplorerFindingKind.Potential);
    }

    [Fact]
    public void SummarizesRootsToAtMostThreeDirectories()
    {
        Assert.Equal(["app/a"], ExplorerInsights.SummarizeRoots(["app/a/x", "app/a/y"]));
        Assert.Equal(["a", "b", "c", "d"], ExplorerInsights.SummarizeRoots(["a/1", "b/1", "c/1", "d/1"]));
        Assert.Equal("/a, /b, /c +1 more", ExplorerInsights.FormatWhere(["a", "b", "c", "d"]));
    }

    [Theory]
    [InlineData(999, "999 B")]
    [InlineData(1500, "1.5 KB")]
    [InlineData(1_500_000, "1.5 MB")]
    [InlineData(2_340_000_000, "2.34 GB")]
    public void FormatsSizes(long bytes, string text) => Assert.Equal(text, ExplorerInsights.Size(bytes));

    internal static LayerChanges Layer(params ScannedEntry[] entries) => new(entries, [], []);

    internal static LayerChanges Layer(ScannedEntry[] entries, string[] whiteouts) => new(entries, whiteouts, []);

    internal static ScannedEntry File(string path, long size, string hash) =>
        new(path, ImageFileType.File, 0x1A4, 0, 0, size, DateTime.UnixEpoch, null, 0, 0, 0, hash);
}

public sealed class ExplorerLayerIndexerTests
{
    private static StoredLayerIndex Index(int layer) => new($"sha256:{layer}", 1, new([], [], []));

    [Fact]
    public async Task IndexesEveryLayerAndReportsCompletion()
    {
        ExplorerLayerIndexer indexer = new(3, (layer, _, _) => Task.FromResult(Index(layer)));
        TaskCompletionSource<IReadOnlyDictionary<int, StoredLayerIndex>> done = new();
        indexer.Completed += indexes => done.TrySetResult(indexes);
        indexer.Start(CancellationToken.None);

        IReadOnlyDictionary<int, StoredLayerIndex> indexes = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([0, 1, 2], indexes.Keys.Order());
        Assert.Equal(["sha256:0", "sha256:1", "sha256:2"], indexes.OrderBy(item => item.Key).Select(item => item.Value.Digest));
        Assert.Throws<InvalidOperationException>(() => indexer.Start(CancellationToken.None));
    }

    [Fact]
    public async Task PrioritizedLayerJumpsTheQueue()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> order = [];
        ExplorerLayerIndexer indexer = new(4, async (layer, _, _) =>
        {
            lock (order)
            {
                order.Add(layer);
            }
            if (layer == 0)
            {
                await gate.Task;
            }
            return Index(layer);
        }, concurrency: 1);
        TaskCompletionSource done = new();
        TaskCompletionSource started = new();
        indexer.Completed += _ => done.TrySetResult();
        indexer.Started += layer => started.TrySetResult();
        indexer.Start(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        indexer.Prioritize(3);
        gate.SetResult();

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([0, 3, 1, 2], order);
    }

    [Fact]
    public async Task FailedLayerStaysFailedUntilRetried()
    {
        int attempts = 0;
        ExplorerLayerIndexer indexer = new(1, (layer, _, _) =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException<StoredLayerIndex>(new IOException("network"))
                : Task.FromResult(Index(layer)));
        TaskCompletionSource<Exception> failed = new();
        TaskCompletionSource done = new();
        TaskCompletionSource<StoredLayerIndex> indexed = new();
        indexer.Failed += (_, error) => failed.TrySetResult(error);
        indexer.Completed += _ => done.TrySetResult();
        indexer.Indexed += (_, index) => indexed.TrySetResult(index);
        indexer.Start(CancellationToken.None);

        Assert.Equal("network", (await failed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Message);
        Assert.False(indexed.Task.IsCompleted);

        Assert.True(indexer.Retry(0));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("sha256:0", (await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Digest);
        Assert.Equal(2, attempts);
        Assert.False(indexer.Retry(0));
    }

    [Fact]
    public async Task EmptyImageCompletesImmediately()
    {
        ExplorerLayerIndexer indexer = new(0, (_, _, _) => throw new InvalidOperationException());
        TaskCompletionSource done = new();
        indexer.Completed += _ => done.TrySetResult();
        indexer.Start(CancellationToken.None);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

public sealed class ExplorerImageModelTests
{
    private static readonly DateTime Now = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("app")]
    [InlineData("team/app:stable")]
    [InlineData("localhost:5000/team/app:stable")]
    [InlineData("[::1]:5000/team/app:stable")]
    [InlineData("registry.test/team/app@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void RepositoryLabelExcludesRegistryTagAndDigest(string reference)
    {
        ExplorerImage img = new(reference, null, "", [], [], null);
        Assert.Equal("app", img.RepoName);
    }

    [Fact]
    public void MapsHistoryToLayersSkippingEmptyInstructions()
    {
        LayerHistory[] history =
        [
            new() { CreatedBy = "/bin/sh -c #(nop) ADD file:x in / ", Created = Now.AddDays(-3) },
            new() { CreatedBy = "/bin/sh -c #(nop)  CMD [\"sh\"]", IsEmptyLayer = true },
            new() { CreatedBy = "COPY . /app # buildkit", Created = Now.AddHours(-2) },
        ];
        ExplorerImage img = new("registry.test/group/app:1.0", "linux/amd64", "sha256:m", ["l0", "l1"], [10, 20],
            history, baseImages: [new("base:1", 1)], now: Now);

        Assert.Equal("app", img.RepoName);
        Assert.Equal(["ADD file:x /", "COPY . /app"], img.Instructions);
        Assert.Equal([0, null, 1], img.History.Select(static row => row.Layer));
        Assert.True(img.History[0].IsBase);
        // No base layer follows the CMD, so it is attributed to this image.
        Assert.False(img.History[1].IsBase);
        Assert.False(img.History[2].IsBase);
        Assert.True(img.IsBase(0));
        Assert.Equal(30, img.TotalDownload);
    }

    [Fact]
    public void FallsBackWhenHistoryDoesNotMatchLayers()
    {
        ExplorerImage img = new("app", null, "sha256:m", ["l0", "l1"], [1, 2],
            [new LayerHistory { CreatedBy = "RUN x" }], now: Now);
        Assert.Equal(["Layer 0", "Layer 1"], img.Instructions);
        Assert.All(img.History, static row => Assert.Equal("(no history for this layer)", row.Instruction));
        Assert.Equal("linux", img.Platform);
    }

    [Fact]
    public void BuildsLayerAndWholeTreesFromAnalysis()
    {
        ExplorerImage img = new("app:1", null, "sha256:m", ["l0", "l1"], [1, 1], null, now: Now);
        LayerChanges first = ExplorerInsightsTests.Layer(ExplorerInsightsTests.File("app/a", 5, "a"), ExplorerInsightsTests.File("app/b", 6, "b"));
        LayerChanges second = ExplorerInsightsTests.Layer([ExplorerInsightsTests.File("app/a", 7, "c")], ["app/b"]);
        img.SetIndexed(0, first);
        Assert.Null(img.WholeTree(0));
        Assert.Equal(11, img.LayerSize(0));
        img.SetIndexed(1, second);
        ImageAnalysisResult analysis = ImageAnalysis.Analyze(img.IndexedPrefix()!);
        img.SetAnalysis(analysis, ExplorerInsights.Build(analysis, img.Instructions, null));

        Node app = Assert.Single(img.LayerTree(1));
        Assert.Equal(["a", "b"], app.Children.Select(static child => child.Name));
        Assert.Equal(Change.Modified, app.Children[0].Change);
        Assert.Equal(Change.Removed, app.Children[1].Change);
        Assert.Equal(11, img.TotalReclaimable);

        List<Node> whole = img.WholeTree(1)!;
        Assert.Equal(["a", "b"], Assert.Single(whole).Children.Select(static child => child.Name));
    }

    [Fact]
    public void ReconcilesIndexedLayersWhenAUiNotificationWasMissed()
    {
        ExplorerImage img = new("app:1", null, "sha256:m", ["l0", "l1"], [10, 20],
            null, now: Now);
        LayerChanges first = ExplorerInsightsTests.Layer(ExplorerInsightsTests.File("a", 5, "a"));
        LayerChanges second = ExplorerInsightsTests.Layer(ExplorerInsightsTests.File("b", 6, "b"));
        img.SetIndexed(0, first);
        img.States[1] = ExplorerLayerState.Indexing;
        img.Progress[1] = 1;
        Dictionary<int, StoredLayerIndex> completed = new()
        {
            [0] = new("l0", 10, first),
            [1] = new("l1", 20, second)
        };

        Assert.True(img.ReconcileIndexes(completed));
        Assert.Equal(2, img.ReadyCount);
        Assert.Equal(2, img.IndexedPrefix()!.Count);
        Assert.False(img.ReconcileIndexes(completed));
    }

    [Fact]
    public void PlatformArgumentsIncludeOsVersion() =>
        Assert.Equal("--os windows --arch amd64 --os-version 10.0",
            ExplorerImage.PlatformArgumentsFor(new ExplorerPlatform("windows", "amd64", null, "10.0")));
}
