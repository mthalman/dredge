using System.Diagnostics;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;

namespace Valleysoft.Dredge.Explorer.Tui;

internal sealed record ExplorerOptions(
    int? Layer, string? Compare, bool Mouse, ClipboardMode Clipboard, KeyMap Keys,
    string ViewerExePath, string ViewerArgs, string? Notice = null, bool ViewerUsesTerminal = true,
    bool PauseAfterViewer = false);

// Runs the explorer for one image: wires the layer indexer to the window,
// analyzes the growing indexed prefix in the background, builds the full
// session once every layer is indexed, and suspends the screen for the viewer.
// Returns when the user quits or picks another platform.
internal sealed class ExplorerApp : IAsyncDisposable
{
    // Sizes are shown in decimal units, so a round decimal limit reads as "256 KB".
    internal const int PreviewLimit = 256_000;

    private readonly object sync = new();
    private readonly IDockerRegistryClient client;
    private readonly ExplorerSource source;
    private readonly LayerStore store;
    private readonly ExplorerOptions options;
    private readonly ExplorerImage img;
    private readonly ExplorerLayerIndexer indexer;
    private readonly ExplorerHost host;
    private readonly CancellationTokenSource cts;
    private readonly double[] progress;
    private readonly List<Action> pending = [];
    private IApplication? app;
    private ExplorerWindow? window;
    private ExplorerInsightsResult? completedInsights;
    private bool analyzing;
    private bool analysisDirty;
    private string? pendingCompare;

    public ExplorerApp(
        IDockerRegistryClient client, IDockerRegistryClientFactory factory, ExplorerSource source,
        LayerStore store, ExplorerOptions options, CancellationToken cancellationToken)
    {
        this.client = client;
        this.source = source;
        this.store = store;
        this.options = options;
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        img = ExplorerImage.FromSource(source);
        progress = new double[img.LayerCount];
        indexer = ExplorerLayerIndexer.Create(client, source, store);
        host = new ExplorerHost(client, factory, source, store, img, indexer, options);
        pendingCompare = options.Compare;
    }

    public ExplorerImage Image => img;
    internal ExplorerHost Host => host;

    public ExplorerExit Run()
    {
        ExplorerState state = Start();
        while (true)
        {
            ExplorerExit exit = RunScreen(state);
            if (exit.Kind != ExplorerExitKind.Viewer || exit.ViewerFile is null)
            {
                cts.Cancel();
                return exit;
            }
            if (RunTerminalViewer(exit.ViewerFile, options.ViewerExePath, options.ViewerArgs,
                options.PauseAfterViewer, Console.In, Console.Out) is string error)
            {
                state.Notice = error;
                state.NoticeIsError = true;
            }
        }
    }

    // Wires the indexer to the model and starts downloading layers.
    internal ExplorerState Start()
    {
        indexer.Progress += (layer, bytes) =>
        {
            long total = img.LayerDownloads[layer];
            Volatile.Write(ref progress[layer], total <= 0 ? 0 : Math.Clamp((double)bytes / total, 0, 1));
        };
        indexer.Started += layer => Post(() => img.States[layer] = ExplorerLayerState.Indexing);
        indexer.Indexed += (layer, index) => Post(() =>
        {
            img.SetIndexed(layer, index.Changes);
            ScheduleAnalysis();
            window?.ImageChanged();
        });
        indexer.Failed += (layer, error) => Post(() =>
        {
            img.States[layer] = ExplorerLayerState.Failed;
            img.Errors[layer] = error.Message;
            window?.ImageChanged();
        });
        indexer.Completed += indexes => _ = Task.Run(() => CreateSessionAsync(indexes));

        int layer = options.Layer ?? Math.Max(0, img.LayerCount - 1);
        indexer.Prioritize(layer);
        indexer.Start(cts.Token);
        return new() { Layer = layer, Focus = FocusPane.Layers, Notice = options.Notice, NoticeIsError = options.Notice is not null };
    }

