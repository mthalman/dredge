using System.CommandLine;
using System.Formats.Tar;
using System.Text.Json;
using Valleysoft.Dredge.Commands.Image;
using static Valleysoft.Dredge.Tests.SemanticCompareFixture;

namespace Valleysoft.Dredge.Tests;

public sealed class CompareCommandTests
{
    public static bool LiveSmokeEnabled => Environment.GetEnvironmentVariable("DREDGE_COMPARE_LIVE_SMOKE") == "1";

    [Fact(Skip = "Load the fixed smoke archives and set DREDGE_COMPARE_LIVE_SMOKE=1.", SkipUnless = nameof(LiveSmokeEnabled))]
    public async Task LiveDaemonCommandSmoke()
    {
        using var fixture = new SemanticCompareFixture();
        using var output = new StringWriter();
        var command = fixture.Command(output);
        command.ImageSourceFactory = new ImageSourceFactory(new DockerRegistryClientFactory(),
            new TestAppSettingsStore(new AppSettings()), static connection => new DockerImageEngine(connection),
            Path.Combine(fixture.Root, "daemon-export"), indexStoreFactory: () => new LayerStore(Path.Combine(fixture.Root, "cache")));
        var parsed = command.Parse("docker://dredge-406-base:fixture docker://dredge-406-target:fixture --scope files --scope config --output json --os linux --arch amd64");
        Assert.Empty(parsed.Errors);
        await parsed.InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Terminator!.ExitCode);
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal(16, report.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal("dockerImageId", report.RootElement.GetProperty("base").GetProperty("identityKind").GetString());
        await File.WriteAllTextAsync(Path.Combine(CompareFixtureTests.RepositoryRoot(), ".compare-smoke", "report.json"),
            output.ToString(), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("files", "--output", "external-tool")]
    [InlineData("layers", "--output", "side-by-side")]
    [InlineData("metadata", "--output", "inline")]
    public void LegacySubcommandsRetainOperandsAndOptions(string subcommand, string option, string value)
    {
        var command = new CompareCommand(Mock.Of<IDockerRegistryClientFactory>());
        var parsed = command.Parse([subcommand, "base:one", "target:two", option, value]);
        Assert.Empty(parsed.Errors);
        Assert.Equal(subcommand, parsed.CommandResult.Command.Name);
        var child = Assert.IsAssignableFrom<Commands.CommandWithOptions<CompareFilesOptions>>(command.Subcommands.First(s => s.Name == "files"));
        if (subcommand == "files")
        {
            child.Options.SetParseResult(parsed);
            Assert.Equal("base:one", child.Options.BaseImage);
            Assert.Equal("target:two", child.Options.TargetImage);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("files")]
    [InlineData("layers")]
    [InlineData("metadata")]
    [InlineData("semantic")]
    public async Task HelpShowsOperandsOnlyForTheExecutingCommand(string? subcommand)
    {
        var command = new CompareCommand(Mock.Of<IDockerRegistryClientFactory>());
        RootCommand root = new();
        Command image = new("image");
        root.Subcommands.Add(image);
        image.Subcommands.Add(command);
        using StringWriter output = new();
        var parsed = root.Parse(subcommand is null ? ["image", "compare", "--help"] : ["image", "compare", subcommand, "--help"]);
        Assert.Equal(0, await parsed.InvokeAsync(new InvocationConfiguration { Output = output }, TestContext.Current.CancellationToken));
        string help = output.ToString();
        Assert.DoesNotContain("[<base>", help);
        if (subcommand is null)
        {
            Assert.Contains("semantic", help);
            Assert.DoesNotContain("--scope", help);
        }
        else
        {
            Assert.Contains("<base>", help);
            Assert.Contains("--output", help);
        }
        Assert.Single(help.Split('\n'), static line => line.Contains("--help", StringComparison.Ordinal));
        Assert.All(command.Arguments, static argument => Assert.False(argument.Hidden));
    }

    [Theory]
    [InlineData("")]
    [InlineData("base")]
    [InlineData("base target --scope unknown")]
    [InlineData("base target --limit -1")]
    [InlineData("base target --ignore-path ../etc/**")]
    [InlineData("base target --ignore-path !etc/**")]
    [InlineData("base target --docker-context one --docker-host two")]
    public void InvalidUnifiedOptionsFailParsing(string arguments)
    {
        var command = new SemanticCompareCommand(Mock.Of<IDockerRegistryClientFactory>());
        Assert.NotEmpty(command.Parse(arguments).Errors);
    }

    [Fact]
    public void SelectionsAreRepeatedDeduplicatedAndOrdered()
    {
        var command = new SemanticCompareCommand(Mock.Of<IDockerRegistryClientFactory>());
        var parsed = command.Parse("base target --scope layers --scope files --scope layers --ignore-path /a/** --ignore-path /a/** --limit 0");
        Assert.Empty(parsed.Errors);
        command.Options.SetParseResult(parsed);
        Assert.Equal(["files", "layers"], command.Options.Scopes);
        Assert.Equal(["/a/**"], command.Options.IgnorePaths);
        Assert.Equal(0, command.Options.Limit);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AllSourcePairsReportPinnedIdentityAndCleanUp(bool oldDaemon, bool newDaemon)
    {
        using var fixture = new SemanticCompareFixture();
        string old = oldDaemon ? "docker://base:one" : "base:one";
        string current = newDaemon ? "docker://target:two" : "target:two";
        byte[] layer = Layer(new TarItem("hello", "world"));
        fixture.Images[old] = Image(layer);
        fixture.Images[current] = Image(newDaemon ? Uncompress(layer) : layer);
        var report = await fixture.CompareAsync(old, current, "--scope", "files");
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(oldDaemon ? "dockerImageId" : "registryManifestDigest", report.Base.IdentityKind);
        Assert.Equal(newDaemon ? "dockerImageId" : "registryManifestDigest", report.Target.IdentityKind);
        Assert.Equal("linux/amd64", report.Base.Platform);
        Assert.Equal(2, fixture.Disposals);
        Assert.Equal(fixture.Reads, fixture.StreamDisposals);
        Assert.DoesNotContain(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories), static path => path.EndsWith(".scratch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MetadataScopeDoesNotAcquireLayerContentAndUnknownFieldsRemainVisible()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = new([], Configuration([], extra: ",\"custom\":{}"));
        fixture.Images["target"] = new([], Configuration([], extra: ",\"custom\":[]"));
        var report = await fixture.CompareAsync("base", "target", "--scope", "config");
        Assert.Equal(2, report.ExitCode);
        Assert.Single(report.Config);
        Assert.Equal(0, fixture.Reads);
        Assert.All(fixture.Requirements, static r => Assert.Equal(ImageSourceRequirements.Metadata, r));
        Assert.Equal("notRequested", report.Sections["files"].Coverage);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task IncompatiblePlatformsKeepMetadataFactsAndFailCompletion()
    {
        using var fixture = new SemanticCompareFixture();
        byte[] layer = Layer(new TarItem("file", "value"));
        fixture.Images["base"] = Image(layer);
        fixture.Images["target"] = new([layer], Configuration([layer], architecture: "arm64"));
        var report = await fixture.CompareAsync("base", "target");
        Assert.Equal(1, report.ExitCode);
        Assert.Null(report.AreEqual);
        Assert.Equal("incompatible", report.Sections["files"].Coverage);
        Assert.NotEmpty(report.Config);
    }

    [Fact]
    public async Task IncompatibleFileAndPackageScopesDoNotReadLayers()
    {
        using var fixture = new SemanticCompareFixture();
        byte[] layer = Layer(new TarItem("file", "value"));
        fixture.Images["base"] = Image(layer);
        fixture.Images["target"] = new([layer], Configuration([layer], architecture: "arm64"));
        var report = await fixture.CompareAsync("base", "target", "--scope", "files", "--scope", "packages", "--scope", "config");
        Assert.Equal("incompatible", report.Sections["files"].Coverage);
        Assert.Equal("incompatible", report.Sections["packages"].Coverage);
        Assert.NotEmpty(report.Config);
        Assert.Equal(0, fixture.Reads);
    }

    [Fact]
    public async Task IncompatiblePlatformsStillLoadLayersForSizes()
    {
        using var fixture = new SemanticCompareFixture();
        byte[] layer = Layer(new TarItem("file", "value"));
        fixture.Images["base"] = Image(layer);
        fixture.Images["target"] = new([layer], Configuration([layer], architecture: "arm64"));
        var report = await fixture.CompareAsync("base", "target", "--scope", "files", "--scope", "sizes");
        Assert.Equal("incompatible", report.Sections["files"].Coverage);
        Assert.Equal("available", report.Sections["sizes"].Coverage);
        Assert.True(fixture.Reads > 0);
    }

    [Theory]
    [InlineData(false, "available", 0)]
    [InlineData(true, "partial", 1)]
    public async Task WindowsSizeCoverageDistinguishesMetadataWarningFromExcludedPayload(
        bool hasExcludedPayload, string coverage, int exitCode)
    {
        using var fixture = new SemanticCompareFixture();
        TarItem[] items = hasExcludedPayload
            ? [new("Files/file", "data"), new("Hives/registry", "excluded")]
            : [new("Files/file", "data")];
        byte[] layer = Layer(items);
        fixture.Images["base"] = new([layer], Configuration([layer], os: "windows"));
        fixture.Images["target"] = fixture.Images["base"];
        var report = await fixture.CompareAsync("base", "target", "--scope", "sizes");
        Assert.Equal(exitCode, report.ExitCode);
        Assert.Equal(coverage, report.Sections["sizes"].Coverage);
        Assert.Equal(coverage, report.Sizes.Single(static s => s.Name == "effectiveLiveContentBytes").Coverage);
    }

    [Fact]
    public async Task DaemonSizeCoverageIsPartialEvenWithKnownDifferences()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new TarItem("file", "one")));
        fixture.Images["docker://target"] = Image(Uncompress(Layer(new TarItem("file", "two"))));
        var report = await fixture.CompareAsync("base", "docker://target");
        Assert.Equal(1, report.ExitCode);
        Assert.False(report.Complete);
        Assert.Null(report.Sections["sizes"].AreEqual);
        Assert.Single(report.Files);
        Assert.Contains(report.Sizes, static s => s.Name == "registryCompressedLayerBytes" && s.Coverage == "unavailable");
        Assert.NotNull(report.Sizes.Single(static s => s.Name == "registryCompressedLayerBytes").BaseBytes);
    }

