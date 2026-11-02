using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using static Valleysoft.Dredge.Tests.LayerCacheTestContext;

namespace Valleysoft.Dredge.Tests;

public sealed class HistorylessLayerComparisonTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DifferentRegistryLayersWithoutHistoryAreNotEqual(bool omitHistory, bool includeHistory)
    {
        var result = await CompareAsync(omitHistory, includeHistory, equal: false, scratch: false);

        Assert.Equal(2, result.ExitCode);
        Assert.False(result.Json["summary"]!["areEqual"]!.GetValue<bool>());
        JsonNode comparison = Assert.Single(result.Json["layerComparisons"]!.AsArray())!;
        Assert.NotEqual(comparison["base"]!["digest"]!.GetValue<string>(),
            comparison["target"]!["digest"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EqualAndScratchRegistryImagesWithoutHistoryRemainEqual(bool omitHistory, bool scratch)
    {
        var result = await CompareAsync(omitHistory, includeHistory: true, equal: true, scratch);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json["summary"]!["areEqual"]!.GetValue<bool>());
        Assert.Equal(scratch ? 0 : 1, result.Json["layerComparisons"]!.AsArray().Count);
    }

    private static async Task<(int ExitCode, JsonNode Json)> CompareAsync(
        bool omitHistory, bool includeHistory, bool equal, bool scratch)
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        AddImage("before", "one");
        AddImage("after", equal ? "one" : "two");
        Mock<IDockerRegistryClientFactory> registries = new();
        registries.Setup(value => value.GetClientAsync("registry.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        using StringWriter output = new();
        CompareLayersCommand command = new(registries.Object,
            AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) }))
        {
            ImageSourceFactory = new ImageSourceFactory(registries.Object, new TestAppSettingsStore(new(), ""))
        };
        RecordingProcessTerminator terminator = new();
        ((IProcessTerminationAware)command).ProcessTerminator = terminator;
        int invocation = await command.Parse([
            "registry://registry.test/app:before", "registry://registry.test/app:after",
            "--output", "json", "--compressed-size", .. includeHistory ? new[] { "--history" } : []
        ]).InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);
        Assert.Equal(0, invocation);
        client.Verify(value => value.Blobs.GetRangeAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
        return (terminator.ExitCode ?? invocation, JsonNode.Parse(output.ToString())!);

        void AddImage(string tag, string payload)
        {
            string digest = Digest(Encoding.UTF8.GetBytes(payload));
            Dictionary<string, object> fields = new()
            {
                ["os"] = "linux", ["architecture"] = "amd64",
                ["rootfs"] = new { type = "layers", diff_ids = scratch ? Array.Empty<string>() : [digest] }
            };
            if (!omitHistory)
            {
                fields["history"] = Array.Empty<object>();
            }
            byte[] config = JsonSerializer.SerializeToUtf8Bytes(fields);
            string configDigest = Digest(config);
            OciImageManifest manifest = new()
            {
                Config = new() { Digest = configDigest },
                Layers = scratch ? [] : [new OciDescriptor { Digest = digest, Size = 3 }]
            };
            client.Setup(value => value.Manifests.GetAsync("app", tag, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ManifestInfo("application/vnd.oci.image.manifest.v1+json",
                    Digest(Encoding.UTF8.GetBytes(tag)), manifest));
            client.Setup(value => value.Blobs.GetAsync("app", configDigest, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(config));
        }
    }
}