    private ExplorerExit RunScreen(ExplorerState state)
    {
        using ResponsiveLoop loop = new();
        IApplication application = Application.Create();
        application.Init();
        if (!options.Mouse)
        {
            // The driver always turns on mouse reporting; turn it back off so the
            // terminal keeps its own selection and scrolling.
            application.Mouse.IsMouseDisabled = true;
            application.Driver?.WriteRaw(EscSeqUtils.CSI_DisableMouseEvents);
        }
        ExplorerWindow w = Attach(application, state);
        application.Iteration += (_, _) => ResponsiveLoop.QuietCursor(application);
        try
        {
            application.Run(w);
        }
        finally
        {
            Detach();
            w.Dispose();
            application.Dispose();
        }
        return w.Exit;
    }

    // Creates the window on an initialized application and routes loader
    // updates to it. Headless tests call this with the ANSI driver.
    internal ExplorerWindow Attach(IApplication application, ExplorerState state)
    {
        ExplorerWindow w = new(img, state, host, cts.Token,
            options.ViewerUsesTerminal ? null : OpenWindowedViewer);
        List<Action> queued;
        lock (sync)
        {
            app = application;
            window = w;
            queued = [.. pending];
            pending.Clear();
        }
        foreach (Action action in queued)
        {
            application.Invoke(action);
        }
        application.AddTimeout(TimeSpan.Zero, () =>
        {
            w.SyncFocus();
            StartPendingCompare();
            return false;
        });
        application.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            bool indexed = img.ReconcileIndexes(indexer.Snapshot());
            if (indexed)
            {
                ScheduleAnalysis();
                w.ImageChanged();
            }
            if (!img.Complete && host.Session is { } session &&
                Volatile.Read(ref completedInsights) is { } insights)
            {
                img.SetSession(session, insights);
                w.ImageChanged();
                StartPendingCompare();
            }
            bool changed = false;
            for (int i = 0; i < progress.Length; i++)
            {
                double value = Volatile.Read(ref progress[i]);
                if (img.States[i] != ExplorerLayerState.Ready && value != img.Progress[i])
                {
                    img.Progress[i] = value;
                    changed = true;
                }
            }
            if (changed || (!img.Complete && img.SessionError is null) || state.Compare?.Busy == true)
            {
                w.Tick();
            }
            return true;
        });
        return w;
    }

    internal void Detach()
    {
        lock (sync)
        {
            app = null;
            window = null;
        }
    }

    // Marshals to the UI thread, or holds the action while the viewer owns the screen.
    private void Post(Action action)
    {
        lock (sync)
        {
            if (app is null)
            {
                pending.Add(action);
                return;
            }
            app.Invoke(action);
        }
    }

    private void StartPendingCompare()
    {
        if (pendingCompare is string tag && img.Complete && window is not null)
        {
            pendingCompare = null;
            window.StartCompare(tag);
        }
    }

    private void OpenWindowedViewer(string file)
    {
        _ = Task.Run(() =>
        {
            string? error = RunViewer(file, options.ViewerExePath, options.ViewerArgs);
            if (error is not null)
            {
                Post(() => window?.ViewerFailed(error));
            }
        });
    }

    // Serialized: at most one analysis runs, and a newer prefix reruns it once.
    private void ScheduleAnalysis()
    {
        if (img.Complete)
        {
            return;
        }
        if (analyzing)
        {
            analysisDirty = true;
            return;
        }
        IReadOnlyList<LayerChanges>? prefix = img.IndexedPrefix();
        if (prefix is null || prefix.Count == img.AnalyzedCount)
        {
            return;
        }
        analyzing = true;
        Task.Run(() =>
        {
            ImageAnalysisResult analysis = ImageAnalysis.Analyze(prefix);
            return (analysis, ExplorerInsights.Build(analysis, img.Instructions, img.BaseLayerCount, includePotential: false));
        }).ContinueWith(task => Post(() =>
        {
            analyzing = false;
            if (task.IsCompletedSuccessfully && !img.Complete)
            {
                img.SetAnalysis(task.Result.analysis, task.Result.Item2);
                window?.ImageChanged();
            }
            if (analysisDirty)
            {
                analysisDirty = false;
                ScheduleAnalysis();
            }
        }), TaskScheduler.Default);
    }

    private async Task CreateSessionAsync(IReadOnlyDictionary<int, StoredLayerIndex> indexes)
    {
        try
        {
            ExplorerSession session = await ExplorerSession.CreateAsync(client, source, store, indexes, cts.Token);
            ExplorerInsightsResult insights = ExplorerInsights.Build(
                session.Analysis, img.Instructions, img.BaseLayerCount, includePotential: true);
            host.Session = session;
            Volatile.Write(ref completedInsights, insights);
            Post(() =>
            {
                if (!img.Complete)
                {
                    img.SetSession(session, insights);
                    window?.ImageChanged();
                    StartPendingCompare();
                }
            });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Post(() =>
            {
                img.SessionError = exception.Message;
                window?.ImageChanged();
            });
        }
    }

    // Returns an error to show once the explorer is back, or null on success.
    internal static string? RunTerminalViewer(string file, string exePath, string args, bool pause,
        TextReader input, TextWriter output)
    {
        string? error = RunViewer(file, exePath, args);
        if (pause && error is null)
        {
            output.Write("\nPress Enter to return to the explorer...");
            output.Flush();
            input.ReadLine();
        }
        return error;
    }

    internal static string? RunViewer(string file, string exePath, string args)
    {
        ProcessStartInfo info = new(exePath, args.Replace("{0}", file, StringComparison.Ordinal));
        info.UseShellExecute = false;
        try
        {
            using Process process = Process.Start(info) ??
                throw new InvalidOperationException("The process did not start.");
            process.WaitForExit();
            return process.ExitCode == 0 ? null : $"The viewer '{exePath}' exited with code {process.ExitCode}.";
        }
        catch (Exception exception)
        {
            return $"Could not run the viewer '{exePath}': {exception.Message}";
        }
        finally
        {
            TryDelete(file);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            string? directory = Path.GetDirectoryName(file);
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        await host.DisposeAsync();
        cts.Dispose();
    }
}

