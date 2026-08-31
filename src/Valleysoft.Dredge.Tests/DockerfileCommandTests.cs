namespace Valleysoft.Dredge.Tests;

using Spectre.Console;
using System.Text;
using System.Text.Json;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands.Image;

public class DockerfileCommandTests
{
    private const string Registry = "test-registry.io";

    public static IEnumerable<TheoryDataRow<TestScenario>> GetTestData()
    {
        DirectoryInfo workingDir = new(Path.Combine(Environment.CurrentDirectory, "TestData", "DockerfileCommand"));
        return workingDir.GetDirectories()
            .SelectMany(dir => new TestScenario[]
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
            .Select(scenario => new TheoryDataRow<TestScenario>(scenario));

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
            .Setup(o => o.GetClientAsync(RegistryHelper.McrRegistry))
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
                History = image.History.Take(2).ToArray()
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
            .Setup(o => o.GetClientAsync(Registry))
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
