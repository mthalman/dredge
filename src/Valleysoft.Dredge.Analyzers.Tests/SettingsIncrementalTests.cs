using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Valleysoft.Dredge.Analyzers.Tests;

public class SettingsIncrementalTests
{
    private const string First = """
        [Valleysoft.Dredge.GenerateSettings]
        public partial class First
        {
            [System.Text.Json.Serialization.JsonPropertyName("first")]
            public string Value { get; set; } = "";
        }
        """;
    private const string Second = """
        [Valleysoft.Dredge.GenerateSettings]
        public partial class Second
        {
            [System.Text.Json.Serialization.JsonPropertyName("second")]
            public string Value { get; set; } = "";
        }
        """;

    [Fact]
    public void UnchangedRunReusesModelsAndSourceOutput()
    {
        var compilation = GeneratorTestHelper.CreateCompilation(First, Second);
        var driver = GeneratorTestHelper.Run(compilation, out _);
        var before = Sources(driver);
        driver = GeneratorTestHelper.Run(compilation, out var output, driver);

        Assert.Equal(before, Sources(driver));
        Assert.All(Reasons(driver), reason => Assert.Equal(IncrementalStepRunReason.Cached, reason));
        AssertSourceOutputsCached(driver);
        GeneratorTestHelper.AssertCompiles(output);
    }

    [Fact]
    public void UnrelatedEditDoesNotRegenerateAccessors()
    {
        var compilation = GeneratorTestHelper.CreateCompilation(First, Second, "public class Other { }");
        var driver = GeneratorTestHelper.Run(compilation, out _);
        var before = Sources(driver);
        compilation = ReplaceLast(compilation, "public class Other { public int Value { get; set; } }");
        driver = GeneratorTestHelper.Run(compilation, out var output, driver);

        Assert.Equal(before, Sources(driver));
        Assert.All(Reasons(driver), AssertReused);
        AssertSourceOutputsCached(driver);
        GeneratorTestHelper.AssertCompiles(output);
    }

    [Fact]
    public void TargetEditOnlyChangesItsOwnOutput()
    {
        var compilation = GeneratorTestHelper.CreateCompilation(First, Second);
        var driver = GeneratorTestHelper.Run(compilation, out _);
        var before = Sources(driver);
        compilation = ReplaceLast(compilation, Second.Replace("\"second\"", "\"renamed\""));
        driver = GeneratorTestHelper.Run(compilation, out var output, driver);

        Assert.Equal(before[0], Sources(driver)[0]);
        Assert.NotEqual(before[1], Sources(driver)[1]);
        AssertReused(Reasons(driver)[0]);
        Assert.Equal(IncrementalStepRunReason.Modified, Reasons(driver)[1]);
        GeneratorTestHelper.AssertCompiles(output);
    }

    [Fact]
    public void NestedContractInvalidationAndRecoveryUpdateDependents()
    {
        const string parent = """
            [Valleysoft.Dredge.GenerateSettings]
            public partial class Parent
            {
                [System.Text.Json.Serialization.JsonPropertyName("child")]
                public Second Child { get; } = new();
            }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(First, parent, Second);
        var driver = GeneratorTestHelper.Run(compilation, out _);
        Assert.Equal(3, Sources(driver).Length);

        compilation = ReplaceLast(compilation, Second.Replace(
            "public string Value { get; set; } = \"\";", "public int Value { get; set; }"));
        driver = GeneratorTestHelper.Run(compilation, out _, driver);
        var result = GeneratorTestHelper.Result(driver);
        Assert.Equal(["DRG005", "DRG008"], result.Diagnostics.Select(d => d.Id).Order().ToArray());
        Assert.Single(GeneratorTestHelper.Accessors(result));
        AssertReused(Reasons(driver)[0]);

        compilation = ReplaceLast(compilation, Second);
        driver = GeneratorTestHelper.Run(compilation, out var output, driver);
        Assert.Empty(GeneratorTestHelper.Result(driver).Diagnostics);
        Assert.Equal(3, Sources(driver).Length);
        GeneratorTestHelper.AssertCompiles(output);
    }

    [Fact]
    public void EditsInUnmarkedPartialDeclarationInvalidateItsTarget()
    {
        const string partial = """
            public partial class First
            {
                [System.Text.Json.Serialization.JsonPropertyName("extra")]
                public string Extra { get; set; } = "";
            }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(First, Second, partial);
        var driver = GeneratorTestHelper.Run(compilation, out _);
        var before = Sources(driver);
        compilation = ReplaceLast(compilation, partial.Replace("\"extra\"", "\"changed\""));
        driver = GeneratorTestHelper.Run(compilation, out var output, driver);

        Assert.NotEqual(before[0], Sources(driver)[0]);
        Assert.Equal(before[1], Sources(driver)[1]);
        Assert.Equal(IncrementalStepRunReason.Modified, Reasons(driver)[0]);
        AssertReused(Reasons(driver)[1]);
        GeneratorTestHelper.AssertCompiles(output);
    }

    [Fact]
    public void DiagnosticLocationsMoveWhenInputMoves()
    {
        const string source = "[Valleysoft.Dredge.GenerateSettings] public class Invalid { }";
        var compilation = GeneratorTestHelper.CreateCompilation(First, source);
        var driver = GeneratorTestHelper.Run(compilation, out _);
        int originalStart = Assert.Single(GeneratorTestHelper.Result(driver).Diagnostics).Location.SourceSpan.Start;

        compilation = ReplaceLast(compilation, "\n\n" + source);
        driver = GeneratorTestHelper.Run(compilation, out _, driver);

        Assert.Equal(originalStart + 2,
            Assert.Single(GeneratorTestHelper.Result(driver).Diagnostics).Location.SourceSpan.Start);
        AssertReused(Assert.Single(Reasons(driver)));
    }

    private static CSharpCompilation ReplaceLast(CSharpCompilation compilation, string source)
    {
        var tree = compilation.SyntaxTrees.Last();
        return compilation.ReplaceSyntaxTree(tree,
            CSharpSyntaxTree.ParseText(source, GeneratorTestHelper.ParseOptions, tree.FilePath));
    }

    private static string[] Sources(GeneratorDriver driver) =>
        GeneratorTestHelper.Accessors(GeneratorTestHelper.Result(driver))
            .Select(source => source.HintName + source.SourceText.ToString()).ToArray();

    private static IncrementalStepRunReason[] Reasons(GeneratorDriver driver) =>
        GeneratorTestHelper.Result(driver).TrackedSteps["SettingsModels"]
            .SelectMany(step => step.Outputs).Select(output => output.Reason).ToArray();

    private static void AssertReused(IncrementalStepRunReason reason) =>
        Assert.Contains(reason, new[] { IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged });

    private static void AssertSourceOutputsCached(GeneratorDriver driver) =>
        Assert.All(GeneratorTestHelper.Result(driver).TrackedOutputSteps.SelectMany(pair => pair.Value)
            .SelectMany(step => step.Outputs),
            output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
}
