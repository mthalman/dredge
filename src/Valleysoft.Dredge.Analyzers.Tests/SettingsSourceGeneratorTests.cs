using Microsoft.CodeAnalysis;

namespace Valleysoft.Dredge.Analyzers.Tests;

public class SettingsSourceGeneratorTests
{
    [Fact]
    public void OnlyExplicitlyMarkedTypesParticipate()
    {
        var compilation = GeneratorTestHelper.CreateCompilation("""
            using Valleysoft.Dredge;
            using System.Text.Json.Serialization;
            [GenerateSettings]
            public partial class Options
            {
                [JsonPropertyName("value")]
                public string Value { get; set; } = "";
            }
            public partial class UnmarkedSettings { }
            """);

        var driver = GeneratorTestHelper.Run(compilation, out var output);
        var source = Assert.Single(GeneratorTestHelper.Accessors(GeneratorTestHelper.Result(driver)));

        Assert.Contains("partial class Options", source.SourceText.ToString());
        GeneratorTestHelper.AssertCompiles(output);
    }

    [Theory]
    [InlineData("JsonName")]
    [InlineData("global::System.Text.Json.Serialization.JsonPropertyNameAttribute")]
    public void ResolvesJsonAttributesBySymbol(string attributeName)
    {
        var compilation = GeneratorTestHelper.CreateCompilation($$"""
            using Valleysoft.Dredge;
            using JsonName = System.Text.Json.Serialization.JsonPropertyNameAttribute;
            [GenerateSettings]
            internal partial class SampleSettings
            {
                [{{attributeName}}("json-name")]
                public string Value { get; set; } = "";
            }
            """);

        var driver = GeneratorTestHelper.Run(compilation, out var output);
        var source = Assert.Single(GeneratorTestHelper.Accessors(GeneratorTestHelper.Result(driver)));

        Assert.Contains("case \"json-name\":", source.SourceText.ToString());
        GeneratorTestHelper.AssertCompiles(output);
    }

    [Fact]
    public void SameShortNamesDoNotCollide()
    {
        var compilation = GeneratorTestHelper.CreateCompilation("""
            namespace First
            {
                [Valleysoft.Dredge.GenerateSettings]
                internal partial class SampleSettings { }
            }
            namespace Second
            {
                [Valleysoft.Dredge.GenerateSettings]
                internal partial class SampleSettings { }
            }
            """);

        var driver = GeneratorTestHelper.Run(compilation, out var output);
        var sources = GeneratorTestHelper.Accessors(GeneratorTestHelper.Result(driver));

        Assert.Equal(2, sources.Length);
        Assert.Equal(2, sources.Select(source => source.HintName).Distinct().Count());
        GeneratorTestHelper.AssertCompiles(output);
    }

    [Fact]
    public void NonPartialTargetProducesAnActionableDiagnostic()
    {
        var compilation = GeneratorTestHelper.CreateCompilation("""
            [Valleysoft.Dredge.GenerateSettings]
            internal class Options { }
            """);

        var driver = GeneratorTestHelper.Run(compilation, out _);
        var result = GeneratorTestHelper.Result(driver);
        var diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal("DRG001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("partial", diagnostic.GetMessage());
        Assert.True(diagnostic.Location.IsInSource);
        Assert.Empty(GeneratorTestHelper.Accessors(result));
    }
}
