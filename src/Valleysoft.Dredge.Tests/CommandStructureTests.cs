namespace Valleysoft.Dredge.Tests;

using System.CommandLine;
using System.CommandLine.Parsing;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Commands.Manifest;
using Valleysoft.Dredge.Commands.Referrer;
using Valleysoft.Dredge.Commands.Repo;
using Valleysoft.Dredge.Commands.Settings;
using Valleysoft.Dredge.Commands.Tag;
using ReferrerGetOptions = Valleysoft.Dredge.Commands.Referrer.GetOptions;
using ReferrerInspectOptions = Valleysoft.Dredge.Commands.Referrer.InspectOptions;

public class CommandStructureTests
{
    [Fact]
    public void TopLevelCommands_RegisterExpectedSubcommands()
    {
        Mock<IDockerRegistryClientFactory> factory = new();

        Assert.Equal(
            ["compare", "inspect", "os", "save-layers", "dockerfile"],
            new ImageCommand(factory.Object).Subcommands.Select(command => command.Name));
        Assert.Equal(
            ["get", "digest", "resolve"],
            new ManifestCommand(factory.Object).Subcommands.Select(command => command.Name));
        Assert.Equal(
            ["list", "inspect", "get"],
            new ReferrerCommand(factory.Object).Subcommands.Select(command => command.Name));
        Assert.Equal(["list"], new RepoCommand(factory.Object).Subcommands.Select(command => command.Name));
        Assert.Equal(["list"], new TagCommand(factory.Object).Subcommands.Select(command => command.Name));
        Assert.Equal(
            ["open", "get", "set", "clear-cache"],
            new SettingsCommand().Subcommands.Select(command => command.Name));
    }

    [Fact]
    public void SaveLayersOptions_BindAllArgumentsAndOptions()
    {
        SaveLayersOptions options = Bind(
            new SaveLayersOptions(),
            "image:tag",
            "output",
            "--no-squash",
            "--layer-index",
            "3",
            "--os",
            "linux",
            "--os-version",
            "1",
            "--arch",
            "arm64");

        Assert.Equal("image:tag", options.Image);
        Assert.Equal("output", options.OutputPath);
        Assert.True(options.NoSquash);
        Assert.Equal(3, options.LayerIndex);
        Assert.Equal("linux", options.Os);
        Assert.Equal("1", options.OsVersion);
        Assert.Equal("arm64", options.Architecture);
    }

    [Fact]
    public void CompareFilesOptions_BindComparisonSettings()
    {
        CompareFilesOptions options = Bind(
            new CompareFilesOptions(),
            "base",
            "target",
            "--base-layer-index",
            "1",
            "--target-layer-index",
            "2");

        Assert.Equal("base", options.BaseImage);
        Assert.Equal("target", options.TargetImage);
        Assert.Equal(1, options.BaseLayerIndex);
        Assert.Equal(2, options.TargetLayerIndex);
        Assert.Equal(CompareFilesOutput.ExternalTool, options.OutputType);
    }

    [Fact]
    public void CompareLayersOptions_UseExpectedDefaults()
    {
        CompareLayersOptions options = Bind(new CompareLayersOptions(), "base", "target");

        Assert.Equal(CompareLayersOutput.SideBySide, options.OutputFormat);
        Assert.False(options.IsColorDisabled);
        Assert.False(options.IncludeHistory);
        Assert.False(options.IncludeCompressedSize);
    }

    [Fact]
    public void ReferrerInspectOptions_BindArgumentsAndOutput()
    {
        ReferrerInspectOptions options = Bind(
            new ReferrerInspectOptions(),
            "registry.example/repo:tag",
            "sha256:artifact",
            "--output",
            "Json");

        Assert.Equal("registry.example/repo:tag", options.Image);
        Assert.Equal("sha256:artifact", options.ArtifactDigest);
        Assert.Equal(ArtifactInspectOutput.Json, options.OutputFormat);
    }

    [Fact]
    public void ReferrerGetOptions_BindArgumentsAndOptions()
    {
        ReferrerGetOptions options = Bind(
            new ReferrerGetOptions(),
            "registry.example/repo:tag",
            "sha256:artifact",
            "--payload",
            "2",
            "--output",
            "artifact.json");

        Assert.Equal("registry.example/repo:tag", options.Image);
        Assert.Equal("sha256:artifact", options.ArtifactDigest);
        Assert.Equal("2", options.Payload);
        Assert.Equal("artifact.json", options.OutputPath);
    }

    [Fact]
    public void ReferrerArtifactOptions_UseNameArgument()
    {
        Command inspect = new("inspect");
        new ReferrerInspectOptions().SetCommandOptions(inspect);
        Command get = new("get");
        new ReferrerGetOptions().SetCommandOptions(get);

        Assert.Equal(["name", "artifact-digest"], inspect.Arguments.Select(argument => argument.Name));
        Assert.Equal(["name", "artifact-digest"], get.Arguments.Select(argument => argument.Name));
    }

    private static TOptions Bind<TOptions>(TOptions options, params string[] args)
        where TOptions : OptionsBase
    {
        Command command = new("test");
        options.SetCommandOptions(command);
        ParseResult parseResult = command.Parse(args);
        Assert.Empty(parseResult.Errors);
        options.SetParseResult(parseResult);
        return options;
    }
}