// The real host: the registry, the layer store, and the loaded session.
internal sealed class ExplorerHost : IExplorerHost, IAsyncDisposable
{
    private readonly IDockerRegistryClient client;
    private readonly IDockerRegistryClientFactory factory;
    private readonly ExplorerSource source;
    private readonly LayerStore store;
    private readonly ExplorerImage img;
    private readonly ExplorerLayerIndexer indexer;
    private readonly ExplorerOptions options;
    private readonly SemaphoreSlim compareGate = new(1, 1);
    private readonly Dictionary<string, ExplorerSession> targets = new(StringComparer.Ordinal);
    private readonly List<IDockerRegistryClient> ownedClients = [];
    private IReadOnlyList<string>? tags;

    public ExplorerHost(
        IDockerRegistryClient client, IDockerRegistryClientFactory factory, ExplorerSource source,
        LayerStore store, ExplorerImage img, ExplorerLayerIndexer indexer, ExplorerOptions options)
    {
        this.client = client;
        this.factory = factory;
        this.source = source;
        this.store = store;
        this.img = img;
        this.indexer = indexer;
        this.options = options;
    }

    public ExplorerSession? Session { get; set; }
    public KeyMap Keys => options.Keys;
    public bool ClipboardEnabled => options.Clipboard != ClipboardMode.Off;
    public IReadOnlyList<ExplorerPlatform> Platforms => source.Platforms;
    public ExplorerPlatform? Platform => source.Platform;

    private ExplorerSession Loaded => Session ??
        throw new InvalidOperationException("Every layer must be indexed first.");

