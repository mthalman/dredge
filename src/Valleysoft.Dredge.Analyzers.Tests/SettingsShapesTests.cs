namespace Valleysoft.Dredge.Analyzers.Tests;

public class SettingsShapesTests
{
    [Fact]
    public void MarkerAliasesWorkAndLookalikeMarkersDoNotOptIn() =>
        AssertValid("""
            using Generate = Valleysoft.Dredge.GenerateSettingsAttribute;
            [Generate]
            public partial class Options { }
            namespace Unrelated
            {
                public class GenerateSettingsAttribute : System.Attribute { }
                [GenerateSettings]
                public class UnmarkedSettings { }
            }
            """);

    [Theory]
    [InlineData("public")]
    [InlineData("internal")]
    public void GenericTargetsAndContainersCompile(string accessibility)
    {
        AssertValid($$"""
            namespace @namespace;
            public partial class Outer<T> where T : class, new()
            {
                [Valleysoft.Dredge.GenerateSettings]
                {{accessibility}} partial class Options<U> where U : unmanaged
                {
                    [System.Text.Json.Serialization.JsonPropertyName("value")]
                    public string? @event { get; private set; }
                }
            }
            """);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("protected")]
    [InlineData("private protected")]
    [InlineData("protected internal")]
    public void NestedAccessibilityIsPreserved(string accessibility) =>
        AssertValid($$"""
            public partial class Outer
            {
                [Valleysoft.Dredge.GenerateSettings]
                {{accessibility}} partial class Options { }
            }
            """);

    [Fact]
    public void StaticContainersAndGenericNestedBranchesCompile() =>
        AssertValid("""
            public static partial class Outer
            {
                [Valleysoft.Dredge.GenerateSettings]
                public partial class Options<T> where T : class
                {
                    [System.Text.Json.Serialization.JsonPropertyName("child")]
                    public Child<T>? Child { get; }
                }
                [Valleysoft.Dredge.GenerateSettings]
                public partial class Child<T> where T : class
                {
                    [System.Text.Json.Serialization.JsonPropertyName("value")]
                    public string? Value { get; set; }
                }
            }
            """, 2);

    [Fact]
    public void PartialDeclarationsIncludeOnlyOwnAnnotatedProperties()
    {
        var compilation = GeneratorTestHelper.CreateCompilation("""
            using System.Text.Json.Serialization;
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Options
            {
                private const string Name = "first";
                [JsonPropertyName(Name)]
                public string First { get; set; } = "";
                public int Ignored { get; set; }
                public class UnmarkedSettings
                {
                    [JsonPropertyName("descendant")]
                    public int InvalidIfSelected { get; set; }
                }
            }
            """, """
            public partial class Options
            {
                [System.Text.Json.Serialization.JsonPropertyName("second")]
                public string? Second { get; set; }
            }
            """);
        var driver = GeneratorTestHelper.Run(compilation, out var output);
        var result = GeneratorTestHelper.Result(driver);
        var source = Assert.Single(GeneratorTestHelper.Accessors(result)).SourceText.ToString();

        Assert.Empty(result.Diagnostics);
        Assert.Contains("case \"first\":", source);
        Assert.Contains("case \"second\":", source);
        Assert.DoesNotContain("descendant", source);
        Assert.DoesNotContain("Ignored", source);
        GeneratorTestHelper.AssertCompiles(output);
    }

    [Fact]
    public void LookalikeAttributesAreIgnored() =>
        AssertValid("""
            using System;
            namespace Example;
            public class JsonPropertyNameAttribute(string value) : Attribute
            {
                public string Value { get; } = value;
            }
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Options
            {
                [JsonPropertyName("not-a-setting")]
                public int NotASetting { get; set; }
            }
            """);

    [Fact]
    public void EscapedStringContentsArePreserved() =>
        AssertValid("""
            [Valleysoft.Dredge.GenerateSettings]
            public partial class @class
            {
                [System.Text.Json.Serialization.JsonPropertyName("\"quote\\slash\n\t\u263a")]
                public string @namespace { get; set; } = "";
            }
            """);

    [Fact]
    public void HintNamesDistinguishContainersNamespacesAndGenericArity() =>
        AssertValid("""
            namespace A
            {
                [Valleysoft.Dredge.GenerateSettings]
                public partial class B { }
                [Valleysoft.Dredge.GenerateSettings]
                public partial class B<T> { }
                public partial class Outer
                {
                    [Valleysoft.Dredge.GenerateSettings]
                    public partial class B { }
                }
            }
            namespace A_Outer
            {
                [Valleysoft.Dredge.GenerateSettings]
                public partial class B { }
            }
            namespace A.OuterSpace
            {
                [Valleysoft.Dredge.GenerateSettings]
                public partial class B { }
            }
            """, 5);

    [Fact]
    public void CyclicAndExpandingGenericReferencesDoNotRecurseDuringGeneration() =>
        AssertValid("""
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Node<T>
            {
                [System.Text.Json.Serialization.JsonPropertyName("next")]
                public Node<System.Collections.Generic.List<T>>? Next { get; set; }
            }
            """);

    [Fact]
    public void AccessorOverloadsDoNotConflict() =>
        AssertValid("""
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Options
            {
                public string GetProperty(string name) => name;
                public void SetProperty<T>(T value) { }
            }
            """);

    private static void AssertValid(string source, int expectedSources = 1)
    {
        var driver = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source), out var output);
        var result = GeneratorTestHelper.Result(driver);
        var sources = GeneratorTestHelper.Accessors(result);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(expectedSources, sources.Length);
        Assert.Equal(expectedSources, sources.Select(source => source.HintName).Distinct().Count());
        GeneratorTestHelper.AssertCompiles(output);
    }
}
