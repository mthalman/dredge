using Microsoft.CodeAnalysis;

namespace Valleysoft.Dredge.Analyzers.Tests;

public class SettingsDiagnosticsTests
{
    [Theory]
    [InlineData("public class Target { }", "DRG001")]
    [InlineData("public partial record Target;", "DRG002")]
    [InlineData("public partial struct Target { }", "DRG002")]
    [InlineData("public partial interface Target { }", "DRG002")]
    [InlineData("public static partial class Target { }", "DRG002")]
    [InlineData("file partial class Target { }", "DRG002")]
    [InlineData("public partial class Target : System.Exception { }", "DRG002")]
    public void UnsupportedTargetsHaveStableDiagnostics(string declaration, string id) =>
        AssertDiagnostic("[Valleysoft.Dredge.GenerateSettings]\n" + declaration, id);

    [Theory]
    [InlineData("[JsonPropertyName(null)] public string Value { get; set; } = \"\";", "DRG003")]
    [InlineData("[JsonPropertyName(\"\")] public string Value { get; set; } = \"\";", "DRG003")]
    [InlineData("[JsonPropertyName(\"a.b\")] public string Value { get; set; } = \"\";", "DRG003")]
    [InlineData("[JsonPropertyName] public string Value { get; set; } = \"\";", "DRG003")]
    [InlineData("[JsonPropertyName(12)] public string Value { get; set; } = \"\";", "DRG003")]
    [InlineData("[JsonPropertyName(Missing)] public string Value { get; set; } = \"\";", "DRG003")]
    [InlineData("[JsonPropertyName(\"a\"), JsonPropertyName(\"b\")] public string Value { get; set; } = \"\";", "DRG003")]
    [InlineData("[JsonPropertyName(\"a\")] public int Value { get; set; }", "DRG005")]
    [InlineData("[JsonPropertyName(\"a\")] public static string Value { get; set; } = \"\";", "DRG005")]
    [InlineData("[JsonPropertyName(\"a\")] public string this[int i] { get => \"\"; set { } }", "DRG005")]
    [InlineData("[JsonPropertyName(\"a\")] public string Value { get; } = \"\";", "DRG006")]
    [InlineData("[JsonPropertyName(\"a\")] public string Value { get; init; } = \"\";", "DRG006")]
    [InlineData("[JsonPropertyName(\"a\")] public string Value { set { } }", "DRG006")]
    [InlineData("public object GetProperty(System.Collections.Generic.Queue<string> path) => \"\";", "DRG007")]
    [InlineData("public void SetProperty(System.Collections.Generic.Queue<string> path, string value) { }", "DRG007")]
    [InlineData("public string GetProperty { get; set; } = \"\";", "DRG007")]
    public void InvalidMembersAreDiagnosed(string member, string id) =>
        AssertDiagnostic($$"""
            using System.Text.Json.Serialization;
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Target
            {
                {{member}}
            }
            """, id);

    [Theory]
    [InlineData("class Outer", "DRG001")]
    [InlineData("partial record Outer", "DRG002")]
    [InlineData("partial struct Outer", "DRG002")]
    [InlineData("file partial class Outer", "DRG002")]
    public void UnsupportedContainersAreDiagnosed(string container, string id) =>
        AssertDiagnostic($$"""
            {{container}}
            {
                [Valleysoft.Dredge.GenerateSettings]
                public partial class Target { }
            }
            """, id);

    [Fact]
    public void DuplicateNamesAcrossPartialDeclarationsAreDiagnosed() =>
        AssertDiagnostic("""
            using System.Text.Json.Serialization;
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Target
            {
                [JsonPropertyName("same")]
                public string First { get; set; } = "";
            }
            public partial class Target
            {
                [JsonPropertyName("same")]
                public string Second { get; set; } = "";
            }
            """, "DRG004");

    [Fact]
    public void DuplicateMarkersDoNotCauseDuplicateHintExceptions() =>
        AssertDiagnostic("""
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Target { }
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Target { }
            """, "DRG003");

    [Theory]
    [InlineData("GetProperty")]
    [InlineData("SetProperty")]
    public void GenericParametersCannotConflictWithGeneratedMembers(string name) =>
        AssertDiagnostic($$"""
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Target<{{name}}> { }
            """, "DRG007");

    [Fact]
    public void UnmarkedNestedClassIsNotASettingsBranch() =>
        AssertDiagnostic("""
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Target
            {
                [System.Text.Json.Serialization.JsonPropertyName("child")]
                public ChildSettings Child { get; } = new();
            }
            public class ChildSettings { }
            """, "DRG005");

    [Fact]
    public void ExplicitInterfacePropertiesAreDiagnosed() =>
        AssertDiagnostic("""
            using System.Text.Json.Serialization;
            public interface IValue { string Value { get; set; } }
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Target : IValue
            {
                [JsonPropertyName("value")]
                string IValue.Value { get; set; } = "";
            }
            """, "DRG005");

    [Fact]
    public void InvalidNestedContractsSuppressDependentSources()
    {
        var compilation = GeneratorTestHelper.CreateCompilation("""
            using System.Text.Json.Serialization;
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Parent
            {
                [JsonPropertyName("child")]
                public Child Child { get; } = new();
            }
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Child
            {
                [JsonPropertyName("value")]
                public int Value { get; set; }
            }
            """);
        var driver = GeneratorTestHelper.Run(compilation, out _);
        var result = GeneratorTestHelper.Result(driver);

        Assert.Equal(["DRG005", "DRG008"], result.Diagnostics.Select(d => d.Id).Order().ToArray());
        Assert.Empty(GeneratorTestHelper.Accessors(result));
    }

    [Fact]
    public void DiagnosticLocationPointsToInvalidAttribute()
    {
        const string source = """
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Target
            {
                [System.Text.Json.Serialization.JsonPropertyName(null)]
                public string Value { get; set; } = "";
            }
            """;
        var driver = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source), out _);
        var diagnostic = Assert.Single(GeneratorTestHelper.Result(driver).Diagnostics);

        Assert.Equal("DRG003", diagnostic.Id);
        Assert.Equal("System.Text.Json.Serialization.JsonPropertyName(null)",
            source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    private static void AssertDiagnostic(string source, string id)
    {
        var driver = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source), out _);
        var result = GeneratorTestHelper.Result(driver);
        var diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal(id, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.True(diagnostic.Location.IsInSource);
        Assert.NotEmpty(diagnostic.GetMessage());
        Assert.Empty(GeneratorTestHelper.Accessors(result));
    }
}
