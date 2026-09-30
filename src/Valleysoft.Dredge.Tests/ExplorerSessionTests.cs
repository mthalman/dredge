using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands.Image;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerSessionTests
{
    [Fact]
    public void ResolvesBareTagAgainstBaselineRepository()
    {
        ImageName baseline = ImageName.Parse("registry.test/group/app:current");

        Assert.Equal("registry.test/group/app:previous",
            ExploreCommand.ResolveCompareImage(baseline, "previous").ToString());
        Assert.Equal("registry.other/group/app:previous",
            ExploreCommand.ResolveCompareImage(baseline, "registry.other/group/app:previous").ToString());
        Assert.Throws<ArgumentException>(() => ExploreCommand.ResolveCompareImage(baseline, " "));
    }

    [Fact]
    public void VerifiesEntireBaseLayerPrefix()
    {
        OciImageManifest target = Manifest("first", "second", "third");
        Assert.Equal(2, ExplorerSession.VerifyPrefix(target, Manifest("first", "second")));
        Assert.Throws<InvalidOperationException>(
            () => ExplorerSession.VerifyPrefix(target, Manifest("first", "different")));
        Assert.Throws<InvalidOperationException>(
            () => ExplorerSession.VerifyPrefix(target, Manifest("first", "second", "third", "fourth")));
    }

    private static OciImageManifest Manifest(params string[] digests) => new()
    {
        Config = new OciDescriptor { Digest = "config" },
        Layers = [.. digests.Select(static digest => new OciDescriptor
        {
            Digest = digest,
            Size = 1
        })]
    };
}
