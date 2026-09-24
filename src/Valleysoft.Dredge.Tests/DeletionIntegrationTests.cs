using System.CommandLine;
using Valleysoft.DockerRegistryClient;
using Valleysoft.Dredge.Commands;
using ManifestDeleteCommand = Valleysoft.Dredge.Commands.Manifest.DeleteCommand;
using TagDeleteCommand = Valleysoft.Dredge.Commands.Tag.DeleteCommand;

namespace Valleysoft.Dredge.Tests;

[Trait("Category", "Integration")]
public sealed class DeletionIntegrationTests(RegistryFixture fixture, ZotRegistryFixture zot)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManifestDeletion_RemovesDigestAndAllAssociatedTags(bool byTag)
    {
        await fixture.EnsureInitializedAsync();
        string repository = fixture.GetRepositoryName(nameof(ManifestDeletion_RemovesDigestAndAllAssociatedTags));
        LayerSeed layer = await fixture.UploadLayerAsync(repository, LayerEntry.File("file.txt", "preserved"));
        ImageSeed image = await fixture.PutImageAsync(repository, "first", [layer]);
        ImageSeed alias = await fixture.PutImageAsync(repository, "second", [layer]);
        Assert.Equal(image.Manifest.Digest, alias.Manifest.Digest);
        string reference = byTag ? $"{repository}:first" : $"{repository}@{image.Manifest.Digest}";
        using StringWriter output = new();
        using StringWriter error = new();
        ManifestDeleteCommand command = new LiveManifestDeleteCommand(fixture.CreateClientFactory(), output, error);

        Assert.Equal(0, await InvokeAsync(command, $"{fixture.Registry}/{reference}"));

        using RegistryClient client = fixture.CreateClient();
        Assert.False(await client.Manifests.ExistsAsync(repository, image.Manifest.Digest));
        Assert.False(await client.Manifests.ExistsAsync(repository, "first"));
        Assert.False(await client.Manifests.ExistsAsync(repository, "second"));
        Assert.True(await client.Blobs.ExistsAsync(repository, layer.Digest));
        Assert.True(await client.Blobs.ExistsAsync(repository, image.Config.Digest));
        Assert.Contains(image.Manifest.Digest, output.ToString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TagDeletion_PreservesManifestAndOtherTag(bool isIndex)
    {
        await zot.EnsureInitializedAsync();
        string repository = zot.GetRepositoryName(nameof(TagDeletion_PreservesManifestAndOtherTag));
        ImageSeed image = await zot.PutImageAsync(repository, "child", []);
        string digest;
        if (isIndex)
        {
            ManifestSeed index = await zot.PutIndexAsync(repository, "first", (image, "linux", "amd64"));
            await zot.PutIndexAsync(repository, "second", (image, "linux", "amd64"));
            digest = index.Digest;
        }
        else
        {
            await zot.PutImageAsync(repository, "first", []);
            await zot.PutImageAsync(repository, "second", []);
            digest = image.Manifest.Digest;
        }
        using StringWriter output = new();
        using StringWriter error = new();
        TagDeleteCommand command = new LiveTagDeleteCommand(zot.CreateClientFactory(), output, error);

        Assert.Equal(0, await InvokeAsync(command, $"{zot.Registry}/{repository}:first"));

        using RegistryClient client = zot.CreateClient();
        Assert.False(await client.Manifests.ExistsAsync(repository, "first"));
        Assert.True(await client.Manifests.ExistsAsync(repository, "second"));
        Assert.True(await client.Manifests.ExistsAsync(repository, digest));
        Assert.True(await client.Manifests.ExistsAsync(repository, image.Manifest.Digest));
        Assert.Contains("Deleted tag", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task IndexDeletion_PreservesChildrenAndLayers()
    {
        await fixture.EnsureInitializedAsync();
        string repository = fixture.GetRepositoryName(nameof(IndexDeletion_PreservesChildrenAndLayers));
        LayerSeed layer = await fixture.UploadLayerAsync(repository, LayerEntry.File("file.txt", "shared"));
        ImageSeed amd64 = await fixture.PutImageAsync(repository, "amd64", [layer]);
        ImageSeed arm64 = await fixture.PutImageAsync(repository, "arm64", [layer], architecture: "arm64");
        ManifestSeed index = await fixture.PutIndexAsync(
            repository, "multi", (amd64, "linux", "amd64"), (arm64, "linux", "arm64"));
        await fixture.PutIndexAsync(
            repository, "alias", (amd64, "linux", "amd64"), (arm64, "linux", "arm64"));
        using StringWriter output = new();
        using StringWriter error = new();
        ManifestDeleteCommand command = new LiveManifestDeleteCommand(fixture.CreateClientFactory(), output, error);

        Assert.Equal(0, await InvokeAsync(command, $"{fixture.Registry}/{repository}:multi"));

        using RegistryClient client = fixture.CreateClient();
        Assert.False(await client.Manifests.ExistsAsync(repository, index.Digest));
        Assert.False(await client.Manifests.ExistsAsync(repository, "multi"));
        Assert.False(await client.Manifests.ExistsAsync(repository, "alias"));
        foreach (ImageSeed child in new[] { amd64, arm64 })
        {
            Assert.True(await client.Manifests.ExistsAsync(repository, child.Manifest.Digest));
            Assert.True(await client.Manifests.ExistsAsync(repository, child.Reference));
            Assert.True(await client.Blobs.ExistsAsync(repository, child.Config.Digest));
        }
        Assert.True(await client.Blobs.ExistsAsync(repository, layer.Digest));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledDeletion_DoesNotDeleteManifest(bool tagOnly)
    {
        await using RegistryFixture disabled = new(
            _ => RegistryFixture.StartDistributionContainerAsync(deleteEnabled: false));
        await disabled.EnsureInitializedAsync();
        string repository = disabled.GetRepositoryName(nameof(DisabledDeletion_DoesNotDeleteManifest));
        ImageSeed image = await disabled.PutImageAsync(repository, "keep", []);
        using StringWriter output = new();
        using StringWriter error = new();
        Command command = tagOnly
            ? new LiveTagDeleteCommand(disabled.CreateClientFactory(), output, error)
            : new LiveManifestDeleteCommand(disabled.CreateClientFactory(), output, error);

        Assert.Equal(1, await InvokeAsync(command, $"{disabled.Registry}/{repository}:keep"));

        using RegistryClient client = disabled.CreateClient();
        Assert.True(await client.Manifests.ExistsAsync(repository, "keep"));
        Assert.True(await client.Manifests.ExistsAsync(repository, image.Manifest.Digest));
        Assert.Empty(output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task ManifestDeletion_MissingTargetIsFailure()
    {
        await fixture.EnsureInitializedAsync();
        string repository = fixture.GetRepositoryName(nameof(ManifestDeletion_MissingTargetIsFailure));
        using StringWriter output = new();
        using StringWriter error = new();
        ManifestDeleteCommand command = new LiveManifestDeleteCommand(fixture.CreateClientFactory(), output, error);

        Assert.Equal(1, await InvokeAsync(command, $"{fixture.Registry}/{repository}:missing"));

        Assert.Empty(output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticatedDeletion_UsesProductionFactoryAndRejectsInvalidCredentials(bool tagOnly)
    {
        await using AuthenticatedRegistryFixture authenticated = new();
        await authenticated.EnsureInitializedAsync();
        string repository = authenticated.GetRepositoryName(nameof(AuthenticatedDeletion_UsesProductionFactoryAndRejectsInvalidCredentials));
        ImageSeed image = await authenticated.PutImageAsync(repository, "private", []);
        string reference = $"{authenticated.Registry}/{repository}:private";
        using StringWriter output = new();
        using StringWriter error = new();
        AuthenticatedClientFactory invalidFactory = new(authenticated, "wrong-password");
        Command rejected = tagOnly
            ? new LiveTagDeleteCommand(invalidFactory, output, error)
            : new LiveManifestDeleteCommand(invalidFactory, output, error);

        Assert.Equal(1, await InvokeAsync(rejected, reference));
        Assert.Empty(output.ToString());
        Assert.NotEmpty(error.ToString());

        AuthenticatedClientFactory factory = new(authenticated, AuthenticatedRegistryFixture.Password);
        using IDockerRegistryClient client = await factory.GetClientAsync(authenticated.Registry);
        Assert.True(await client.Manifests.ExistsAsync(repository, image.Manifest.Digest));
        error.GetStringBuilder().Clear();
        Command accepted = tagOnly
            ? new LiveTagDeleteCommand(factory, output, error)
            : new LiveManifestDeleteCommand(factory, output, error);

        Assert.Equal(0, await InvokeAsync(accepted, reference));
        Assert.Equal(tagOnly, await client.Manifests.ExistsAsync(repository, image.Manifest.Digest));
        Assert.False(await client.Manifests.ExistsAsync(repository, "private"));
        Assert.Empty(error.ToString());
        Assert.Contains(tagOnly ? "Deleted tag" : "Deleted manifest", output.ToString());
    }

    private static async Task<int> InvokeAsync(Command command, string reference)
    {
        RecordingProcessTerminator terminator = new();
        ((IProcessTerminationAware)command).ProcessTerminator = terminator;
        int result = await command.Parse([reference, "--yes"]).InvokeAsync(
            new InvocationConfiguration(), TestContext.Current.CancellationToken);
        return terminator.ExitCode ?? result;
    }

    private sealed class LiveManifestDeleteCommand(
        IDockerRegistryClientFactory factory, TextWriter output, TextWriter error)
        : ManifestDeleteCommand(factory, output)
    {
        protected override TextWriter Error => error;
    }

    private sealed class LiveTagDeleteCommand(
        IDockerRegistryClientFactory factory, TextWriter output, TextWriter error)
        : TagDeleteCommand(factory, output)
    {
        protected override TextWriter Error => error;
    }

    private sealed class AuthenticatedClientFactory(AuthenticatedRegistryFixture fixture, string password)
        : IDockerRegistryClientFactory
    {
        public Task<IDockerRegistryClient> GetClientAsync(string? registry, CancellationToken cancellationToken = default)
        {
            Assert.Equal(fixture.Registry, registry);
            return new DockerRegistryClientFactory(new Credentials(password))
                .GetClientAsync(fixture.BaseUri.AbsoluteUri, cancellationToken);
        }
    }

    private sealed class Credentials(string password) : IEnvironmentVariableProvider
    {
        public string? GetVariable(string name) => name switch
        {
            "DREDGE_USERNAME" => AuthenticatedRegistryFixture.Username,
            "DREDGE_PASSWORD" => password,
            _ => null
        };
    }
}
