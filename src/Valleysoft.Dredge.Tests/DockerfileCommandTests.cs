namespace Valleysoft.Dredge.Tests;

using Newtonsoft.Json;
using Spectre.Console;
using System.Text;
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
        Image image = JsonConvert.DeserializeObject<Image>(File.ReadAllText(scenario.ImagePath))!;
        if (image.Os == "windows")
        {
            layers =
            [
                new ManifestLayer
                {
                    Digest = "layer0digest"
                },
                new ManifestLayer
                {
                    Digest = "layer1digest"
                }
            ];

            mcrClientMock
                .Setup(o => o.Blobs.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            mcrClientMock
                .Setup(o => o.Blobs.ExistsAsync(
                    "windows/servercore", It.Is<string>(digest => digest == "layer0digest" || digest == "layer1digest"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        }

        Mock<IDockerRegistryClient> registryClientMock = new();
        registryClientMock
            .Setup(o => o.Manifests.GetAsync(RepoName, TagName, It.IsAny<CancellationToken>()))
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
            .Setup(o => o.Blobs.GetAsync(RepoName, Digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(scenario.ImagePath))));

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

        string markupStr = await command.GetMarkupStringAsync();

        string actual = TestHelper.Normalize(markupStr);
        string expected = TestHelper.Normalize(File.ReadAllText(scenario.ExpectedOutputPath));
        Assert.Equal(expected, actual);
    }
}
