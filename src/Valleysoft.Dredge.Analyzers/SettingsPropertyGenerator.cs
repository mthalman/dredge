using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace Valleysoft.Dredge.Analyzers;

[Generator]
public class SettingsSourceGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
    }

    public void Execute(GeneratorExecutionContext context)
    {
        // Iterate over all the syntax trees in the compilation
        foreach (SyntaxTree tree in context.Compilation.SyntaxTrees)
        {
            // Retrieve the semantic model for the syntax tree
            SemanticModel semanticModel = context.Compilation.GetSemanticModel(tree);

            // Find all class declarations whose name ends with "Settings"
            IEnumerable<ClassDeclarationSyntax> classes = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(c => c.Identifier.Text.EndsWith("Settings"));

            foreach (ClassDeclarationSyntax classDecl in classes)
            {
                // Retrieve the symbol for the class
                INamedTypeSymbol classSymbol = semanticModel.GetDeclaredSymbol(classDecl)!;

                // Start building the source code
                StringBuilder sourceBuilder = new();

                // Add the necessary using directives
                sourceBuilder.AppendLine("#nullable enable");
                sourceBuilder.AppendLine("using System;");
                sourceBuilder.AppendLine("using System.Collections.Generic;");
                sourceBuilder.AppendLine("using Newtonsoft.Json;");

                // Start the namespace declaration
                sourceBuilder.AppendLine($"namespace {classSymbol.ContainingNamespace}");
                sourceBuilder.AppendLine("{");

                // Start the class declaration
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

                sourceBuilder.AppendLine("        }"); // end class

                sourceBuilder.AppendLine("    }"); // end namespace
                sourceBuilder.AppendLine("#nullable disable");


                // Add the new syntax tree to the compilation
                context.AddSource($"{classDecl.Identifier.Text}.Generated.cs", sourceBuilder.ToString());
            }
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

        // Generate a case for each property in the class
        foreach (PropertyDeclarationSyntax property in classDecl.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            // Get the JsonPropertyAttribute for the property, if it exists
            AttributeSyntax jsonPropertyAttribute = property.AttributeLists
                .SelectMany(a => a.Attributes)
                .FirstOrDefault(a => a.Name.ToString() == "JsonProperty");

            if (jsonPropertyAttribute != null)
            {
                // Get the name argument of the JsonPropertyAttribute
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
