using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Valleysoft.Dredge.Analyzers;

namespace Valleysoft.Dredge.Analyzers.Tests;

public class OutputOptionAnalyzerTests
{
    [Fact]
    public async Task DirectOutputOptionIsDiagnosed()
    {
        ImmutableArray<Diagnostic> diagnostics = await Analyze("""
            namespace System.CommandLine
            {
                public class Option<T>
                {
                    public Option(string name) { }
                }
            }

            public class CommandOptions
            {
                private readonly System.CommandLine.Option<string> output =
                    new("--output");
            }
            """);
        Diagnostic diagnostic = Assert.Single(diagnostics);

        Assert.Equal("DRG009", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("\"--output\"", diagnostic.Location.SourceTree!.GetText()
            .ToString(diagnostic.Location.SourceSpan));
    }

    [Fact]
    public async Task OutputOptionAliasIsDiagnosed()
    {
        ImmutableArray<Diagnostic> diagnostics = await Analyze("""
            namespace System.CommandLine
            {
                public class Option<T>
                {
                    public Option(string name, params string[] aliases) { }
                    public System.Collections.Generic.ICollection<string> Aliases { get; } =
                        new System.Collections.Generic.List<string>();
                }
            }

            public class CommandOptions
            {
                private readonly System.CommandLine.Option<string> output =
                    new("--format", "--output");

                public CommandOptions()
                {
                    output.Aliases.Add("--output");
                }
            }
            """);

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("DRG009", diagnostic.Id));
    }

    [Fact]
    public async Task OutputOptionCollectionInitializerAliasIsDiagnosed()
    {
        ImmutableArray<Diagnostic> diagnostics = await Analyze("""
            namespace System.CommandLine
            {
                public class Option<T>
                {
                    public Option(string name) { }
                    public System.Collections.Generic.ICollection<string> Aliases { get; } =
                        new System.Collections.Generic.List<string>();
                }
            }

            public class CommandOptions
            {
                private readonly System.CommandLine.Option<string> output =
                    new("--format") { Aliases = { "--output" } };
            }
            """);

        Assert.Equal("DRG009", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task FileDestinationOptionUsesDedicatedWrapper()
    {
        ImmutableArray<Diagnostic> diagnostics = await Analyze("""
            namespace System.CommandLine
            {
                public class Option<T>
                {
                    protected Option(string name) { }
                }
            }

            namespace Valleysoft.Dredge.Commands
            {
                internal sealed class CliOutputPathOption : System.CommandLine.Option<string>
                {
                    public CliOutputPathOption(string description) : base("--output") { }
                }

                public class CommandOptions
                {
                    private readonly CliOutputPathOption output =
                        new("File path for the payload");
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CustomOutputOptionSubclassIsDiagnosed()
    {
        ImmutableArray<Diagnostic> diagnostics = await Analyze("""
            namespace System.CommandLine
            {
                public class Option<T>
                {
                    protected Option(string name) { }
                }
            }

            public sealed class CustomOutputOption : System.CommandLine.Option<string>
            {
                public CustomOutputOption() : base("--output") { }
            }
            """);
        Diagnostic diagnostic = Assert.Single(diagnostics);

        Assert.Equal("DRG009", diagnostic.Id);
    }

    [Fact]
    public async Task CustomOutputOptionAliasIsDiagnosed()
    {
        ImmutableArray<Diagnostic> diagnostics = await Analyze("""
            namespace System.CommandLine
            {
                public class Option<T>
                {
                    protected Option(string name, params string[] aliases) { }
                }
            }

            public sealed class CustomOutputOption : System.CommandLine.Option<string>
            {
                public CustomOutputOption() : base("--format", "--output") { }
            }
            """);

        Assert.Equal("DRG009", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task OtherOptionsAreNotDiagnosed()
    {
        ImmutableArray<Diagnostic> diagnostics = await Analyze("""
            namespace System.CommandLine
            {
                public class Option<T>
                {
                    public Option(string name) { }
                }
            }

            public class CommandOptions
            {
                private readonly System.CommandLine.Option<string> config =
                    new("--config");
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task RawJsonOutputOptionIsDiagnosedOutsideTypedHelper()
    {
        ImmutableArray<Diagnostic> diagnostics = await Analyze("""
            namespace System.CommandLine
            {
                public class Option<T>
                {
                    public Option(string name) { }
                }
            }

            namespace Valleysoft.Dredge.Commands
            {
                public sealed class JsonOutputOption : System.CommandLine.Option<string>
                {
                    public JsonOutputOption(string name) : base(name) { }
                }

                public sealed class CliOutputOption<T>
                {
                    private readonly JsonOutputOption option = new("--output");
                }

                public class CommandOptions
                {
                    private readonly JsonOutputOption output = new("--output");
                }
            }
            """);

        Assert.Equal("DRG009", Assert.Single(diagnostics).Id);
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source)
    {
        Compilation compilation = GeneratorTestHelper.CreateCompilation(source);
        return await compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new OutputOptionAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
    }
}
