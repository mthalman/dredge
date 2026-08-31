namespace Valleysoft.Dredge.Tests;

public class LinuxOsInfoTests
{
    [Fact]
    public void Parse_ReturnsAllKnownFields()
    {
        const string Content =
            """
            PRETTY_NAME="Test Linux 1.0"
            NAME="Test Linux"
            ID=test
            ID_LIKE="debian linux"
            VERSION="1.0 (Stable)"
            VERSION_ID="1.0"
            VERSION_CODENAME=stable
            BUILD_ID=build
            IMAGE_ID=image
            IMAGE_VERSION=2
            VARIANT=server
            VARIANT_ID=server
            HOME_URL="https://example.com"
            SUPPORT_URL="https://example.com/support"
            BUG_REPORT_URL="https://example.com/bugs"
            PRIVACY_POLICY_URL="https://example.com/privacy"
            CPE_NAME="cpe:/o:example:test:1.0"
            """;

        LinuxOsInfo result = LinuxOsInfo.Parse(Content);

        Assert.Equal("Test Linux 1.0", result.PrettyName);
        Assert.Equal("Test Linux", result.Name);
        Assert.Equal("test", result.Id);
        Assert.Equal(["debian", "linux"], result.IdLike!);
        Assert.Equal("1.0 (Stable)", result.Version);
        Assert.Equal("1.0", result.VersionId);
        Assert.Equal("stable", result.VersionCodeName);
        Assert.Equal("build", result.BuildId);
        Assert.Equal("image", result.ImageId);
        Assert.Equal("2", result.ImageVersion);
        Assert.Equal("server", result.Variant);
        Assert.Equal("server", result.VariantId);
        Assert.Equal("https://example.com", result.HomeUrl);
        Assert.Equal("https://example.com/support", result.SupportUrl);
        Assert.Equal("https://example.com/bugs", result.BugReportUrl);
        Assert.Equal("https://example.com/privacy", result.PrivacyPolicyUrl);
        Assert.Equal("cpe:/o:example:test:1.0", result.CpeName);
    }

    [Fact]
    public void Parse_WhenOptionalFieldsAreMissing_ReturnsNullProperties()
    {
        const string Content = "# generated file\r\nmalformed line\r\nID=test\r\n";

        LinuxOsInfo result = LinuxOsInfo.Parse(Content);

        Assert.Equal("test", result.Id);
        Assert.Null(result.PrettyName);
        Assert.Null(result.IdLike);
        Assert.Null(result.Version);
    }
}