    [Fact]
    public async Task JsonIsFullStableVersionedAndIndependentOfTerminalLimit()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(Enumerable.Range(0, 105).Select(i => new TarItem($"dir/{i:D3}", "old")).ToArray()));
        fixture.Images["target"] = Image(Layer(Enumerable.Range(0, 105).Reverse().Select(i => new TarItem($"dir/{i:D3}", "new")).ToArray()));
        var first = await fixture.CompareAsync("base", "target", "--scope", "files", "--output", "json", "--limit", "0");
        var second = await fixture.CompareAsync("base", "target", "--scope", "files", "--output", "json", "--limit", "1");
        string json = JsonHelper.Serialize(first);
        Assert.Equal(json, JsonHelper.Serialize(second));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(105, document.RootElement.GetProperty("files").GetArrayLength());
        JsonElement file = document.RootElement.GetProperty("files")[0];
        Assert.True(file.TryGetProperty("base", out _));
        Assert.True(file.TryGetProperty("target", out _));
        Assert.True(file.TryGetProperty("basePackages", out _));
        Assert.True(file.TryGetProperty("targetPackages", out _));
        Assert.False(file.TryGetProperty("before", out _));
        Assert.False(file.TryGetProperty("after", out _));
        Assert.DoesNotContain(fixture.Root, json, StringComparison.OrdinalIgnoreCase);
        using var text = new StringWriter();
        CompareTextRenderer.Write(text, first, 1);
        Assert.Contains("Showing 1 of 105 differences", text.ToString());
        Assert.DoesNotContain("/dir/001", text.ToString());
    }

    [Fact]
    public async Task TextOutputOrdersPackageNamesBeforeScopes()
    {
        static string Status(string version) =>
            string.Join("\n\n", new[] { ("zeta", "all"), ("alpha", "amd64"), ("alpha", "all"), ("beta", "amd64") }
                .Select(package =>
                    $"Package: {package.Item1}\nStatus: install ok installed\nArchitecture: {package.Item2}\nVersion: {version}")) + "\n";

        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new TarItem("var/lib/dpkg/status", Status("1"))));
        fixture.Images["target"] = Image(Layer(new TarItem("var/lib/dpkg/status", Status("2"))));
        CompareResult result = await fixture.CompareAsync("base", "target", "--scope", "packages");

        static string[] Render(CompareResult result, int limit)
        {
            using StringWriter text = new();
            CompareTextRenderer.Write(text, result, limit);
            return text.ToString().Split('\n')
                .Select(static line => line.Trim())
                .Where(static line => line.StartsWith("~ ", StringComparison.Ordinal))
                .ToArray();
        }

        string[] expected =
        [
            "~ alpha [all] 1 -> 2",
            "~ alpha [amd64] 1 -> 2",
            "~ beta [amd64] 1 -> 2",
            "~ zeta [all] 1 -> 2"
        ];
        Assert.Equal(expected, Render(result, 10));
        Assert.Equal(expected, Render(result with { Packages = result.Packages.Reverse().ToArray() }, 10));
        Assert.Equal(expected[..2], Render(result, 2));

        using JsonDocument packageJson = JsonDocument.Parse(JsonHelper.Serialize(result));
        JsonElement package = packageJson.RootElement.GetProperty("packages")[0];
        Assert.True(package.TryGetProperty("base", out _));
        Assert.True(package.TryGetProperty("target", out _));
        Assert.False(package.TryGetProperty("before", out _));
        Assert.False(package.TryGetProperty("after", out _));

        CompareResult withSizes = result with
        {
            Sizes = [new("registryCompressedLayerBytes", "available", 1024, 2048, 1024, false)]
        };
        using JsonDocument sizeJson = JsonDocument.Parse(JsonHelper.Serialize(withSizes));
        JsonElement size = sizeJson.RootElement.GetProperty("sizes")[0];
        Assert.Equal(1024, size.GetProperty("baseBytes").GetInt64());
        Assert.Equal(2048, size.GetProperty("targetBytes").GetInt64());
        Assert.False(size.TryGetProperty("beforeBytes", out _));
        Assert.False(size.TryGetProperty("afterBytes", out _));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void TextOutputGroupsChangesAndSharesLimitAcrossKinds(string newLine)
    {
        static ComparePackageDifference Package(string name, string kind, string? before, string? after) =>
            new("dpkg", "system/all", name, kind,
                before is null ? null : new("dpkg", "system/all", name, [before], []),
                after is null ? null : new("dpkg", "system/all", name, [after], []),
                true);

        CompareResult result = new(
            new("base", "registry", "sha256:1111111111111111111111111111111111111111111111111111111111111111",
                "registryManifestDigest", null, "linux/amd64"),
            new("target", "registry", "sha256:2222222222222222222222222222222222222222222222222222222222222222",
                "registryManifestDigest", null, "linux/amd64"),
            new(["packages"], [], false, "posix"),
            true,
            false,
            2,
            new Dictionary<string, CompareSection>
            {
                ["files"] = new("notRequested", null, 0, 0, 0),
                ["packages"] = new("available", false, 2, 2, 2),
                ["config"] = new("notRequested", null, 0, 0, 0),
                ["layers"] = new("notRequested", null, 0, 0, 0),
                ["sizes"] = new("notRequested", null, 0, 0, 0)
            },
            [],
            [],
            [
                Package("add-a", "added", null, "1"),
                Package("add-b", "added", null, "2"),
                Package("remove-a", "removed", "1", null),
                Package("remove-b", "removed", "2", null),
                Package("change-a", "changed", "1", "2"),
                Package("change-b", "changed", "2", "3")
            ],
            [
                new("npm", "base", "notDetected", 0, 0, 0),
                new("npm", "target", "notDetected", 0, 0, 0),
                new("dpkg", "base", "available", 1, 1, 0),
                new("dpkg", "target", "available", 1, 1, 0)
            ],
            [],
            [],
            [],
            [],
            [],
            0);

        using StringWriter text = new();
        CompareTextRenderer.Write(text, result, 3, isColorDisabled: true);
        string output = text.ToString();

        Assert.Contains("Status: Different", output);
        Assert.Contains("Packages  +2 -2 ~2", output);
        Assert.Contains("sha256:1111111111111111111111111111111111111111111111111111111111111111", output);
        Assert.Contains("sha256:2222222222222222222222222222222222222222222222222222222222222222", output);
        Assert.DoesNotContain("notRequested", output);
        Assert.DoesNotContain("notDetected", output);
        Assert.DoesNotContain("registryManifestDigest", output);
        Assert.DoesNotContain("exit 2", output);
        Assert.Contains("+ add-a [all] 1", output);
        Assert.Contains("- remove-a [all] 1", output);
        Assert.Contains("~ change-a [all] 1 -> 2", output);
        Assert.DoesNotContain("add-b", output);
        Assert.DoesNotContain("remove-b", output);
        Assert.DoesNotContain("change-b", output);
        Assert.Contains("... 1 more added", output);
        Assert.Contains("... 1 more removed", output);
        Assert.Contains("... 1 more changed", output);
        Assert.Contains("Showing 3 of 6 differences", output);
        Assert.True(output.IndexOf("\u001b[", StringComparison.Ordinal) < 0);

        using StringWriter noDetails = new();
        CompareTextRenderer.Write(noDetails, result, 0);
        string summaryOnly = noDetails.ToString();
        Assert.Contains("Added (2)", summaryOnly);
        Assert.Contains("... 2 more added", summaryOnly);
        Assert.Contains("Removed (2)", summaryOnly);
        Assert.Contains("... 2 more removed", summaryOnly);
        Assert.Contains("Changed (2)", summaryOnly);
        Assert.Contains("... 2 more changed", summaryOnly);
        Assert.DoesNotContain("+ add-a", summaryOnly);
        Assert.Contains("Showing 0 of 6 differences", summaryOnly);

        CompareResult incomplete = result with
        {
            Complete = false,
            AreEqual = null,
            ExitCode = 1,
            Sections = result.Sections.ToDictionary(
                static pair => pair.Key,
                pair => pair.Key == "packages" ? new("partial", null, 2, 2, 2) : pair.Value),
            Diagnostics = [new("packageCoverageIncomplete", "target", "packages", "dpkg", "Package inventory is incomplete.")],
            PackageCoverage = [.. result.PackageCoverage, new("dpkg", "target", "partial", 2, 1, 1)]
        };
        using StringWriter incompleteText = new() { NewLine = newLine };
        CompareTextRenderer.Write(incompleteText, incomplete, 3, isColorDisabled: true);
        string incompleteOutput = incompleteText.ToString();
        Assert.Contains("Coverage", incompleteOutput);
        Assert.Contains("dpkg target: Partial (1/2 parsed, 1 failed)", incompleteOutput);
        Assert.Contains("Warning: Packages target /dpkg: Package inventory is incomplete.", incompleteOutput);
        Assert.True(incompleteOutput.IndexOf("Coverage", StringComparison.Ordinal) <
            incompleteOutput.IndexOf($"{newLine}Packages{newLine}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TextHeadersRetainFullDigestsForLongImageReferences()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new TarItem("file", "data")));
        fixture.Images["target"] = fixture.Images["base"];
        CompareResult result = await fixture.CompareAsync("base", "target", "--scope", "files");
        string host = string.Join(".", Enumerable.Repeat(new string('a', 60), 4));
        string repository = $"{new string('b', 120)}/{new string('c', 120)}";
        string reference = $"{host}/{repository}:{new string('d', 128)}";
        _ = ImageName.Parse(reference);
        result = result with
        {
            Base = result.Base with { Requested = reference },
            Target = result.Target with { Requested = reference }
        };
        using StringWriter text = new();
        CompareTextRenderer.Write(text, result, 0);
        string[] lines = text.ToString().Split(Environment.NewLine);
        Assert.Contains(result.Base.Identity, lines[0]);
        Assert.Contains(result.Target.Identity, lines[1]);
        Assert.Contains(reference, lines[0]);
    }

    [Fact]
    public async Task InvocationWritesOneReportAndUsesDifferenceExitCode()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new TarItem("file", "old")));
        fixture.Images["target"] = Image(Layer(new TarItem("file", "new")));
        using StringWriter output = new();
        var parent = new CompareCommand(Mock.Of<IDockerRegistryClientFactory>());
        parent.Subcommands.Remove(parent.Subcommands.Single(static command => command.Name == "semantic"));
        parent.Subcommands.Add(fixture.Command(output));
        int code = await parent.Parse("semantic base target --scope files --output json").InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Equal(2, fixture.Terminator!.ExitCode);
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal(2, report.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void SemanticIsRegisteredAsSubcommandWithRequiredOperands()
    {
        var command = new CompareCommand(Mock.Of<IDockerRegistryClientFactory>());
        Assert.IsType<SemanticCompareCommand>(command.Subcommands.Single(static child => child.Name == "semantic"));
        Assert.Empty(command.Parse("semantic docker://candidate:dev registry://example.com/app:latest --scope files").Errors);
        Assert.NotEmpty(command.Parse("semantic").Errors);
        Assert.NotEmpty(command.Parse("semantic base").Errors);
        Assert.NotEmpty(command.Parse("base target --scope files").Errors);
    }

    [Fact]
    public async Task AcquisitionFailureDisposesOtherOperandAndDoesNotEmitSuccess()
    {
        using var fixture = new SemanticCompareFixture { FailTarget = true };
        fixture.Images["base"] = Image(Layer(new TarItem("file", "data")));
        using StringWriter output = new();
        int code = await fixture.Command(output).Parse("base target --output json").InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Equal(1, fixture.Terminator!.ExitCode);
        Assert.Equal(1, fixture.Disposals);
        Assert.DoesNotContain("\"schemaVersion\"", output.ToString());
    }
}
