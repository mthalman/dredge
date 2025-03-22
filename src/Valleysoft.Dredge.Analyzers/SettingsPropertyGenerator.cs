using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace Valleysoft.Dredge.Analyzers;

[Generator]
public class SettingsSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Register a syntax receiver that will be created for each generation pass
        IncrementalValuesProvider<ClassDeclarationSyntax> classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsSettingsClass(s),
                transform: static (ctx, _) => GetClassDeclaration(ctx))
            .Where(static m => m is not null)!;

        IncrementalValueProvider<(Compilation, ImmutableArray<ClassDeclarationSyntax>)> compilationAndClasses =
            context.CompilationProvider.Combine(classDeclarations.Collect());

        context.RegisterSourceOutput(compilationAndClasses, static (spc, source) => Execute(source.Item1, source.Item2, spc));
    }

    private static bool IsSettingsClass(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax classDeclaration && classDeclaration.Identifier.Text.EndsWith("Settings");
    }

    private static ClassDeclarationSyntax GetClassDeclaration(GeneratorSyntaxContext context)
    {
        return (ClassDeclarationSyntax)context.Node;
    }

    private static void Execute(Compilation compilation, ImmutableArray<ClassDeclarationSyntax> classes, SourceProductionContext context)
    {
        foreach (ClassDeclarationSyntax classDecl in classes)
        {
            SemanticModel semanticModel = compilation.GetSemanticModel(classDecl.SyntaxTree);
            INamedTypeSymbol classSymbol = semanticModel.GetDeclaredSymbol(classDecl)!;

            StringBuilder sourceBuilder = new();

            sourceBuilder.AppendLine("#nullable enable");
            sourceBuilder.AppendLine("using System;");
            sourceBuilder.AppendLine("using System.Collections.Generic;");
            sourceBuilder.AppendLine("using Newtonsoft.Json;");

            sourceBuilder.AppendLine($"namespace {classSymbol.ContainingNamespace}");
            sourceBuilder.AppendLine("{");
            sourceBuilder.AppendLine($"    internal partial class {classSymbol.Name}");
            sourceBuilder.AppendLine("    {");

            sourceBuilder.AppendLine(GetMethod(semanticModel, classDecl,
                "public void SetProperty(Queue<string> propertyPath, string value)",
                propertyName => $"{propertyName}.SetProperty(propertyPath, value);",
                propertyName => $"{propertyName} = value;",
                includeBreak: true));

            sourceBuilder.AppendLine(GetMethod(semanticModel, classDecl,
                "public object? GetProperty(Queue<string> propertyPath)",
                propertyName => $"return {propertyName}.GetProperty(propertyPath);",
                propertyName => $"return {propertyName};",
                includeBreak: false));

            sourceBuilder.AppendLine("    }"); // end class
            sourceBuilder.AppendLine("}"); // end namespace
            sourceBuilder.AppendLine("#nullable disable");

            context.AddSource($"{classDecl.Identifier.Text}.Generated.cs", SourceText.From(sourceBuilder.ToString(), Encoding.UTF8));
        }
    }

    private static string GetMethod(
        SemanticModel semanticModel, ClassDeclarationSyntax classDecl, string methodSignature,
        Func<string, string> settingsPropertyAction, Func<string, string> propertyAction, bool includeBreak)
    {
        StringBuilder sourceBuilder = new();

        sourceBuilder.AppendLine($@"
            {methodSignature}
            {{
                if (propertyPath.Count == 0)
                {{
                    throw new ArgumentException(""Property path cannot be empty"", nameof(propertyPath));
                }}

                var currentProperty = propertyPath.Dequeue();
                switch (currentProperty)
                {{");

        foreach (PropertyDeclarationSyntax property in classDecl.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            AttributeSyntax jsonPropertyAttribute = property.AttributeLists
                .SelectMany(a => a.Attributes)
                .FirstOrDefault(a => a.Name.ToString() == "JsonProperty");

            if (jsonPropertyAttribute != null)
            {
                AttributeArgumentSyntax attribArg = jsonPropertyAttribute.ArgumentList?.Arguments[0]!;
                string jsonPropertyName = semanticModel.GetConstantValue(attribArg.Expression).Value?.ToString().Trim('"')!;

                if (property.Type.ToString().EndsWith("Settings"))
                {
                    sourceBuilder.AppendLine($@"
                        case ""{jsonPropertyName}"":
                            if (propertyPath.Count > 0)
                            {{
                                {settingsPropertyAction(property.Identifier.Text)}
                            }}
                            else
                            {{
                                throw new ArgumentException(""Property path must point to a valid property"", nameof(propertyPath));
                            }}
                            {(includeBreak ? "break;" : string.Empty)}");
                }
                else
                {
                    sourceBuilder.AppendLine($@"
                        case ""{jsonPropertyName}"":
                            if (propertyPath.Count > 0)
                            {{
                                throw new ArgumentException(""Property path must point to a valid property"", nameof(propertyPath));
                            }}
                            {propertyAction(property.Identifier.Text)}
                            {(includeBreak ? "break;" : string.Empty)}");
                }
            }
        }

        sourceBuilder.AppendLine($@"
                    default:
                        throw new ArgumentException($""Unknown property: {{currentProperty}}"", nameof(propertyPath));
                }}
            }}");

        return sourceBuilder.ToString();
    }
}
