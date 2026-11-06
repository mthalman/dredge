namespace Valleysoft.Dredge.Tests;

using Spectre.Console;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands.Image;

public class DockerfileCommandTests
{
    private const string Registry = "test-registry.io";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HistoryMarkerInShellLiteralIsPreserved(bool noFormat, bool noColor)
    {
        string actual = await RenderHistoryAsync(
            ["/bin/sh -c #(nop) ENV X=1", "/bin/sh -c echo \"#(nop) literal\""],
            noFormat, noColor);

        Assert.Equal(
            $"FROM scratch{Environment.NewLine}ENV X=1{Environment.NewLine}RUN echo \"#(nop) literal\"{Environment.NewLine}",
            actual);
    }

    public static IEnumerable<TheoryDataRow<string, bool, bool>> GetLiteralRunData()
    {
        string[] commands =
        [
            "printf '%s' 'A &&  B'",
            "printf '%s' \"A  && B\"",
            "printf '%s' 'A;  B'",
            "printf '%s' \"A {  B\"",
            "printf '%s' 'A  { B'",
            "printf '%s' 'A  B'",
            "printf '%s' \"A '  B\"",
            "printf '%s' 'A \"  B'",
            "printf '%s' \"A \\\" &&  B\"",
            "echo A\\ \\ B",
            "echo $(printf '%s' 'A &&  B')",
            "echo \"$(printf '%s' \"$(printf '%s' 'A &&  B')\")\"",
            "echo $(case x in x) printf '%s' 'A &&  B';; esac)",
            "echo `printf '%s' 'A &&  B'`",
            "printf '%s' $'A\\' &&  B'",
            "printf '%s' ${VALUE:-'A &&  B'}",
            "echo done # literal &&  comment"
        ];
        foreach (string command in commands)
        {
            foreach (bool noFormat in new[] { false, true })
            {
                foreach (bool noColor in new[] { false, true })
                {
                    yield return new(command, noFormat, noColor);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(GetLiteralRunData))]
    public async Task RunLiteralSemanticsArePreserved(string command, bool noFormat, bool noColor)
    {
        string actual = await RenderHistoryAsync(
            ["/bin/sh -c #(nop) ENV X=1", $"/bin/sh -c {command}"],
            noFormat, noColor);

        string reconstructedCommand = actual.Split("RUN ", 2)[1].TrimEnd('\r', '\n')
            .Replace($"\\{Environment.NewLine}", "", StringComparison.Ordinal);
        Assert.Equal(command, reconstructedCommand);
        Assert.Equal(
            $"FROM scratch{Environment.NewLine}ENV X=1{Environment.NewLine}RUN {command}{Environment.NewLine}",
            actual);
    }

    [Theory]
    [InlineData(false, "echo B")]
    [InlineData(true, "echo B")]
    [InlineData(false, "echo \"B &&  C\"")]
    [InlineData(true, "echo \"B &&  C\"")]
    [InlineData(false, "echo \"$(printf '%s' 'B &&  C')\"")]
    [InlineData(true, "echo \"$(printf '%s' 'B &&  C')\"")]
    [InlineData(false, "echo $(printf '%s' 'B &&  C')")]
    [InlineData(true, "echo $(printf '%s' 'B &&  C')")]
    [InlineData(false, "echo \"${VALUE:-B &&  C}\"")]
    [InlineData(true, "echo \"${VALUE:-B &&  C}\"")]
    [InlineData(false, "echo \"$(printf '%s' \"$(printf '%s' 'B &&  C')\")\"")]
    [InlineData(true, "echo \"$(printf '%s' \"$(printf '%s' 'B &&  C')\")\"")]
    public async Task UnquotedRunStillFormats(bool noFormat, string secondCommand)
    {
        string actual = await RenderHistoryAsync(
            ["/bin/sh -c #(nop) ENV X=1", $"/bin/sh -c echo  A &&  {secondCommand}"],
            noFormat, noColor: false);

        string expectedRun = noFormat
            ? $"echo  A &&  {secondCommand}"
            : $"echo A && \\{Environment.NewLine}    {secondCommand}";
        Assert.Equal(
            $"FROM scratch{Environment.NewLine}ENV X=1{Environment.NewLine}RUN {expectedRun}{Environment.NewLine}",
            actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitShellMetadataIsRecognized(bool noFormat)
    {
        string actual = await RenderHistoryAsync(
            ["SHELL [\"/bin/bash\", \"-c\"]", "/bin/bash -c #(nop) ENV X=1", "/bin/bash -c echo \"#(nop) literal\""],
            noFormat, noColor: true);

        Assert.Equal(
            $"FROM scratch{Environment.NewLine}SHELL [\"/bin/bash\", \"-c\"]{Environment.NewLine}ENV X=1{Environment.NewLine}RUN echo \"#(nop) literal\"{Environment.NewLine}",
            actual);
    }

    [Theory]
    [InlineData(false, false, "powershell -Command")]
    [InlineData(false, true, "powershell -Command")]
    [InlineData(true, false, "powershell -Command")]
    [InlineData(true, true, "powershell -Command")]
    [InlineData(false, false, "pwsh -NoLogo -Command")]
    [InlineData(true, true, "pwsh -NoLogo -Command")]
    [InlineData(false, false, "powershell -ExecutionPolicy Bypass -Command")]
    [InlineData(false, true, "powershell -ExecutionPolicy Bypass -Command")]
    [InlineData(true, false, "powershell -ExecutionPolicy Bypass -Command")]
    [InlineData(true, true, "powershell -ExecutionPolicy Bypass -Command")]
    [InlineData(false, false, "/bin/bash --noprofile -o pipefail -c")]
    [InlineData(true, true, "/bin/bash --noprofile -o pipefail -c")]
    [InlineData(false, false, "powershell -ExecutionPolicy \"Bypass\" -Command")]
    [InlineData(true, true, "powershell -ExecutionPolicy \"Bypass\" -Command")]
    [InlineData(false, false, "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -Command")]
    [InlineData(true, true, "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -Command")]
    [InlineData(false, false, "C:\\Program Files\\PowerShell\\7\\pwsh.exe -Command")]
    [InlineData(true, true, "C:\\Program Files\\PowerShell\\7\\pwsh.exe -Command")]
    [InlineData(false, false, "C:\\tools\\O'Brien\\pwsh.exe -Command")]
    [InlineData(true, true, "C:\\tools\\O'Brien\\pwsh.exe -Command")]
    [InlineData(false, false, "C:\\tools\\build#1\\pwsh.exe -Command")]
    [InlineData(true, true, "C:\\tools\\build#1\\pwsh.exe -Command")]
    [InlineData(false, false, "/opt/O'Brien/sh -c")]
    [InlineData(true, true, "/opt/build#1/sh -c")]
    [InlineData(false, false, "cmd /V:ON /S /C")]
    [InlineData(false, true, "cmd /V:ON /S /C")]
    [InlineData(true, false, "cmd /V:ON /S /C")]
    [InlineData(true, true, "cmd /V:ON /S /C")]
    [InlineData(false, false, "cmd /E:ON /S /C")]
    [InlineData(true, true, "cmd /E:ON /S /C")]
    [InlineData(false, false, "cmd /F:OFF /S /C")]
    [InlineData(true, true, "cmd /F:OFF /S /C")]
    [InlineData(false, false, "cmd /T:0A /S /C")]
    [InlineData(true, true, "cmd /T:0A /S /C")]
    public async Task InheritedShellMetadataIsRecognized(bool noFormat, bool noColor, string shell)
    {
        string actual = await RenderHistoryAsync(
            [$"{shell} #(nop) ENV X=1", $"{shell} echo \"#(nop) literal\""],
            noFormat, noColor);

        Assert.Equal(
            $"FROM scratch{Environment.NewLine}ENV X=1{Environment.NewLine}RUN echo \"#(nop) literal\"{Environment.NewLine}",
            actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownShellBoundaryProtectsCommandText(bool noFormat)
    {
        const string Shell = "/bin/sh -c";
        const string Command = "echo \"\\\" #(nop) literal\"";
        string actual = await RenderHistoryAsync(
            [$"{Shell} #(nop) ENV X=1", $"{Shell} {Command}"],
            noFormat, noColor: true);

        Assert.Equal(
            $"FROM scratch{Environment.NewLine}ENV X=1{Environment.NewLine}RUN {Command}{Environment.NewLine}",
            actual);
    }

    [Theory]
    [InlineData(false, "RUN /bin/sh -c echo \"\\\" #(nop) literal\" # buildkit")]
    [InlineData(true, "RUN /bin/sh -c echo \"\\\" #(nop) literal\" # buildkit")]
    [InlineData(false, "RUN /bin/sh -c echo prefix\" #(nop) literal\" # buildkit")]
    [InlineData(true, "RUN /bin/sh -c echo prefix\" #(nop) literal\" # buildkit")]
    [InlineData(false, "RUN /bin/sh -c echo prefix' #(nop) literal' # buildkit")]
    [InlineData(true, "RUN /bin/sh -c echo prefix' #(nop) literal' # buildkit")]
    [InlineData(false, "RUN /bin/sh -c echo prefix\\\"\\ #(nop) literal # buildkit")]
    [InlineData(true, "RUN /bin/sh -c echo prefix\\\"\\ #(nop) literal # buildkit")]
    [InlineData(false, "LABEL text=prefix\" #(nop) literal\"")]
    [InlineData(true, "LABEL text=prefix\" #(nop) literal\"")]
    [InlineData(false, "CMD /bin/sh -c echo prefix\" #(nop) literal\"")]
    [InlineData(true, "CMD /bin/sh -c echo prefix\" #(nop) literal\"")]
    [InlineData(false, "run\t/bin/sh -c echo prefix\" #(nop) literal\" # buildkit")]
    [InlineData(true, "run\t/bin/sh -c echo prefix\" #(nop) literal\" # buildkit")]
    public async Task BuildKitCommandTextIsNotAnInheritedShellPrefix(bool noFormat, string command)
    {
        string actual = await RenderHistoryAsync([command], noFormat, noColor: true);

        Assert.Equal($"FROM scratch{Environment.NewLine}{command}{Environment.NewLine}", actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataDiscoveryHandlesLongWhitespaceInBoundedTime(bool hasMetadata)
    {
        string prefix = "/bin/sh -c" + new string(' ', 1_000_000);
        string history = prefix + (hasMetadata ? "#(nop) ENV X=1" : "echo done");
        Stopwatch timer = Stopwatch.StartNew();

        int index = DockerfileCommand.FindHistoryMetadataIndex(history);

        timer.Stop();
        Assert.Equal(hasMetadata ? prefix.Length : -1, index);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"Metadata scanning took {timer.Elapsed}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongWhitespaceBuildKitHistoryIsPreserved(bool noFormat)
    {
        string history = "RUN /bin/sh -c echo" + new string(' ', 16_384) + "done # buildkit";
        string actual = await RenderHistoryAsync([history], noFormat, noColor: true);

        Assert.Equal($"FROM scratch{Environment.NewLine}{history}{Environment.NewLine}", actual);
    }

    private static async Task<string> RenderHistoryAsync(string[] history, bool noFormat, bool noColor)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Image image = new()
        {
            Os = "linux",
            Architecture = "amd64",
            History = [.. history.Select(static createdBy => new LayerHistory { CreatedBy = createdBy })]
        };
        Mock<IDockerRegistryClient> client = new();
        client.Setup(o => o.Manifests.GetAsync("repo", "tag", cancellationToken))
            .ReturnsAsync(new ManifestInfo("media-type", "digest", new DockerManifest
            {
                Config = new ManifestConfig { Digest = "config-digest" }
            }));
        client.Setup(o => o.Blobs.GetAsync("repo", "config-digest", cancellationToken))
            .ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(image))));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync(Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        DockerfileCommand command = new(factory.Object)
        {
            Options = new DockerfileOptions
            {
                Image = $"{Registry}/repo:tag",
                NoFormat = noFormat,
                NoColor = noColor
            }
        };

        string markup = await command.GetMarkupStringAsync(cancellationToken);
        StringWriter output = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output)
        });
        console.Profile.Width = 32_768;
        console.Write(new Markup(markup));
        return output.ToString();
    }

    public static IEnumerable<TheoryDataRow<TestScenario>> GetTestData()
    {
        DirectoryInfo workingDir = new(Path.Combine(Environment.CurrentDirectory, "TestData", "DockerfileCommand"));
        return workingDir.GetDirectories()
            .SelectMany(static dir => new TestScenario[]
            {
                new(
                    dir.Name,
                    Path.Combine(dir.FullName, "image.json"),
                    noFormat: false,
                    Path.Combine(dir.FullName, "expected-output-format.txt")),
                new(
                    dir.Name,
                    Path.Combine(dir.FullName, "image.json"),
                    noFormat: true,
                    Path.Combine(dir.FullName, "expected-output-no-format.txt"))
            })
            .Select(static scenario => new TheoryDataRow<TestScenario>(scenario));

    }

    public class TestScenario
    {
        public TestScenario(string name, string imagePath, bool noFormat, string expectedOutputPath)
        {
            Name = name;
            ImagePath = imagePath;
            NoFormat = noFormat;
            ExpectedOutputPath = expectedOutputPath;
        }

        public string Name { get; }
        public string ImagePath { get; }
        public bool NoFormat { get; }
        public string ExpectedOutputPath { get; }
    }


    [Theory]
    [MemberData(nameof(GetTestData))]
    public async Task Test(TestScenario scenario)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string RepoName = "repo";
        const string TagName = "tag";
        const string ImageName = $"{Registry}/{RepoName}:{TagName}";
        const string Digest = "digest";

        Mock<IDockerRegistryClientFactory> clientFactoryMock = new();
        Mock<IDockerRegistryClient> mcrClientMock = new();

        clientFactoryMock
            .Setup(o => o.GetClientAsync(RegistryHelper.McrRegistry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mcrClientMock.Object);

        ManifestLayer[] layers = [];
        string imageJson = File.ReadAllText(scenario.ImagePath);
        Image image = JsonSerializer.Deserialize<Image>(imageJson)!;
        if (image.Os == "windows")
        {
            image.RootFilesystem = new RootFilesystem
            {
                Type = "layers",
                DiffIds = ["baseDiff0", "baseDiff1", "appDiff"]
            };

            layers =
            [
                new ManifestLayer
                {
                    Digest = "repackedLayer0Digest"
                },
                new ManifestLayer
                {
                    Digest = "repackedLayer1Digest"
                }
            ];

            mcrClientMock
                .Setup(o => o.Blobs.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), cancellationToken))
                .ReturnsAsync(false);

            mcrClientMock
                .Setup(o => o.Manifests.ExistsAsync(
                    It.IsAny<string>(), It.IsAny<string>(), cancellationToken))
                .ReturnsAsync(false);

            string baseImageTag = $"{image.OsVersion}-{image.Architecture}";
            mcrClientMock
                .Setup(o => o.Manifests.ExistsAsync(
                    "windows/servercore", baseImageTag, cancellationToken))
                .ReturnsAsync(true);

            const string BaseConfigDigest = "baseConfigDigest";
            mcrClientMock
                .Setup(o => o.Manifests.GetAsync(
                    "windows/servercore", baseImageTag, cancellationToken))
                .ReturnsAsync(new ManifestInfo("media-type", "base-manifest-digest",
                    new DockerManifest
                    {
                        Config = new ManifestConfig
                        {
                            Digest = BaseConfigDigest
                        }
                    }));

            Image baseImage = new()
            {
                RootFilesystem = new RootFilesystem
                {
                    Type = "layers",
                    DiffIds = ["baseDiff0", "baseDiff1"]
                },
                History = [.. image.History.Take(2)]
            };
            mcrClientMock
                .Setup(o => o.Blobs.GetAsync(
                    "windows/servercore", BaseConfigDigest, cancellationToken))
                .ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(baseImage))));

            imageJson = JsonSerializer.Serialize(image);
        }

        Mock<IDockerRegistryClient> registryClientMock = new();
        registryClientMock
            .Setup(o => o.Manifests.GetAsync(RepoName, TagName, cancellationToken))
            .ReturnsAsync(new ManifestInfo("media-type", "digest",
                new DockerManifest
                {
                    Config = new ManifestConfig
                    {
                        Digest = Digest
                    },
                    Layers = layers
                }));

        registryClientMock
            .Setup(o => o.Blobs.GetAsync(RepoName, Digest, cancellationToken))
            .ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes(imageJson)));

        clientFactoryMock
            .Setup(o => o.GetClientAsync(Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(registryClientMock.Object);

        DockerfileCommand command = new(clientFactoryMock.Object)
        {
            Options = new DockerfileOptions
            {
                Image = ImageName,
                NoFormat = scenario.NoFormat
            }
        };

        string markupStr = await command.GetMarkupStringAsync(cancellationToken);

        string actual = TestHelper.Normalize(markupStr);
        string expected = TestHelper.Normalize(File.ReadAllText(scenario.ExpectedOutputPath));
        Assert.Equal(expected, actual);
    }
}
