using System.CommandLine;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Commands.Settings;

namespace Valleysoft.Dredge.Tests;

public sealed class CompareFilesWorkspaceTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private string CompareRoot => Path.Combine(cache.Paths.TempPath, "compare");

    [Fact]
    public async Task SequentialInvocations_PreserveInputsAfterLauncherReturns()
    {
        RecordingLauncher launcher = new();
        TestCommand command = CreateCommand(CreateFactory(), launcher);

        await InvokeAsync(command, "a", "b");
        ProcessStartInfo first = Assert.Single(launcher.Launches);
        string firstWorkspace = AssertInputs(first, "a", "b");
        await InvokeAsync(command, "c", "d");

        Assert.Equal(2, launcher.Launches.Count);
        string secondWorkspace = AssertInputs(launcher.Launches[1], "c", "d");
        Assert.NotEqual(firstWorkspace, secondWorkspace);
        AssertInputs(first, "a", "b");
        Assert.Equal(2, Directory.GetDirectories(CompareRoot).Length);
        Assert.Contains(firstWorkspace, command.ErrorOutput.ToString());
        Assert.Contains(secondWorkspace, command.ErrorOutput.ToString());
        Assert.Contains("dredge settings clear-cache", command.ErrorOutput.ToString());
    }

    [Fact]
    public async Task ConcurrentInvocations_KeepBothSidesInTheirOwnWorkspace()
    {
        TaskCompletionSource bothPreparingTargets = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseTargets = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int preparingTargets = 0;
        IDockerRegistryClientFactory factory = CreateFactory(async (image, ct) =>
        {
            if (image is "b" or "d")
            {
                if (Interlocked.Increment(ref preparingTargets) == 2)
                {
                    bothPreparingTargets.SetResult();
                }
                await releaseTargets.Task.WaitAsync(ct);
            }
        });
        RecordingLauncher firstLauncher = new();
        RecordingLauncher secondLauncher = new();
        Task first = InvokeAsync(CreateCommand(factory, firstLauncher), "a", "b");
        Task second = InvokeAsync(CreateCommand(factory, secondLauncher), "c", "d");
        try
        {
            await bothPreparingTargets.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
            string[] workspaces = Directory.GetDirectories(CompareRoot);
            Assert.Equal(2, workspaces.Length);
            Assert.Equal(["a", "c"], workspaces.Select(path =>
                File.ReadAllText(Path.Combine(path, "base", "value"))).Order().ToArray());
        }
        finally
        {
            releaseTargets.TrySetResult();
            await Task.WhenAll(first, second);
        }

        string firstWorkspace = AssertInputs(Assert.Single(firstLauncher.Launches), "a", "b");
        string secondWorkspace = AssertInputs(Assert.Single(secondLauncher.Launches), "c", "d");
        Assert.NotEqual(firstWorkspace, secondWorkspace);
    }

    [Theory]
    [InlineData("preparation")]
    [InlineData("cancellation")]
    [InlineData("launch")]
    public async Task UnsuccessfulInvocation_RemovesOnlyItsWorkspace(string failure)
    {
        RecordingLauncher originalLauncher = new();
        await InvokeAsync(CreateCommand(CreateFactory(), originalLauncher), "a", "b");
        ProcessStartInfo original = Assert.Single(originalLauncher.Launches);
        string originalWorkspace = AssertInputs(original, "a", "b");
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        IDockerRegistryClientFactory factory = CreateFactory((image, ct) =>
        {
            if (image == "d" && failure != "launch")
            {
                if (failure == "cancellation")
                {
                    cancellation.Cancel();
                    ct.ThrowIfCancellationRequested();
                }
                throw new IOException("Preparation failed.");
            }
            return Task.CompletedTask;
        });
        RecordingLauncher failedLauncher = new()
        {
            Failure = failure == "launch" ? new IOException("Launch failed.") : null
        };
        TestCommand command = CreateCommand(factory, failedLauncher);
        RecordingProcessTerminator terminator = new();
        ((IProcessTerminationAware)command).ProcessTerminator = terminator;

        Exception? error = await Record.ExceptionAsync(() =>
            InvokeAsync(command, "c", "d", cancellation.Token));

        if (failure == "cancellation")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(1, terminator.ExitCode);
            Assert.Contains(failure == "launch" ? "Launch failed." : "Preparation failed.",
                command.ErrorOutput.ToString());
        }
        Assert.Empty(failedLauncher.Launches);
        Assert.Equal(originalWorkspace, Assert.Single(Directory.GetDirectories(CompareRoot)));
        AssertInputs(original, "a", "b");
    }

    [Fact]
    public async Task CleanupFailure_ReportsWorkspaceWithoutHidingPreparationError()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        FileStream? lockedInput = null;
        string? workspace = null;
        IDockerRegistryClientFactory factory = CreateFactory((image, _) =>
        {
            if (image == "b")
            {
                workspace = Assert.Single(Directory.GetDirectories(CompareRoot));
                lockedInput = new FileStream(Path.Combine(workspace, "base", "value"),
                    FileMode.Open, FileAccess.Read, FileShare.Read);
                throw new IOException("Preparation failed.");
            }
            return Task.CompletedTask;
        });
        TestCommand command = CreateCommand(factory, new RecordingLauncher());
        RecordingProcessTerminator terminator = new();
        ((IProcessTerminationAware)command).ProcessTerminator = terminator;
        try
        {
            await InvokeAsync(command, "a", "b");
            Assert.Equal(1, terminator.ExitCode);
            Assert.Contains("Preparation failed.", command.ErrorOutput.ToString());
            Assert.Contains($"Could not remove comparison workspace '{workspace}'",
                command.ErrorOutput.ToString());
        }
        finally
        {
            lockedInput?.Dispose();
        }
    }

    [Fact]
    public async Task ClearCache_RemovesAllRetainedWorkspaces()
    {
        RecordingLauncher launcher = new();
        TestCommand command = CreateCommand(CreateFactory(), launcher);
        await InvokeAsync(command, "a", "b");
        await InvokeAsync(command, "c", "d");
        string unrelated = Path.Combine(cache.Paths.TempPath, "unrelated");
        await File.WriteAllTextAsync(unrelated, "keep", Token);
        using StringWriter output = new();
        ClearCacheCommand clear = new(cache.Paths, output, new TestProcessTerminator());

        Assert.Equal(0, await clear.Parse([]).InvokeAsync(new InvocationConfiguration(), Token));

        Assert.False(Directory.Exists(CompareRoot));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated, Token));
        Assert.Contains(CompareRoot, output.ToString());
    }

    private TestCommand CreateCommand(IDockerRegistryClientFactory factory, IProcessLauncher launcher)
    {
        AppSettings settings = new();
        settings.FileCompareTool.ExePath = "comparison-tool";
        settings.FileCompareTool.Args = "--base \"{0}\" --target \"{1}\"";
        TestCommand command = new(factory, new TestAppSettingsStore(settings), cache.Paths, launcher);
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();
        return command;
    }

    private static async Task InvokeAsync(TestCommand command, string baseline, string target,
        CancellationToken? cancellationToken = null)
    {
        Assert.Equal(0, await command.Parse([$"registry.example/{baseline}:latest",
            $"registry.example/{target}:latest"]).InvokeAsync(
                new InvocationConfiguration { EnableDefaultExceptionHandler = false }, cancellationToken ?? Token));
    }

    private string AssertInputs(ProcessStartInfo startInfo, string baseline, string target)
    {
        string[] arguments = startInfo.Arguments.Split('"');
        Assert.Equal(5, arguments.Length);
        string basePath = arguments[1];
        string targetPath = arguments[3];
        string workspace = Path.GetDirectoryName(basePath)!;
        Assert.Equal(CompareRoot, Path.GetDirectoryName(workspace));
        Assert.Equal(Path.Combine(workspace, "base"), basePath);
        Assert.Equal(Path.Combine(workspace, "target"), targetPath);
        Assert.Equal($"--base \"{basePath}\" --target \"{targetPath}\"", startInfo.Arguments);
        Assert.Equal("comparison-tool", startInfo.FileName);
        Assert.Equal(baseline, File.ReadAllText(Path.Combine(basePath, "value")));
        Assert.Equal(target, File.ReadAllText(Path.Combine(targetPath, "value")));
        return workspace;
    }

    private static IDockerRegistryClientFactory CreateFactory(
        Func<string, CancellationToken, Task>? beforeManifest = null)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        foreach (string image in new[] { "a", "b", "c", "d" })
        {
            using MemoryStream bytes = new();
            using (GZipStream gzip = new(bytes, CompressionMode.Compress, leaveOpen: true))
            using (TarWriter tar = new(gzip, leaveOpen: true))
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "value")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(image))
                });
            }
            byte[] layer = bytes.ToArray();
            string digest = LayerCacheTestContext.Digest(layer);
            client.Setup(value => value.Manifests.GetAsync(image, "latest", It.IsAny<CancellationToken>()))
                .Returns(async (string _, string _, CancellationToken ct) =>
                {
                    if (beforeManifest is not null)
                    {
                        await beforeManifest(image, ct);
                    }
                    return new ManifestInfo("application/test", $"sha256:{image}",
                        new DockerManifest { Layers = [new ManifestLayer { Digest = digest, Size = layer.Length }] });
                });
            client.Setup(value => value.Blobs.GetRangeAsync(image, digest, 0, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new BlobDownloadResult(new MemoryStream(layer), false, null, null, layer.Length));
        }
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(value => value.GetClientAsync("registry.example", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        return factory.Object;
    }

    public ValueTask DisposeAsync() => cache.DisposeAsync();

    private sealed class TestCommand(
        IDockerRegistryClientFactory factory, IAppSettingsStore settings,
        IDredgePathProvider paths, IProcessLauncher launcher)
        : CompareFilesCommand(factory, settings, paths, launcher)
    {
        public StringWriter ErrorOutput { get; } = new();
        protected override TextWriter Error => ErrorOutput;
    }

    private sealed class RecordingLauncher : IProcessLauncher
    {
        public List<ProcessStartInfo> Launches { get; } = [];
        public Exception? Failure { get; init; }

        public void Start(ProcessStartInfo startInfo)
        {
            if (Failure is not null)
            {
                throw Failure;
            }
            Launches.Add(startInfo);
        }
    }
}
