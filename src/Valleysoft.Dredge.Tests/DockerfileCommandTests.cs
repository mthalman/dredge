namespace Valleysoft.Dredge.Tests;

using Microsoft.Rest;
using Newtonsoft.Json;
using Spectre.Console;
using System.Text;
using Valleysoft.DockerRegistryClient.Models;
using DockerfileCommand = ImageCommand.DockerfileCommand;

public class DockerfileCommandTests
{
    private const string Registry = "test-registry.io";

    public static IEnumerable<object[]> GetTestData()
    {
        DirectoryInfo workingDir = new(Path.Combine(Environment.CurrentDirectory, "TestData", "DockerfileCommand"));
        return workingDir.GetDirectories()
            .SelectMany(dir => new TestScenario[]
            {
                new TestScenario(
                    dir.Name,
                    Path.Combine(dir.FullName, "image.json"),
                    noFormat: false,
                    Path.Combine(dir.FullName, "expected-output-format.txt")),
                new TestScenario(
                    dir.Name,
                    Path.Combine(dir.FullName, "image.json"),
                    noFormat: true,
                    Path.Combine(dir.FullName, "expected-output-no-format.txt"))
            })
            .Select(scenario => new object[] { scenario });

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
            .Setup(o => o.GetClientAsync(ImageCommand.McrRegistry))
            .ReturnsAsync(mcrClientMock.Object);

        ManifestLayer[] layers = Array.Empty<ManifestLayer>();
        Image image = JsonConvert.DeserializeObject<Image>(File.ReadAllText(scenario.ImagePath))!;
        if (image.Os == "windows")
        {
            layers = new[]
            {
                new ManifestLayer
                {
                    Digest = "layer0digest"
                },
                new ManifestLayer
                {
                    Digest = "layer1digest"
                }
            };

            mcrClientMock
                .Setup(o => o.Blobs.ExistsWithHttpMessagesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HttpOperationResponse<bool>
                {
                    Body = false
                });

            mcrClientMock
                .Setup(o => o.Blobs.ExistsWithHttpMessagesAsync(
                    "windows/servercore", It.Is<string>(digest => digest == "layer0digest" || digest == "layer1digest"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HttpOperationResponse<bool>
                {
                    Body = true
                });
        }

        Mock<IDockerRegistryClient> registryClientMock = new();
        registryClientMock
            .Setup(o => o.Manifests.GetWithHttpMessagesAsync(RepoName, TagName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<ManifestInfo>
            {
                Body = new ManifestInfo("media-type", "digest",
                    new DockerManifestV2
                    {
                        Config = new ManifestConfig
                        {
                            Digest = Digest
                        },
                        Layers = layers
                    })
            });

        registryClientMock
            .Setup(o => o.Blobs.GetWithHttpMessagesAsync(RepoName, Digest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<Stream>
            {
                Body = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(scenario.ImagePath)))
            });

        clientFactoryMock
            .Setup(o => o.GetClientAsync(Registry))
            .ReturnsAsync(registryClientMock.Object);

        DockerfileCommand command = new(clientFactoryMock.Object);

        string markupStr = await command.GetMarkupStringAsync(ImageName, false, scenario.NoFormat);

        string actual = TestHelper.Normalize(markupStr);
        string expected = TestHelper.Normalize(File.ReadAllText(scenario.ExpectedOutputPath));
        Assert.Equal(expected, actual);
    }
}
