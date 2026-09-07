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
        "REGISTRY/team/image:v1",
        "REGISTRY",
        "team/image",
        "v1",
        null,
        "REGISTRY/team/image:v1")]
    [InlineData(
        "09:5000/repo",
        "09:5000",
        "repo",
        "latest",
        null,
        "09:5000/repo:latest")]
    [InlineData(
        "09/repo",
        null,
        "09/repo",
        "latest",
        null,
        "09/repo:latest")]
    [InlineData(
        "ghcr.io/mthalman/dredge@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "ghcr.io",
        "mthalman/dredge",
        null,
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "ghcr.io/mthalman/dredge@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(
        "ghcr.io/mthalman/dredge@multihash+base58:QmRZxt2b1FVZPNqd8hsiykDL3TdBDeTSPX9Kv46HmX4Gx8",
        "ghcr.io",
        "mthalman/dredge",
        null,
        "multihash+base58:QmRZxt2b1FVZPNqd8hsiykDL3TdBDeTSPX9Kv46HmX4Gx8",
        "ghcr.io/mthalman/dredge@multihash+base58:QmRZxt2b1FVZPNqd8hsiykDL3TdBDeTSPX9Kv46HmX4Gx8")]
    [InlineData(
        "localhost/dredge",
        "localhost",
        "dredge",
        "latest",
        null,
        "localhost/dredge:latest")]
    [InlineData(
        "[2001:db8::1]:5000/team/image:Release-1",
        "[2001:db8::1]:5000",
        "team/image",
        "Release-1",
        null,
        "[2001:db8::1]:5000/team/image:Release-1")]
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
    [InlineData("", "image reference")]
    [InlineData(" ", "image reference")]
    [InlineData("repo/", "repository")]
    [InlineData("repo//image", "repository")]
    [InlineData("registry.example/team\n/app:v1", "repository")]
    [InlineData("Repo", "repository")]
    [InlineData("repo:", "tag")]
    [InlineData("repo:bad tag", "tag")]
    [InlineData("repo@", "digest")]
    [InlineData("repo@sha256", "digest")]
    [InlineData("repo@sha256\n:a", "digest")]
    [InlineData("repo@sha256:bad!", "digest")]
    [InlineData("repo@sha256:abcdef", "digest")]
    [InlineData("repo@sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "digest")]
    [InlineData("repo@sha512:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "digest")]
    [InlineData("repo@SHA256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "digest")]
    [InlineData("repo:tag@sha256:value", "both a tag and a digest")]
    [InlineData("registry.example:/repo", "registry")]
    [InlineData("registry.example:abc/repo", "registry")]
    [InlineData("registry.example:+5/repo", "registry")]
    [InlineData("registry.example: 5/repo", "registry")]
    [InlineData("registry.example:5 /repo", "registry")]
    [InlineData("registry.example:65536/repo", "registry")]
    [InlineData("registry..example/repo", "registry")]
    [InlineData("MY_HOST/repo", "registry")]
    [InlineData("[fe80::1%12]/repo", "registry")]
    [InlineData("[::ffff:192.0.2.1]/repo", "registry")]
    [InlineData("https://registry.example/repo", "registry")]
    public void TryParse_InvalidReference_ReturnsComponentError(
        string value,
        string expectedError)
    {
        bool succeeded = ImageName.TryParse(value, out ImageName? imageName, out string? error);

        Assert.False(succeeded);
        Assert.Null(imageName);
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ubuntu", null, "library/ubuntu")]
    [InlineData("owner/image", null, "owner/image")]
    [InlineData(
        "registry.example:5000/owner/image",
        "registry.example:5000",
        "owner/image")]
    public void TryParseRepository_ValidRepository_ReturnsImageName(
        string value,
        string? expectedRegistry,
        string expectedRepo)
    {
        bool succeeded = ImageName.TryParseRepository(
            value,
            out ImageName? imageName,
            out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(imageName);
        Assert.Equal(expectedRegistry, imageName.Registry);
        Assert.Equal(expectedRepo, imageName.Repo);
        Assert.Equal("latest", imageName.Tag);
        Assert.Null(imageName.Digest);
    }

    [Theory]
    [InlineData("ubuntu:latest")]
    [InlineData("owner/image@sha256:value")]
    public void TryParseRepository_TagOrDigest_ReturnsRepositoryError(string value)
    {
        bool succeeded = ImageName.TryParseRepository(
            value,
            out ImageName? imageName,
            out string? error);

        Assert.False(succeeded);
        Assert.Null(imageName);
        Assert.Contains("repository", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_InvalidReference_ThrowsComponentError()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => ImageName.Parse("registry.example:invalid/repo"));

        Assert.Contains("registry", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_DockerHubShorthandAtNormalizedLengthLimit_Succeeds()
    {
        string value = new('a', 247);

        bool succeeded = ImageName.TryParse(
            value,
            out ImageName? imageName,
            out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(imageName);
        Assert.Equal(255, imageName.Repo.Length);
    }

    [Fact]
    public void TryParse_DockerHubShorthandOverNormalizedLengthLimit_ReturnsRepositoryError()
    {
        string value = new('a', 248);

        bool succeeded = ImageName.TryParse(
            value,
            out ImageName? imageName,
            out string? error);

        Assert.False(succeeded);
        Assert.Null(imageName);
        Assert.Contains("repository", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("255", error, StringComparison.Ordinal);
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
