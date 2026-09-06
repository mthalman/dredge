using System.CommandLine;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;

namespace Valleysoft.Dredge.Tests;

public sealed class InjectedDependencyCommandTests
{
    [Fact]
    public async Task CompareFilesCommand_WhenToolIsNotConfiguredDoesNotAccessRegistryOrLaunchProcess()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"dredge-compare-files-{Guid.NewGuid():N}");
        string settingsPath = Path.Combine(tempRoot, "settings.json");
        Mock<IDockerRegistryClientFactory> clientFactory = new(MockBehavior.Strict);
        TestProcessLauncher processLauncher = new();
        TestCompareFilesCommand command = new(
            clientFactory.Object,
            new AppSettingsStore(settingsPath),
            new TestDredgePathProvider(tempRoot),
            processLauncher);
        RecordingProcessTerminator processTerminator = new();
        ((IProcessTerminationAware)command).ProcessTerminator = processTerminator;

        try
        {
            await command
                .Parse(["registry.example/base:latest", "registry.example/target:latest"])
                .InvokeAsync(
                    new InvocationConfiguration(),
                    TestContext.Current.CancellationToken);

            Assert.Equal(1, processTerminator.ExitCode);
            Assert.Contains(settingsPath, command.ErrorOutput.ToString());
            Assert.Null(processLauncher.StartInfo);
            clientFactory.VerifyNoOtherCalls();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class TestCompareFilesCommand : CompareFilesCommand
    {
        public TestCompareFilesCommand(
            IDockerRegistryClientFactory dockerRegistryClientFactory,
            IAppSettingsStore settingsStore,
            IDredgePathProvider pathProvider,
            IProcessLauncher processLauncher)
            : base(dockerRegistryClientFactory, settingsStore, pathProvider, processLauncher)
        {
        }

        public StringWriter ErrorOutput { get; } = new();

        protected override TextWriter Error => ErrorOutput;
    }
}