    private PlatformOptionsBase PlatformOptions => source.Platform is ExplorerPlatform platform
        ? ExplorerSource.ForPlatform(platform)
        : new() { Os = source.Config.Os, Architecture = source.Config.Architecture, OsVersion = source.Config.OsVersion };

    public void Prioritize(int layer) => indexer.Prioritize(layer);

    public void Retry(int layer) => indexer.Retry(layer);

    public async Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken)
    {
        if (tags is not null)
        {
            return tags;
        }
        List<string> result = [];
        Page<RepositoryTags> page = await client.Tags.GetAsync(source.Image.Repo, null, cancellationToken);
        result.AddRange(page.Value.Tags);
        while (page.NextPageLink is not null)
        {
            page = await client.Tags.GetNextAsync(page.NextPageLink, cancellationToken);
            result.AddRange(page.Value.Tags);
        }
        return tags = result.Distinct(StringComparer.Ordinal).ToArray();
    }

    public async Task DescribeTagAsync(TagChoice choice, CancellationToken cancellationToken)
    {
        if (choice.Tag == ExplorerTags.Label(img.Reference))
        {
            choice.Shared = choice.LayerCount = img.LayerCount;
            choice.AdditionalDownload = 0;
            return;
        }
        ImageName name = ImageName.Parse(ExplorerTags.WithTag(img.Reference, choice.Tag));
        (ResolvedManifest resolved, _, _) = await ExplorerSource.ResolveAsync(
            client, name, PlatformOptions, source.Platform, cancellationToken);
        choice.Digest = resolved.ManifestInfo.DockerContentDigest;
        string[] digests = resolved.Manifest.Layers.Select(layer => layer.Digest ?? "").ToArray();
        (choice.Shared, choice.AdditionalDownload) = Describe(img.LayerDigests, digests,
            resolved.Manifest.Layers.Select(layer => layer.Size).ToArray());
        choice.LayerCount = digests.Length;
        choice.Note = TagNote(choice.Digest, img.Digest, choice.Shared.Value, img.BaseLayerCount);
    }

    // Flags tags that are this image, or that can't share its verified base.
    internal static string? TagNote(string? digest, string imageDigest, int shared, int? baseLayerCount) =>
        digest == imageDigest ? "same digest"
        : shared == 0 && baseLayerCount > 0 ? "different base image"
        : null;

    // Shared counts layers equal at the same position; the download counts
    // target layers this image doesn't already have anywhere.
    internal static (int Shared, long Download) Describe(
        IReadOnlyList<string> current, IReadOnlyList<string> target, IReadOnlyList<long> targetSizes)
    {
        int shared = 0;
        while (shared < current.Count && shared < target.Count && current[shared] == target[shared])
        {
            shared++;
        }
        HashSet<string> have = new(current, StringComparer.Ordinal);
        HashSet<string> counted = new(StringComparer.Ordinal);
        long download = 0;
        for (int i = 0; i < target.Count; i++)
        {
            if (!have.Contains(target[i]) && counted.Add(target[i]))
            {
                download += targetSizes[i];
            }
        }
        return (shared, download);
    }

    public async Task<ExplorerComparison> CompareAsync(string tag, CancellationToken cancellationToken)
    {
        ExplorerSession baseline = Loaded;
        await compareGate.WaitAsync(cancellationToken);
        try
        {
            if (!targets.TryGetValue(tag, out ExplorerSession? target))
            {
                ImageName name = ExploreCommand.ResolveCompareImage(source.Image, tag);
                IDockerRegistryClient targetClient = client;
                if (name.Registry != source.Image.Registry)
                {
                    targetClient = await factory.GetClientAsync(name.Registry, cancellationToken);
                    ownedClients.Add(targetClient);
                }
                target = await ExplorerSession.LoadAsync(targetClient, factory, name, PlatformOptions,
                    store, baseImages: null, cancellationToken, exactPlatform: source.Platform);
                targets[tag] = target;
            }
            await baseline.EnsurePackagesAsync(cancellationToken);
            await target.EnsurePackagesAsync(cancellationToken);
            return ExplorerSession.Compare(baseline, target);
        }
        finally
        {
            compareGate.Release();
        }
    }

    public async Task<PreviewContent> PreviewAsync(string path, int layer, CancellationToken cancellationToken)
    {
        ExplorerSession session = Loaded;
        if (!session.Analysis.LiveLayers.TryGetValue(path, out int live))
        {
            return new PreviewContent(path, null, null, "Deleted in the final image, so there's nothing to preview.", 0);
        }
        (List<string>? lines, string? message, long bytes) = await ReadTextAsync(session.Files, path, cancellationToken);
        if (lines is not null && live != layer)
        {
            message = $"Showing the final version from layer {live}.";
        }
        return new PreviewContent(path, LanguageFor(path), lines, message, bytes);
    }

    internal static string? LanguageFor(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json"
        : path.EndsWith("Dockerfile", StringComparison.OrdinalIgnoreCase) ? "dockerfile"
        : null;

    // Reads at most PreviewLimit bytes. Returns null lines for binary content.
    internal static async Task<(List<string>? Lines, string? Message, long Bytes)> ReadTextAsync(
        ImageFileSystem files, string path, CancellationToken cancellationToken)
    {
        using LimitedStream stream = new(ExplorerApp.PreviewLimit);
        try
        {
            await files.CopyFileToAsync(path, stream, cancellationToken);
        }
        catch (Exception) when (stream.Truncated)
        {
            // Stopping the copy at the limit surfaces as an error from the extractor.
        }
        return Decode(stream.ToArray(), stream.Truncated);
    }

    internal static (List<string>? Lines, string? Message, long Bytes) Decode(byte[] data, bool truncated)
    {
        int length = data.Length;
        if (Array.IndexOf(data, (byte)0) >= 0)
        {
            return (null, "Binary file; no preview.", length);
        }
        string text;
        try
        {
            // A truncated read can split a multi-byte character; drop the partial tail.
            int end = length;
            if (truncated)
            {
                while (end > 0 && end > length - 4 && (data[end - 1] & 0xC0) == 0x80)
                {
                    end--;
                }
                if (end > 0 && data[end - 1] >= 0xC0)
                {
                    end--;
                }
            }
            text = new UTF8Encoding(false, true).GetString(data, 0, end);
        }
        catch (DecoderFallbackException)
        {
            return (null, "Not UTF-8 text; no preview.", length);
        }
        List<string> lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return (lines, truncated ? $"Showing the first {Fmt.SizeShort(ExplorerApp.PreviewLimit)}." : null, length);
    }

    public async Task<TextDiffContent> DiffAsync(
        ExplorerComparison comparison, string path, CancellationToken cancellationToken)
    {
        ExplorerFileDifference? difference = comparison.Files.FirstOrDefault(file => file.Path == path);
        if (difference is null)
        {
            return new TextDiffContent(path, null, "This file is the same in both images.");
        }
        async Task<(List<string>? Lines, string? Message)> SideAsync(ExplorerSession session, ImageFileSystemEntry? entry)
        {
            if (entry is null || entry.Type != ImageFileType.File)
            {
                return ([], null);
            }
            (List<string>? lines, string? message, _) = await ReadTextAsync(session.Files, path, cancellationToken);
            return (lines, message);
        }
        (List<string>? before, string? beforeMessage) = await SideAsync(comparison.Baseline, difference.Baseline);
        (List<string>? after, string? afterMessage) = await SideAsync(comparison.Target, difference.Target);
        if (before is null || after is null)
        {
            return new TextDiffContent(path, null, beforeMessage ?? afterMessage);
        }
        IReadOnlyList<DiffLine>? lines = TextDiff.Diff(before, after);
        return lines is null
            ? new TextDiffContent(path, null, "Too many changes to show side by side.")
            : new TextDiffContent(path, lines, beforeMessage ?? afterMessage);
    }

    public async Task<PackageFilesContent> PackageFilesAsync(
        ExplorerComparison comparison, ExplorerPackageDifference package, CancellationToken cancellationToken)
    {
        ExplorerSession side = package.TargetVersion is null ? comparison.Baseline : comparison.Target;
        HashSet<string> all = side.Entries.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> owned = await PackageFileLister.ListAsync(package.Ecosystem, package.Name, all,
            async (path, token) =>
            {
                if (!all.Contains(path))
                {
                    return null;
                }
                using MemoryStream stream = new();
                await side.Files.CopyFileToAsync(path, stream, token);
                return Encoding.UTF8.GetString(stream.ToArray());
            }, cancellationToken);
        if (owned.Count == 0)
        {
            return new PackageFilesContent(package, null, "The package manager doesn't list this package's files.", 0);
        }
        Dictionary<string, ExplorerFileDifference> changed = comparison.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        List<(string Path, Change Change)> files = owned
            .Where(changed.ContainsKey)
            .Select(path => (path, ExplorerImage.ToChange(changed[path].Kind)))
            .ToList();
        return new PackageFilesContent(package, files,
            files.Count == 0 ? "None of its files changed." : null, owned.Count);
    }

    public async Task<string> ExtractAsync(string path, string destination, CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(destination);
        await Loaded.Files.ExtractAsync(path, full, cancellationToken);
        return $"Extracted /{path} to {full}";
    }

    public async Task<string> PrepareForViewerAsync(string path, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "dredge-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, StagedFileName(path));
        try
        {
            await using FileStream stream = File.Create(file);
            await Loaded.Files.CopyFileToAsync(path, stream, cancellationToken);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
        return file;
    }

    private static readonly string[] ReservedWindowsNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    // Image paths can hold characters the host file system rejects (? * : < > |) or that
    // Keep the name recognizable, and its extension for syntax-highlighting viewers,
    // but stage it under safe characters only.
    internal static string StagedFileName(string path)
    {
        const int MaxLength = 100;
        string name = new(path.Split('/')[^1]
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        name = name.TrimEnd('.');
        if (name.Length > MaxLength)
        {
            name = name[^MaxLength..];
        }
        if (name.Length == 0 || ReservedWindowsNames.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase))
        {
            name = "_" + name;
        }
        return name;
    }

    public bool WriteClipboard(string text) => Clipboard.Write(options.Clipboard, text);

    public async ValueTask DisposeAsync()
    {
        foreach (ExplorerSession target in targets.Values)
        {
            await target.Files.DisposeAsync();
        }
        targets.Clear();
        foreach (IDockerRegistryClient owned in ownedClients)
        {
            owned.Dispose();
        }
        if (Session is not null)
        {
            await Session.Files.DisposeAsync();
        }
        compareGate.Dispose();
    }
}

// Keeps the first Limit bytes written and drops the rest.
internal sealed class LimitedStream : Stream
{
    private readonly MemoryStream buffer = new();
    private readonly int limit;

    public LimitedStream(int limit) => this.limit = limit;

    public bool Truncated { get; private set; }
    public byte[] ToArray() => buffer.ToArray();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => buffer.Length;
    public override long Position { get => buffer.Length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] data, int offset, int count) => Write(data.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        int room = (int)Math.Max(0, limit - buffer.Length);
        buffer.Write(data[..Math.Min(room, data.Length)]);
        if (data.Length > room)
        {
            Truncated = true;
            throw new EndOfStreamException("The preview limit was reached.");
        }
    }

    public override Task WriteAsync(byte[] data, int offset, int count, CancellationToken cancellationToken)
    {
        Write(data.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Write(data.Span);
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            buffer.Dispose();
        }
        base.Dispose(disposing);
    }
}
