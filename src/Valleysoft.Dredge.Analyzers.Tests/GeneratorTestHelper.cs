using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;
using Valleysoft.Dredge.Analyzers;

namespace Valleysoft.Dredge.Analyzers.Tests;

internal static class GeneratorTestHelper
{
    internal static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12);
    private static readonly ImmutableArray<MetadataReference> References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToImmutableArray<MetadataReference>();

    internal static CSharpCompilation CreateCompilation(params string[] sources) =>
        CSharpCompilation.Create(
            $"GeneratorTests_{Guid.NewGuid():N}",
            sources.Select((source, index) => CSharpSyntaxTree.ParseText(
                source, ParseOptions, $"Input{index}.cs")),
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

    internal static GeneratorDriver CreateDriver() =>
        CSharpGeneratorDriver.Create(
            [new SettingsSourceGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));

    internal static GeneratorDriver Run(
        CSharpCompilation compilation, out Compilation output, GeneratorDriver? driver = null) =>
        (driver ?? CreateDriver()).RunGeneratorsAndUpdateCompilation(
            compilation, out output, out _);

    internal static GeneratorRunResult Result(GeneratorDriver driver)
    {
        GeneratorRunResult result = Assert.Single(driver.GetRunResult().Results);
        Assert.Null(result.Exception);
        return result;
    }

    internal static GeneratedSourceResult[] Accessors(GeneratorRunResult result) =>
        result.GeneratedSources
            .Where(source => source.HintName != "GenerateSettingsAttribute.g.cs")
            .ToArray();

    internal static void AssertCompiles(Compilation compilation) =>
        Assert.Empty(compilation.GetDiagnostics().Where(
            diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning));
}
