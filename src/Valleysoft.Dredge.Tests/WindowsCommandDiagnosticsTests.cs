using System.CommandLine;
using System.CommandLine.Parsing;
using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using Valleysoft.Dredge.Commands.Image;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

public sealed class WindowsCommandDiagnosticsTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => cache.DisposeAsync();

    private static Mock<IDockerRegistryClientFactory> Factory()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(File("Files/License.txt", "license"), File("UtilityVM/Files/runtime", "runtime")),
            Layer(File("Hives/Software", "registry"),
                File("Files/ProgramData/value", "data", new() { ["MSWINDOWS.fileattr"] = "1056" }),
                File("Files/Other/unsupported", "unsupported", new() { ["MSWINDOWS.unknown"] = "1" }),
                File("Files/Streams/owner", "main"), File("Files/Streams/owner:secret", "stream")));
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        return factory;
    }

    [Theory]
    [InlineData("", false, false)]
    [InlineData("ProgramData", true, false)]
    [InlineData("Other", false, true)]
    [InlineData("Streams/owner", false, true)]
    [InlineData("", true, true)]
    public async Task ListingWarningsDescribeOnlySelectedUnsupportedEntries(
        string path, bool recursive, bool expectWarning)
    {
        Mock<IDockerRegistryClientFactory> factory = Factory();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using StringWriter output = new();
            using StringWriter error = new();
            IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(output)
            });
            TestLsCommand command = new(factory.Object, console, cache.Paths, error);
            List<string> args = [Name.ToString(), path, "--output", "json"];
            if (recursive)
            {
                args.Add("--recursive");
            }
            Assert.Equal(0, await command.Parse([.. args]).InvokeAsync(new(), Token));
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            Assert.NotEmpty(document.RootElement.EnumerateArray());
            Assert.Equal(expectWarning, error.ToString().Contains("Warning:", StringComparison.Ordinal));
            Assert.DoesNotContain("Layer ", error.ToString());
            Assert.DoesNotContain("runtime payloads", error.ToString());
            Assert.DoesNotContain("ACLs", error.ToString());
            Assert.DoesNotContain("reparse attributes", error.ToString());
            if (expectWarning)
            {
                Assert.Contains(path == "Other" ? "Other/unsupported" : "Streams/owner", error.ToString());
            }
        }
    }

    [Theory]
    [InlineData("text", false)]
    [InlineData("json", false)]
    [InlineData("text", true)]
    [InlineData("json", true)]
    public async Task MetadataControlsAreEscapedInDiagnosticsAndListingWarnings(string format, bool global)
    {
        const string key = "MSWINDOWS.\u001b]52;c;YXVkaXQtbWFya2Vy\u0007\u009b31m";
        const string escapedKey = @"MSWINDOWS.\u001b]52;c;YXVkaXQtbWFya2Vy\u0007\u009b31m";
        Dictionary<string, string> metadata = new() { [key] = "1" };
        byte[] layer = global
            ? Layer(new PaxGlobalExtendedAttributesTarEntry(metadata), File("Files/value", "data"))
            : Layer(File("Files/value", "data", metadata));
        LayerChanges changes = await ImageLayerScanner.ScanAsync(
            new MemoryStream(layer), new(0, "layer"), Token, ImageFileSystemSemantics.Windows);
        Assert.Contains(escapedKey, Assert.Single(changes.Entries).Windows!.UnsupportedReason);
        Assert.Contains(changes.Diagnostics, diagnostic => diagnostic.Message.Contains(escapedKey, StringComparison.Ordinal));
        Assert.All(changes.Diagnostics, diagnostic => Assert.DoesNotContain(diagnostic.Message, char.IsControl));

        Mock<IDockerRegistryClient> client = Client(layer);
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(item => item.GetClientAsync(Name.Registry, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using StringWriter output = new();
            using StringWriter error = new();
            IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(output)
            });
            TestLsCommand command = new(factory.Object, console, cache.Paths, error);
            Assert.Equal(0, await command.Parse([Name.ToString(), "value", "--output", format]).InvokeAsync(new(), Token));
            Assert.Equal($"Warning: value: Unsupported Windows metadata '{escapedKey}'.{Environment.NewLine}", error.ToString());
            if (format == "json")
            {
                using JsonDocument document = JsonDocument.Parse(output.ToString());
                Assert.Single(document.RootElement.EnumerateArray());
            }
        }
    }

    [Fact]
    public void TerminalEscapingCoversControlRangesAndPreservesOrdinaryText()
    {
        const string plain = "Unsupported Windows metadata 'MSWINDOWS.vendor.\u00e9'.";
        Assert.Same(plain, TerminalText.EscapeControls(plain));
        for (int value = 0; value <= char.MaxValue; value++)
        {
            char character = (char)value;
            if (!char.IsControl(character)) continue;
            string expected = $"before\\u{value:x4}after";
            string escaped = TerminalText.EscapeControls($"before{character}after");
            Assert.Equal(expected, escaped);
            Assert.DoesNotContain(escaped, char.IsControl);
            Assert.Equal(escaped, TerminalText.EscapeControls(escaped));
        }
    }

    [Fact]
    public async Task ReadingAndExtractingSupportedContentDoNotPrintUnrelatedDiagnostics()
    {
        Mock<IDockerRegistryClientFactory> factory = Factory();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using StringWriter error = new();
            using MemoryStream output = new();
            TestCatCommand cat = new(factory.Object, output, cache.Paths, error);
            Assert.Equal(0, await cat.Parse([Name.ToString(), "License.txt"]).InvokeAsync(new(), Token));
            Assert.Null(cat.ExitCode);
            Assert.Equal("license", Encoding.UTF8.GetString(output.ToArray()));
            Assert.Equal("", error.ToString());

            string destination = Path.Combine(cache.Root, $"export-{attempt}");
            TestExtractCommand extract = new(factory.Object, cache.Paths, error);
            Assert.Equal(0, await extract.Parse([Name.ToString(), "ProgramData", destination]).InvokeAsync(new(), Token));
            Assert.Null(extract.ExitCode);
            Assert.Equal("data", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "value"), Token));
            Assert.Equal("", error.ToString());
        }
    }

    [Fact]
    public async Task UnsupportedReadAndExtractionStillReportErrorsWithoutWriting()
    {
        Mock<IDockerRegistryClientFactory> factory = Factory();
        using StringWriter error = new();
        using MemoryStream output = new();
        TestCatCommand cat = new(factory.Object, output, cache.Paths, error);
        await cat.Parse([Name.ToString(), "Other/unsupported"]).InvokeAsync(new(), Token);
        Assert.Equal(1, cat.ExitCode);
        Assert.Empty(output.ToArray());
        Assert.Contains("unsupported", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Warning:", error.ToString());

        error.GetStringBuilder().Clear();
        string destination = Path.Combine(cache.Root, "rejected");
        TestExtractCommand extract = new(factory.Object, cache.Paths, error);
        await extract.Parse([Name.ToString(), "Streams", destination]).InvokeAsync(new(), Token);
        Assert.Equal(1, extract.ExitCode);
        Assert.False(Directory.Exists(destination));
        Assert.Contains("unsupported", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Warning:", error.ToString());
    }

    private sealed class TestLsCommand : LsCommand
    {
        private readonly TextWriter error;

        public TestLsCommand(IDockerRegistryClientFactory factory, IAnsiConsole console,
            IDredgePathProvider paths, TextWriter error) : base(factory, console, paths)
        {
            this.error = error;
        }

        protected override TextWriter Error => error;
    }

    private sealed class TestCatCommand : CatCommand
    {
        private readonly TextWriter error;

        public TestCatCommand(IDockerRegistryClientFactory factory, Stream output,
            IDredgePathProvider paths, TextWriter error) : base(factory, output, paths)
        {
            this.error = error;
        }

        public int? ExitCode { get; private set; }
        protected override TextWriter Error => error;
        protected override void Exit(int exitCode) => ExitCode = exitCode;
    }

    private sealed class TestExtractCommand : ExtractCommand
    {
        private readonly TextWriter error;

        public TestExtractCommand(IDockerRegistryClientFactory factory,
            IDredgePathProvider paths, TextWriter error) : base(factory, paths)
        {
            this.error = error;
        }

        public int? ExitCode { get; private set; }
        protected override TextWriter Error => error;
        protected override void Exit(int exitCode) => ExitCode = exitCode;
    }
}
