namespace Valleysoft.Dredge.Tests;

public class ImageNameTests
{
    [Theory]
    [InlineData(
        "ubuntu",
        null,
        "library/ubuntu",
        "latest",
        null,
        "library/ubuntu:latest")]
    [InlineData(
        "mthalman/dredge",
        null,
        "mthalman/dredge",
        "latest",
        null,
        "mthalman/dredge:latest")]
    [InlineData(
        "mthalman/dredge:v1",
        null,
        "mthalman/dredge",
        "v1",
        null,
        "mthalman/dredge:v1")]
    [InlineData(
        "ghcr.io/mthalman/dredge:v1",
        "ghcr.io",
        "mthalman/dredge",
        "v1",
        null,
        "ghcr.io/mthalman/dredge:v1")]
    [InlineData(
        "localhost:5000/dredge:v1",
        "localhost:5000",
        "dredge",
        "v1",
        null,
        "localhost:5000/dredge:v1")]
    [InlineData(
        "ghcr.io/mthalman/dredge@sha256:abcdef",
        "ghcr.io",
        "mthalman/dredge",
        null,
        "sha256:abcdef",
        "ghcr.io/mthalman/dredge@sha256:abcdef")]
    public void Parse_ReturnsExpectedComponents(
        string value,
        string? expectedRegistry,
        string expectedRepo,
        string? expectedTag,
        string? expectedDigest,
        string expectedString)
    {
        ImageName imageName = ImageName.Parse(value);

        Assert.Equal(expectedRegistry, imageName.Registry);
        Assert.Equal(expectedRepo, imageName.Repo);
        Assert.Equal(expectedTag, imageName.Tag);
        Assert.Equal(expectedDigest, imageName.Digest);
        Assert.Equal(expectedString, imageName.ToString());
    }

    [Theory]
    [InlineData(null, "repo", null, null, "repo")]
    [InlineData("registry.example", "repo", "tag", null, "registry.example/repo:tag")]
    [InlineData("registry.example", "repo", null, "digest", "registry.example/repo@digest")]
    [InlineData("registry.example", "repo", "tag", "digest", "registry.example/repo:tag")]
    public void ToString_ReturnsExpectedReference(
        string? registry,
        string repo,
        string? tag,
        string? digest,
        string expected)
    {
        ImageName imageName = new(registry, repo, tag, digest);

        Assert.Equal(expected, imageName.ToString());
    }
}
