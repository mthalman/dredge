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
    int? Layer, string? Compare, bool Mouse, ClipboardMode Clipboard,
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

    private readonly Lock sync = new();
    private readonly IDockerRegistryClient? client;
    private readonly ExplorerSource source;
    private readonly LayerStore store;
    private readonly ExplorerOptions options;
    private readonly ExplorerImage img;
    private readonly ExplorerLayerIndexer indexer;
    private readonly ExplorerHost host;
    private readonly CancellationTokenSource cts;
    private readonly double[] progress;
    private readonly List<Action> pending = [];
    private readonly ExplorerOperationLifetime lifetime = new();
    private bool stopping;
    private int attachment;
    private Task? disposal;
    private IApplication? app;
    private ExplorerWindow? window;
    private ExplorerInsightsResult? completedInsights;
    private bool analyzing;
    private bool analysisDirty;
    private string? pendingCompare;

    public ExplorerApp(
        IDockerRegistryClient? client, IDockerRegistryClientFactory factory, ExplorerSource source,
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
        host.ChoosePlatformAsync = ChooseComparisonPlatformAsync;
        pendingCompare = options.Compare;
    }

    public ExplorerImage Image => img;
    internal ExplorerHost Host => host;

    private async Task<ExplorerPlatform?> ChooseComparisonPlatformAsync(
        IReadOnlyList<ExplorerPlatform> platforms, CancellationToken cancellationToken)
    {
        TaskCompletionSource<ExplorerPlatform?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            completion.TrySetCanceled(cancellationToken));
        Post(() =>
        {
            if (completion.Task.IsCompleted)
            {
                return;
            }
            try
            {
                completion.TrySetResult(app is null ? null :
                    PlatformPicker.Show(app, platforms, source.Platform, cancellationToken, comparing: true));
            }
            catch (Exception exception) when (exception is OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return await completion.Task;
    }

    public ExplorerExit Run() => Run(RunScreen, file => RunTerminalViewer(file,
        options.ViewerExePath, options.ViewerArgs, options.PauseAfterViewer, Console.In, Console.Out));

    internal ExplorerExit Run(Func<ExplorerState, ExplorerExit> runScreen, Func<string, string?> runViewer)
    {
        ExplorerState state = Start();
        while (true)
        {
            cts.Token.ThrowIfCancellationRequested();
            ExplorerExit exit = runScreen(state);
            using StagedViewerFile? staged = exit.ViewerFile is null ? null : new(exit.ViewerFile);
            cts.Token.ThrowIfCancellationRequested();
            if (exit.Kind != ExplorerExitKind.Viewer || exit.ViewerFile is null)
            {
                cts.Cancel();
                return exit;
            }
            if (runViewer(exit.ViewerFile) is string error)
            {
                state.Notice = error;
                state.NoticeIsError = true;
            }
        }
    }

    internal ExplorerState Start()
    {
        indexer.Progress += (layer, bytes) =>
        {
            if (Volatile.Read(ref stopping))
            {
                return;
            }
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
        indexer.Completed += indexes => RunBackground(token => CreateSessionAsync(indexes, token));

        int layer = options.Layer ?? Math.Max(0, img.LayerCount - 1);
        indexer.Prioritize(layer);
        indexer.Start(cts.Token);
        return new(img.Semantics.Comparer) { Layer = layer, Focus = FocusPane.Layers, Notice = options.Notice, NoticeIsError = options.Notice is not null };
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
        int generation;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            app = application;
            window = w;
            generation = ++attachment;
        }
        application.Invoke(() => DrainPending(application, generation));
        application.AddTimeout(TimeSpan.Zero, () => WhileAttached(application, generation, () =>
        {
            w.SyncFocus();
            StartPendingCompare();
            return false;
        }));
        application.AddTimeout(TimeSpan.FromMilliseconds(100), () => WhileAttached(application, generation, () =>
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
        }));
        return w;
    }

    internal void Detach()
    {
        lock (sync)
        {
            app = null;
            window = null;
            attachment++;
        }
    }

    // Marshals to the UI thread, or holds the action while the viewer owns the screen.
    private void Post(Action action)
    {
        IApplication? application;
        int generation;
        lock (sync)
        {
            if (stopping)
            {
                return;
            }
            pending.Add(action);
            application = app;
            generation = attachment;
        }
        // Invoke takes the timer lock; timer callbacks acquire sync in WhileAttached.
        if (application is not null)
        {
            try
            {
                application.Invoke(() => DrainPending(application, generation));
            }
            catch (ObjectDisposedException) when (!WhileAttached(application, generation, () => true))
            {
            }
        }
    }

    private void DrainPending(IApplication application, int generation)
    {
        WhileAttached(application, generation, () =>
        {
            Action[] actions = [.. pending];
            pending.Clear();
            foreach (Action action in actions)
            {
                action();
            }
            return true;
        });
    }

    private bool WhileAttached(IApplication application, int generation, Func<bool> action)
    {
        lock (sync)
        {
            return !stopping && ReferenceEquals(app, application) && attachment == generation && action();
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
        // Windowed viewers own only their staged file and may outlive the explorer.
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
        RunBackground(token =>
        {
            token.ThrowIfCancellationRequested();
            ImageAnalysisResult analysis = ImageAnalysis.Analyze(prefix, img.Semantics);
            ExplorerInsightsResult insights = ExplorerInsights.Build(analysis, img.Instructions, img.BaseLayerCount,
                includePotential: false, semantics: img.Semantics);
            token.ThrowIfCancellationRequested();
            Post(() =>
            {
                analyzing = false;
                if (!img.Complete)
                {
                    img.SetAnalysis(analysis, insights);
                    window?.ImageChanged();
                }
                if (analysisDirty)
                {
                    analysisDirty = false;
                    ScheduleAnalysis();
                }
            });
            return Task.CompletedTask;
        });
    }

    private void RunBackground(Func<CancellationToken, Task> action)
    {
        lock (sync)
        {
            if (stopping || cts.IsCancellationRequested)
            {
                return;
            }
            ExplorerOperationLifetime.Operation operation = lifetime.Enter(cts.Token);
            _ = Task.Run(async () =>
            {
                using (operation)
                {
                    try
                    {
                        await action(operation.Token);
                    }
                    catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        Post(() =>
                        {
                            analyzing = false;
                            img.SessionError = exception.Message;
                            window?.ImageChanged();
                        });
                    }
                }
            });
        }
    }

    private async Task CreateSessionAsync(IReadOnlyDictionary<int, StoredLayerIndex> indexes, CancellationToken token)
    {
        try
        {
            ExplorerSession session = await ExplorerSession.CreateAsync(client, source, store, indexes, token);
            ExplorerInsightsResult insights = ExplorerInsights.Build(
                session.Analysis, img.Instructions, img.BaseLayerCount, semantics: img.Semantics);
            lock (sync)
            {
                if (stopping || token.IsCancellationRequested)
                {
                    insights = ExplorerInsightsResult.Empty;
                }
                else
                {
                    host.Session = session;
                    Volatile.Write(ref completedInsights, insights);
                }
            }
            if (!ReferenceEquals(host.Session, session))
            {
                await session.Files.DisposeAsync();
                return;
            }
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
        catch (OperationCanceledException) when (token.IsCancellationRequested)
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
        ProcessStartInfo info = new(exePath, args.Replace("{0}", file, StringComparison.Ordinal))
        {
            UseShellExecute = false
        };
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

    internal static void TryDelete(string file)
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

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            stopping = true;
            app = null;
            window = null;
            pending.Clear();
            attachment++;
            return new(disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task indexDrain = indexer.StopAsync();
        Task backgroundDrain = lifetime.DisposeAsync().AsTask();
        try
        {
            await cts.CancelAsync();
        }
        finally
        {
            try
            {
                await Task.WhenAll(indexDrain, backgroundDrain);
            }
            finally
            {
                try
                {
                    await host.DisposeAsync();
                }
                finally
                {
                    cts.Dispose();
                }
            }
        }
    }
}

internal sealed class StagedViewerFile(string path, Action<string>? cleanup = null) : IDisposable
{
    private int owned = 1;
    private readonly Action<string> cleanup = cleanup ?? ExplorerApp.TryDelete;

    public string? TryTake() => Interlocked.Exchange(ref owned, 0) == 1 ? path : null;

    public void Dispose()
    {
        if (TryTake() is string file)
        {
            cleanup(file);
        }
    }
}

// Snapshot ownership outlives every callback: an immutable ID alone does not
// keep the daemon's exported content available.
internal sealed class ExplorerHost : IExplorerHost, IAsyncDisposable
{
    internal const long MaxPackageOwnershipBytes = 64 * 1024 * 1024;
    private readonly IDockerRegistryClient? client;
    private readonly IDockerRegistryClientFactory factory;
    private readonly ExplorerSource source;
    private readonly LayerStore store;
    private readonly ExplorerImage img;
    private readonly ExplorerLayerIndexer indexer;
    private readonly ExplorerOptions options;
    private readonly SemaphoreSlim compareGate = new(1, 1);
    private readonly ExplorerOperationLifetime lifetime = new();
    private readonly Lock disposalSync = new();
    private Task? disposal;
    private readonly string viewerRoot;
    private readonly Dictionary<string, (ResolvedManifest Resolved, ExplorerPlatform? Platform, string? DaemonReference)>
        targetSnapshots = new(StringComparer.Ordinal);
    private readonly ExplorerTargetRetention<ExplorerSession> comparisonTargets = new();
    private IReadOnlyList<string>? tags;

    public ExplorerHost(
        IDockerRegistryClient? client, IDockerRegistryClientFactory factory, ExplorerSource source,
        LayerStore store, ExplorerImage img, ExplorerLayerIndexer indexer, ExplorerOptions options,
        string? viewerRoot = null)
    {
        this.client = client;
        this.factory = factory;
        this.source = source;
        this.store = store;
        this.img = img;
        this.indexer = indexer;
        this.options = options;
        this.viewerRoot = viewerRoot ?? Path.GetTempPath();
    }

    public ExplorerSession? Session { get; set; }
    internal Func<IReadOnlyList<ExplorerPlatform>, CancellationToken, Task<ExplorerPlatform?>>? ChoosePlatformAsync { get; set; }
    public bool ClipboardEnabled => options.Clipboard != ClipboardMode.Off;

    private ExplorerSession Loaded => Session ??
        throw new InvalidOperationException("Every layer must be indexed first.");

    private PlatformOptionsBase PlatformOptions => ExplorerSource.ForPlatform(
        ExplorerPlatform.FromConfig(source.Config, source.Platform), source.SourceOptions);

    public void Prioritize(int layer)
    {
        using var operation = lifetime.Enter(CancellationToken.None);
        indexer.Prioritize(layer);
    }

    public void Retry(int layer)
    {
        using var operation = lifetime.Enter(CancellationToken.None);
        indexer.Retry(layer);
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken)
    {
        using var operation = lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        if (source.IsDaemon)
        {
            throw new NotSupportedException(
                "Registry tag browsing is unavailable for Docker daemon images. Enter a docker:// or registry:// comparison reference instead.");
        }
        if (tags is not null)
        {
            return tags;
        }
        List<string> result = [];
        IDockerRegistryClient registry = client ?? throw new InvalidOperationException("Registry client is unavailable.");
        Page<RepositoryTags> page = await registry.Tags.GetAsync(source.Image.Repo, null, cancellationToken);
        result.AddRange(page.Value.Tags);
        while (page.NextPageLink is not null)
        {
            page = await registry.Tags.GetNextAsync(page.NextPageLink, cancellationToken);
            result.AddRange(page.Value.Tags);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return tags = [.. result.Distinct(StringComparer.Ordinal)];
    }

    public async Task DescribeTagAsync(TagChoice choice, CancellationToken cancellationToken)
    {
        using var operation = lifetime.Enter(cancellationToken);
        if (source.IsDaemon)
        {
            throw new NotSupportedException("Registry tag descriptions are unavailable for Docker daemon images.");
        }
        cancellationToken = operation.Token;
        ImageName name = ExploreCommand.ResolveCompareImage(source.Image, choice.Tag);
        await compareGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedManifest resolved = (await GetTargetSnapshotAsync(name, cancellationToken)).Resolved;
            cancellationToken.ThrowIfCancellationRequested();
            choice.Digest = resolved.ManifestInfo.DockerContentDigest;
            string[] digests = [.. resolved.Manifest.Layers.Select(static layer => layer.Digest ?? "")];
            (choice.Shared, choice.AdditionalDownload) = Describe(img.LayerDigests, digests,
                [.. resolved.Manifest.Layers.Select(static layer => layer.Size)]);
            choice.LayerCount = digests.Length;
            choice.Note = TagNote(choice.Digest, img.Digest, choice.Shared.Value, img.BaseLayerCount);
            choice.Note = string.Join("; ", new[] { choice.Note, "cached session snapshot; reopen explorer to refresh" }
                .OfType<string>());
        }
        finally
        {
            compareGate.Release();
        }
    }

    private async Task<(ResolvedManifest Resolved, ExplorerPlatform? Platform)>
        GetTargetSnapshotAsync(ImageName name, CancellationToken cancellationToken)
    {
        string key = name.ToString();
        if (!targetSnapshots.TryGetValue(key, out var snapshot))
        {
            IDockerRegistryClient? targetClient = client;
            if (targetClient is null || name.Registry != source.Image.Registry)
            {
                targetClient = await factory.GetClientAsync(name.Registry, cancellationToken);
            }
            try
            {
                (ResolvedManifest resolved, _, ExplorerPlatform? platform) = await ExplorerSource.ResolveAsync(
                    targetClient, name, PlatformOptions, source.Platform, cancellationToken,
                    comparing: true);
                cancellationToken.ThrowIfCancellationRequested();
                snapshot = (resolved, platform, null);
                targetSnapshots.Add(key, snapshot);
            }
            finally
            {
                if (!ReferenceEquals(targetClient, client))
                {
                    targetClient.Dispose();
                }
            }
        }
        return (snapshot.Resolved, snapshot.Platform);
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

    public async Task<ExplorerComparison> CompareAsync(string tag, Action readingPackages, CancellationToken cancellationToken)
    {
        using RetainedExplorerComparison result = await CompareRetainedAsync(tag, readingPackages, cancellationToken);
        DisplayComparison(result.Comparison);
        return result.Comparison;
    }

    internal void DisplayComparison(ExplorerComparison? comparison) =>
        comparisonTargets.Display(comparison is null ? [] : [comparison.Baseline, comparison.Target]);

    internal IDisposable RetainComparison(ExplorerComparison comparison) =>
        comparisonTargets.Retain([comparison.Baseline, comparison.Target]);

    internal async Task<RetainedExplorerComparison> CompareRetainedAsync(
        string tag, Action readingPackages, CancellationToken cancellationToken)
    {
        using var operation = lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        ExplorerSession baseline = Loaded;
        string reference = ExploreCommand.ResolveCompareReference(source.Reference, tag);
        ImageSourceReference parsed = ImageSourceReference.Parse(reference);
        ImageName? name = parsed.IsDaemon ? null : ImageName.Parse(parsed.Value);
        string key = reference;
        await compareGate.WaitAsync(cancellationToken);
        IDisposable? retention = null;
        try
        {
            await comparisonTargets.CheckCleanupAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (!comparisonTargets.TryGet(key, out ExplorerSession? target))
            {
                LayerStore targetStore = store.CreateScope();
                ExplorerSource? targetSource = null;
                try
                {
                    ResolvedManifest? resolved = null;
                    ExplorerPlatform? platform = source.Platform;
                    string snapshotKey = name?.ToString() ?? reference;
                    string? daemonSnapshotReference = null;
                    if (targetSnapshots.TryGetValue(snapshotKey, out var snapshot))
                    {
                        resolved = snapshot.Resolved;
                        platform = snapshot.Platform ?? platform;
                        daemonSnapshotReference = snapshot.DaemonReference;
                    }
                    targetSource = await ExplorerSource.OpenAsync(
                        source.SourceFactory ?? new ImageSourceFactory(factory), factory,
                        reference, PlatformOptions, null, cancellationToken, exactPlatform: platform,
                        store: targetStore, resolvedManifest: resolved,
                        registryClient: name?.Registry == source.Image.Registry ? client : null,
                        daemonSnapshotReference: daemonSnapshotReference, comparing: true,
                        chooseComparisonPlatform: ChoosePlatformAsync);
                    targetSnapshots.TryAdd(snapshotKey, (targetSource.Resolved, targetSource.Platform,
                        targetSource.IsDaemon ? $"docker://{targetSource.Source!.Identity}" : null));
                    target = await ExplorerSession.CreateAsync(targetSource.RegistryClient, targetSource,
                        targetStore, null, cancellationToken);
                }
                catch
                {
                    try
                    {
                        if (targetSource is not null)
                        {
                            await targetSource.DisposeAsync();
                        }
                    }
                    finally
                    {
                        await targetStore.DisposeAsync();
                    }
                    throw;
                }
                ExplorerSession ownedTarget = target;
                retention = comparisonTargets.Add(key, target, async () =>
                {
                    try
                    {
                        await ownedTarget.Files.DisposeAsync();
                    }
                    finally
                    {
                        try
                        {
                            await targetSource.DisposeAsync();
                        }
                        finally
                        {
                            await targetStore.DisposeAsync();
                        }
                    }
                });
            }
            retention ??= comparisonTargets.Retain([baseline, target]);
            cancellationToken.ThrowIfCancellationRequested();
            if (baseline.IsDaemon || target.IsDaemon)
            {
                await baseline.EnsureContentIdentitiesAsync(cancellationToken);
                await target.EnsureContentIdentitiesAsync(cancellationToken);
            }
            readingPackages();
            await baseline.EnsurePackagesAsync(cancellationToken);
            await target.EnsurePackagesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RetainedExplorerComparison result = new(ExplorerSession.Compare(baseline, target), retention);
            retention = null;
            return result;
        }
        finally
        {
            retention?.Dispose();
            compareGate.Release();
        }
    }

    public async Task<PreviewContent> PreviewAsync(string path, int layer, CancellationToken cancellationToken)
    {
        using var operation = lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        ExplorerSession session = Loaded;
        if (!session.Analysis.LiveLayers.TryGetValue(path, out int live))
        {
            return new PreviewContent(path, null, null, "Deleted in the final image, so there's nothing to preview.", 0);
        }
        (List<string>? lines, string? message, long bytes) = await ReadTextAsync(session.Files, path, cancellationToken);
        if (lines is not null && live != layer)
        {
            message = string.Join(" ", new[] { message, $"Showing the final version from layer {live}." }.OfType<string>());
        }
        return new PreviewContent(path, LanguageFor(path), lines, message, bytes);
    }

    public async Task<InstalledPackageMetadata> PackagesAsync(int layer, CancellationToken cancellationToken)
    {
        using var operation = lifetime.Enter(cancellationToken);
        ExplorerSession session = Loaded;
        return await session.GetPackagesAsync(session.Resolved.Manifest.Layers.Length == 0 ? -1 : layer, operation.Token);
    }

    public async Task<PreviewContent> PreviewVersionAsync(string path, int layer, CancellationToken cancellationToken)
    {
        using var operation = lifetime.Enter(cancellationToken);
        await using ImageFileSystem snapshot = Loaded.Files.CreateLayerSnapshot(layer, operation.Token);
        ImageFileSystemEntry? entry = snapshot.FindEntry(path);
        if (entry is null)
        {
            return new(path, null, [], $"Path absent after layer {layer}.", 0);
        }
        if (entry.Type == ImageFileType.Directory)
        {
            return new(path, null, [], "Directory; no text content.", 0, entry);
        }
        if (entry.Type == ImageFileType.SymbolicLink || entry.ContentLinkTarget is not null)
        {
            return new(path, null, [entry.ContentLinkTarget ?? entry.LinkTarget ??
                throw new InvalidDataException($"Link '/{path}' has no target.")],
                "Symbolic link target, not the referenced file.", entry.Size, entry);
        }
        if (entry.Type is not (ImageFileType.File or ImageFileType.HardLink))
        {
            return new(path, null, null, $"No text preview for file type '{entry.Type}'.", entry.Size, entry);
        }
        (List<string>? lines, string? message, long bytes) = await ReadTextAsync(snapshot, path, operation.Token);
        return new(path, LanguageFor(path), lines, message, bytes, entry);
    }

    public async Task<TextDiffContent> DiffVersionAsync(
        string path, int previousLayer, int layer, CancellationToken cancellationToken)
    {
        if (previousLayer < -1 || previousLayer >= layer)
        {
            throw new ArgumentOutOfRangeException(nameof(previousLayer));
        }
        using var operation = lifetime.Enter(cancellationToken);
        PreviewContent before = previousLayer < 0 ? new(path, null, [], "Before the first version.", 0)
            : await PreviewVersionAsync(path, previousLayer, operation.Token);
        PreviewContent after = await PreviewVersionAsync(path, layer, operation.Token);
        List<string> messages = [];
        if (before.Message is not null) messages.Add("Before: " + before.Message);
        if (after.Message is not null) messages.Add("After: " + after.Message);
        string? metadata = MetadataDifference(before.Entry, after.Entry, Loaded.Semantics);
        if (metadata is not null) messages.Add(metadata);
        IReadOnlyList<DiffLine>? lines = before.Lines is null || after.Lines is null ? null
            : TextDiff.Diff(before.Lines, after.Lines, cancellationToken: operation.Token);
        if (lines is null && before.Lines is not null && after.Lines is not null)
        {
            messages.Add("Too many changes to show.");
        }
        else if (lines is not null && lines.All(static line => line.Op == DiffOp.Same))
        {
            messages.Add(before.Entry?.Size > before.Bytes || after.Entry?.Size > after.Bytes
                ? "No differences in the displayed prefix."
                : "No text differences.");
        }
        return new(path, lines, messages.Count == 0 ? null : string.Join(" ", messages), operation.Token);
    }

    private static string? MetadataDifference(
        ImageFileSystemEntry? before, ImageFileSystemEntry? after, ImageFileSystemSemantics semantics)
    {
        bool platformMetadataChanged = semantics.IsWindows ? before?.Windows != after?.Windows
            : before?.Mode != after?.Mode || before?.UserId != after?.UserId || before?.GroupId != after?.GroupId;
        if (before?.Type == after?.Type && !platformMetadataChanged && before?.LinkTarget == after?.LinkTarget)
        {
            return null;
        }
        string Metadata(ImageFileSystemEntry? entry) => entry is null ? "missing"
            : $"{entry.Type}, " + (semantics.IsWindows
                ? $"attributes {(entry.Windows?.Attributes is uint attributes ? $"0x{attributes:X}" : "unknown")}, " +
                    $"metadata fingerprint {entry.Windows?.Fingerprint ?? "unknown"}, " +
                    $"alternate streams {(entry.Windows?.HasAlternateStreams == true ? "present" : "absent")}"
                : $"mode {Convert.ToString(entry.Mode, 8)}, {entry.UserId}:{entry.GroupId}") +
                (entry.LinkTarget is null ? "" : $", target {entry.LinkTarget}");
        return $"Metadata: {Metadata(before)} -> {Metadata(after)}.";
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
        catch (Exception exception) when (stream.Truncated && exception is not OperationCanceledException)
        {
            // Stopping the copy at the limit surfaces as an error from the extractor.
        }
        return Decode(stream.ToArray(), stream.Truncated);
    }

    internal static (List<string>? Lines, string? Message, long Bytes) Decode(byte[] data, bool truncated)
    {
        int length = data.Length;
        bool utf16 = length >= 2 && (data[0] == 0xff && data[1] == 0xfe || data[0] == 0xfe && data[1] == 0xff);
        if (!utf16 && Array.IndexOf(data, (byte)0) >= 0)
        {
            return (null, "Binary file; no preview.", length);
        }
        string text;
        try
        {
            // A truncated read can split a multi-byte character; drop the partial tail.
            if (utf16)
            {
                int count = length - 2;
                if (truncated)
                {
                    count &= ~1;
                    if (count >= 2)
                    {
                        int last = data[0] == 0xff
                            ? data[count] | data[count + 1] << 8 : data[count] << 8 | data[count + 1];
                        if (char.IsHighSurrogate((char)last))
                        {
                            count -= 2;
                        }
                    }
                }
                text = new UnicodeEncoding(data[0] == 0xfe, false, true).GetString(data, 2, count);
                if (text.Contains('\0'))
                {
                    return (null, "Binary file; no preview.", length);
                }
            }
            else
            {
                int end = truncated ? CompleteUtf8PrefixLength(data) : length;
                int start = end >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf ? 3 : 0;
                text = new UTF8Encoding(false, true).GetString(data, start, end - start);
            }
        }
        catch (DecoderFallbackException)
        {
            return (null, utf16 ? "Invalid UTF-16 text; no preview." : "Not UTF-8 text; no preview.", length);
        }
        List<string> lines = [.. text.Replace("\r\n", "\n").Split('\n')];
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return (lines, truncated ? $"Showing the first {Fmt.SizeShort(ExplorerApp.PreviewLimit)}." : null, length);
    }

    internal static int CompleteUtf8PrefixLength(ReadOnlySpan<byte> data)
    {
        int length = data.Length;
        if (length == 0)
        {
            return 0;
        }

        int lead = length - 1;
        while (lead > 0 && lead > length - 4 && IsUtf8Continuation(data[lead]))
        {
            lead--;
        }

        int expected = Utf8SequenceLength(data[lead]);
        return expected > 1 && length - lead < expected ? lead : length;
    }

    private static bool IsUtf8Continuation(byte value) => (value & 0xC0) == 0x80;

    private static int Utf8SequenceLength(byte lead) =>
        lead switch
        {
            >= 0xC2 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 1
        };

    public async Task<TextDiffContent> DiffAsync(
        ExplorerComparison comparison, string path, CancellationToken cancellationToken)
    {
        using var operation = lifetime.Enter(cancellationToken);
        using IDisposable retention = RetainComparison(comparison);
        cancellationToken = operation.Token;
        ExplorerFileDifference? difference = comparison.Files.FirstOrDefault(file =>
            comparison.Baseline.Semantics.Comparer.Equals(file.Path, path));
        if (difference is null)
        {
            return new TextDiffContent(path, null, "This file is the same in both images.");
        }
        async Task<(List<string>? Lines, string? Message)> SideAsync(ExplorerSession session, ImageFileSystemEntry? entry)
        {
            if (entry is null || entry.Type == ImageFileType.Directory)
            {
                return ([], null);
            }
            if (entry.Type == ImageFileType.SymbolicLink)
            {
                return ([entry.LinkTarget ?? throw new InvalidDataException($"Link '/{path}' has no target.")],
                    "Symbolic link target.");
            }
            if (entry.Type == ImageFileType.HardLink && entry.ContentLinkTarget is string linkTarget)
            {
                return ([linkTarget], "Symbolic link target captured by a hard link.");
            }
            if (entry.Type is not (ImageFileType.File or ImageFileType.HardLink))
            {
                return (null, $"No text preview for file type '{entry.Type}'.");
            }
            (List<string>? lines, string? message, _) = await ReadTextAsync(session.Files, path, cancellationToken);
            return (lines, message);
        }
        (List<string>? before, string? beforeMessage) = await SideAsync(comparison.Baseline, difference.Baseline);
        (List<string>? after, string? afterMessage) = await SideAsync(comparison.Target, difference.Target);
        List<string> messages = [];
        string? metadata = MetadataDifference(difference.Baseline, difference.Target, comparison.Baseline.Semantics);
        if (metadata is not null) messages.Add(metadata);
        if (beforeMessage is not null)
        {
            messages.Add("Baseline: " + beforeMessage);
        }
        if (afterMessage is not null)
        {
            messages.Add("Target: " + afterMessage);
        }
        string? message = messages.Count > 0 ? string.Join(" ", messages) : null;
        if (before is null || after is null)
        {
            return new TextDiffContent(path, null, message);
        }
        IReadOnlyList<DiffLine>? lines = TextDiff.Diff(before, after, cancellationToken: cancellationToken);
        return lines is null
            ? new TextDiffContent(path, null, "Too many changes to display a file diff.")
            : new TextDiffContent(path, lines, message, cancellationToken);
    }

    public async Task<PackageFilesContent> PackageFilesAsync(
        ExplorerComparison comparison, ExplorerPackageDifference package, CancellationToken cancellationToken)
    {
        using var operation = lifetime.Enter(cancellationToken);
        using IDisposable retention = RetainComparison(comparison);
        cancellationToken = operation.Token;
        HashSet<string> owned = new(StringComparer.Ordinal);
        List<string> warnings = [];
        foreach (ExplorerSession side in new[]
        {
            package.BaselineVersion is null ? null : comparison.Baseline,
            package.TargetVersion is null ? null : comparison.Target
        }.OfType<ExplorerSession>())
        {
            string label = ReferenceEquals(side, comparison.Baseline) ? "Baseline" : "Target";
            try
            {
                string[] allPaths = [.. side.Entries.Select(entry => entry.Path)];
                IReadOnlyList<string> paths = package.Ecosystem == InstalledPackageEcosystem.Npm
                    ? PackageFileLister.ListNpm(side.Packages.NpmPackageRoots.GetValueOrDefault(package.Name) ?? [], allPaths)
                    : await PackageFileLister.ListAsync(package.Ecosystem, package.Name, allPaths,
                    async (path, token) =>
                    {
                        await foreach (var result in side.Files.ReadFilesAsync([(path, MaxPackageOwnershipBytes)], token))
                        {
                            if (result.Error is not null)
                            {
                                throw result.Error;
                            }
                            return new UTF8Encoding(false, true).GetString(result.Content!);
                        }
                        return null;
                    }, cancellationToken,
                    (path, error) => warnings.Add($"{label} /{path}: {error.Message}"));
                foreach (string path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        owned.Add(side.Files.ResolveParentComponents(ImagePath.NormalizeRequested(path)));
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException)
                    {
                        warnings.Add($"{label} /{path}: {exception.Message}");
                    }
                }
            }
            catch (NotSupportedException exception)
            {
                warnings.Add($"{label}: {exception.Message}");
            }
        }
        if (owned.Count == 0)
        {
            return new PackageFilesContent(package, null, warnings.Count == 0
                ? "The package manager doesn't list this package's files."
                : "Package file ownership is unavailable.", 0, warnings);
        }
        Dictionary<string, ExplorerFileDifference> changed = comparison.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        List<(string Path, Change Change)> files = [.. owned
            .Where(changed.ContainsKey)
            .Order(StringComparer.Ordinal)
            .Select(path => (path, ExplorerImage.ToChange(changed[path].Kind)))];
        return new PackageFilesContent(package, files,
            warnings.Count > 0 ? "Ownership is incomplete; showing changed files from readable metadata."
                : files.Count == 0 ? "None of its files changed." : null, owned.Count, warnings);
    }

    public async Task<string> ExtractAsync(string path, string destination, CancellationToken cancellationToken)
    {
        using var operation = lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        string full = Path.GetFullPath(destination);
        await Loaded.Files.ExtractAsync(path, full, cancellationToken);
        return $"Extracted /{path} to {full}";
    }

    public async Task<string> PrepareForViewerAsync(string path, CancellationToken cancellationToken)
    {
        using var operation = lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        string directory = Path.Combine(viewerRoot, "dredge-" + Guid.NewGuid().ToString("N"));
        CacheFileSystem.CreateDirectory(directory);
        string file = Path.Combine(directory, StagedFileName(path));
        try
        {
            await using FileStream stream = CacheFileSystem.CreateFile(file);
            await Loaded.Files.CopyFileToAsync(path, stream, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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
        string name = new([.. path.Split('/')[^1].Select(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]);
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

    public bool WriteClipboard(string text)
    {
        using var operation = lifetime.Enter(CancellationToken.None);
        return Clipboard.Write(options.Clipboard, text);
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalSync)
        {
            return new(disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await lifetime.DisposeAsync();
        }
        finally
        {
            List<Exception> errors = [];
            try
            {
                await comparisonTargets.DisposeAsync();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            if (Session is not null)
            {
                try
                {
                    await Session.Files.DisposeAsync();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            targetSnapshots.Clear();
            compareGate.Dispose();
            if (errors.Count > 0)
            {
                throw new AggregateException("Could not release all explorer snapshots.", errors);
            }
        }
    }
}

internal sealed class RetainedExplorerComparison(ExplorerComparison comparison, IDisposable? retention = null) : IDisposable
{
    private IDisposable? retention = retention;
    public ExplorerComparison Comparison { get; } = comparison;
    public void Dispose() => Interlocked.Exchange(ref retention, null)?.Dispose();
}

// A displayed snapshot and readers are pins, not cache entries to sacrifice for
// a newer export. Only one unpinned inactive export survives between requests.
internal sealed class ExplorerTargetRetention<T> : IAsyncDisposable where T : class
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, Entry> cached = new(StringComparer.Ordinal);
    private readonly Dictionary<T, Entry> owned = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<T> displayed = new(ReferenceEqualityComparer.Instance);
    private readonly List<Task> cleanups = [];
    private long clock;
    private bool stopping;
    private Task? disposal;

    public bool TryGet(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (cached.TryGetValue(key, out Entry? entry))
            {
                entry.LastUse = ++clock;
                value = entry.Value;
                return true;
            }
            value = null;
            return false;
        }
    }

    public IDisposable Add(string key, T value, Func<ValueTask> cleanup)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            Entry entry = new(key, value, cleanup) { LastUse = ++clock, Readers = 1 };
            cached.Add(key, entry);
            owned.Add(value, entry);
            Trim();
            return new Lease(this, [entry]);
        }
    }

    public IDisposable Retain(IEnumerable<T> values)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            Entry[] entries = [.. values.Distinct<T>(ReferenceEqualityComparer.Instance)
                .Select(value => owned.GetValueOrDefault(value)).OfType<Entry>()];
            foreach (Entry entry in entries)
            {
                entry.Readers++;
            }
            return new Lease(this, entries);
        }
    }

    public void Display(IEnumerable<T> values)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            displayed.Clear();
            displayed.UnionWith(values);
            Trim();
        }
    }

    public Task CheckCleanupAsync()
    {
        lock (sync)
        {
            PruneCleanups();
            return Task.WhenAll(cleanups);
        }
    }

    internal int PendingCleanupCount
    {
        get
        {
            lock (sync)
            {
                PruneCleanups();
                return cleanups.Count;
            }
        }
    }

    private void PruneCleanups() => cleanups.RemoveAll(static task => task.IsCompletedSuccessfully);

    private void Release(Entry[] entries)
    {
        lock (sync)
        {
            foreach (Entry entry in entries)
            {
                entry.Readers--;
                if (stopping && entry.Readers == 0)
                {
                    StartCleanup(entry);
                }
            }
            if (!stopping)
            {
                Trim();
            }
        }
    }

    private void Trim()
    {
        Entry[] inactive = [.. cached.Values
            .Where(entry => entry.Readers == 0 && !displayed.Contains(entry.Value))
            .OrderByDescending(entry => entry.LastUse)];
        foreach (Entry entry in inactive.Skip(1))
        {
            cached.Remove(entry.Key);
            StartCleanup(entry);
        }
    }

    private void StartCleanup(Entry entry)
    {
        if (entry.CleanupStarted)
        {
            return;
        }
        entry.CleanupStarted = true;
        PruneCleanups();
        cleanups.Add(entry.Drained.Task);
        _ = CleanupAsync(entry);
    }

    private async Task CleanupAsync(Entry entry)
    {
        try
        {
            await entry.Cleanup();
            entry.Drained.TrySetResult();
        }
        catch (Exception exception)
        {
            entry.Drained.TrySetException(exception);
        }
        finally
        {
            lock (sync)
            {
                owned.Remove(entry.Value);
                PruneCleanups();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposal is not null)
            {
                return new(disposal);
            }
            stopping = true;
            displayed.Clear();
            cached.Clear();
            Entry[] entries = [.. owned.Values];
            foreach (Entry entry in entries.Where(entry => entry.Readers == 0))
            {
                StartCleanup(entry);
            }
            return new(disposal = Task.WhenAll(entries.Select(entry => entry.Drained.Task).Concat(cleanups)));
        }
    }

    private sealed class Entry(string key, T value, Func<ValueTask> cleanup)
    {
        public string Key { get; } = key;
        public T Value { get; } = value;
        public Func<ValueTask> Cleanup { get; } = cleanup;
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long LastUse { get; set; }
        public int Readers { get; set; }
        public bool CleanupStarted { get; set; }
    }

    private sealed class Lease(ExplorerTargetRetention<T> owner, Entry[] entries) : IDisposable
    {
        private ExplorerTargetRetention<T>? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(entries);
    }
}

internal sealed class ExplorerOperationLifetime : IAsyncDisposable
{
    private readonly Lock sync = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int active;
    private bool stopping;
    private Task? disposal;

    public Operation Enter(CancellationToken token)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            token.ThrowIfCancellationRequested();
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
            active++;
            return new(this, linked);
        }
    }

    private void Exit()
    {
        lock (sync)
        {
            if (--active == 0 && stopping)
            {
                drained.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            stopping = true;
            if (active == 0)
            {
                drained.TrySetResult();
            }
            return new(disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await cancellation.CancelAsync();
        }
        finally
        {
            await drained.Task;
            cancellation.Dispose();
        }
    }

    internal sealed class Operation(ExplorerOperationLifetime owner, CancellationTokenSource cancellation) : IDisposable
    {
        private ExplorerOperationLifetime? owner = owner;
        public CancellationToken Token => cancellation.Token;

        public void Dispose()
        {
            ExplorerOperationLifetime? previous = Interlocked.Exchange(ref owner, null);
            if (previous is not null)
            {
                cancellation.Dispose();
                previous.Exit();
            }
        }
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
