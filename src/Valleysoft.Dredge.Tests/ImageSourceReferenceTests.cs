namespace Valleysoft.Dredge.Tests;

public class ImageSourceReferenceTests
{
    [Theory]
    [InlineData("docker:tag", false, "docker:tag")]
    [InlineData("registry:tag", false, "registry:tag")]
    [InlineData("localhost:5000/repo:tag", false, "localhost:5000/repo:tag")]
    [InlineData("registry://localhost:5000/repo:tag", false, "localhost:5000/repo:tag")]
    [InlineData("docker://repo:tag", true, "repo:tag")]
    [InlineData("docker://sha256:dev", true, "sha256:dev")]
    [InlineData("docker://sha256:abc", true, "sha256:abc")]
    [InlineData("docker://sha256:0123456789ab", true, "sha256:0123456789ab")]
    [InlineData("docker://sha256:Release-1", true, "sha256:Release-1")]
    [InlineData("docker://sha256:5000/repo:tag", true, "sha256:5000/repo:tag")]
    [InlineData("docker://localhost:5000/repo:tag", true, "localhost:5000/repo:tag")]
    public void Parse_PreservesSourceAndRawValue(string reference, bool daemon, string value)
    {
        ImageSourceReference result = ImageSourceReference.Parse(reference);
        Assert.Equal(daemon, result.IsDaemon);
        Assert.False(result.IsImageId);
        Assert.Equal(value, result.Value);
        Assert.Equal(reference, result.ToString());
        if (daemon)
        {
            Assert.Null(result.Registry);
            Assert.Equal(value, result.GetImageName().ToString());
            Assert.DoesNotContain("library/", result.GetImageName().Repo);
        }
        else
        {
            Assert.Equal(ImageName.Parse(value).Registry, result.Registry);
        }
    }

    [Fact]
    public void Parse_AcceptsFullImageIdWithoutRegistryNormalization()
    {
        string id = "sha256:" + new string('a', 64);
        ImageSourceReference result = ImageSourceReference.Parse("docker://" + id);
        Assert.True(result.IsImageId);
        Assert.Equal(id, result.Value);
        Assert.Equal(id, result.GetImageName().Repo);
        Assert.Null(result.GetImageName().Registry);
    }

    [Theory]
    [InlineData("")]
    [InlineData("docker://")]
    [InlineData("registry://")]
    [InlineData("oci://repo:tag")]
    [InlineData("http://registry/repo")]
    [InlineData("Docker://repo")]
    [InlineData("docker://../repo")]
    [InlineData("docker://repo:tag ")]
    [InlineData("docker://sha256:")]
    [InlineData("docker://sha256:bad+tag")]
    [InlineData("docker://sha256:dev ")]
    public void Parse_RejectsMalformedSources(string reference) =>
        Assert.ThrowsAny<ArgumentException>(() => ImageSourceReference.Parse(reference));
}
