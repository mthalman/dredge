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
    private const string MarkerName = "Valleysoft.Dredge.GenerateSettingsAttribute";
    private const string JsonAttributeName = "System.Text.Json.Serialization.JsonPropertyNameAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static context => context.AddSource(
            "GenerateSettingsAttribute.g.cs",
            """
            namespace Valleysoft.Dredge
            {
                [global::System.AttributeUsage(global::System.AttributeTargets.Class | global::System.AttributeTargets.Struct | global::System.AttributeTargets.Interface, Inherited = false)]
                internal sealed class GenerateSettingsAttribute : global::System.Attribute
                {
                }
            }
            """));

        var analyses = context.SyntaxProvider.ForAttributeWithMetadataName(
            MarkerName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (context, cancellationToken) => Analyze(context, cancellationToken));

        var models = analyses.Select(static (analysis, _) => analysis.Model)
            .Where(static model => model is not null)
            .WithTrackingName("SettingsModels");

        context.RegisterSourceOutput(models, static (context, model) =>
            context.AddSource(model!.HintName, SourceText.From(Emit(model), Encoding.UTF8)));

        context.RegisterSourceOutput(
            analyses.SelectMany(static (analysis, _) => analysis.Diagnostics),
            static (context, diagnostic) => context.ReportDiagnostic(diagnostic));
    }

    private static Analysis Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        var type = (INamedTypeSymbol)context.TargetSymbol;
        Compilation compilation = context.SemanticModel.Compilation;
        INamedTypeSymbol marker = context.Attributes[0].AttributeClass!;
        SyntaxReference? firstMarker = type.GetAttributes()
            .First(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker))
            .ApplicationSyntaxReference;
        if (firstMarker is not null &&
            (firstMarker.SyntaxTree != context.TargetNode.SyntaxTree ||
             !context.TargetNode.Span.Contains(firstMarker.Span)))
        {
            return new Analysis(null, []);
        }

        INamedTypeSymbol? jsonAttribute = compilation.GetTypeByMetadataName(JsonAttributeName);
        var diagnostics = new List<Diagnostic>();
        var properties = Validate(type, compilation, marker, jsonAttribute, diagnostics, cancellationToken);

        foreach (var property in properties.Where(property => property.NestedType is not null))
        {
            if (!IsValidNestedGraph(property.NestedType!, compilation, marker, jsonAttribute, cancellationToken))
            {
                diagnostics.Add(Diagnostic.Create(
                    SettingsDiagnostics.InvalidNestedType, property.Symbol.Locations.FirstOrDefault(),
                    property.Symbol.Name, property.NestedType!.ToDisplayString()));
            }
        }

        if (diagnostics.Count != 0)
        {
            return new Analysis(null, diagnostics.ToImmutableArray());
        }

        var containers = new Stack<INamedTypeSymbol>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            containers.Push(current);
        }

        string declarations = string.Join("\n", containers.Select(Declaration));
        string identity = (type.ContainingNamespace.IsGlobalNamespace ? "" :
            type.ContainingNamespace.ToDisplayString() + ".") +
            string.Join("+", containers.Select(container => container.MetadataName));
        string hintName = "Settings_" + string.Concat(Encoding.UTF8.GetBytes(identity).Select(
            value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture))) + ".g.cs";
        var model = new SettingsGenerationModel(
            hintName,
            type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            declarations,
            containers.Count,
            properties.Select(property => new SettingsPropertyModel(
                property.JsonName, property.Symbol.Name, property.NestedType is not null)).ToImmutableArray());

        return new Analysis(model, []);
    }

    private static List<Property> Validate(
        INamedTypeSymbol type,
        Compilation compilation,
        INamedTypeSymbol marker,
        INamedTypeSymbol? jsonAttribute,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.TypeKind != TypeKind.Class || current.IsRecord || current.IsFileLocal ||
                (SymbolEqualityComparer.Default.Equals(current, type) && current.IsStatic))
            {
                diagnostics.Add(Diagnostic.Create(SettingsDiagnostics.UnsupportedType,
                    current.Locations.FirstOrDefault(), current.Name));
            }

            foreach (SyntaxReference reference in current.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration &&
                    !declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    diagnostics.Add(Diagnostic.Create(SettingsDiagnostics.NonPartial,
                        declaration.Identifier.GetLocation(), current.Name));
                }
            }
        }

        if (type.TypeKind == TypeKind.Class && !type.IsRecord &&
            type.BaseType is { SpecialType: not SpecialType.System_Object })
        {
            diagnostics.Add(Diagnostic.Create(SettingsDiagnostics.UnsupportedType,
                type.Locations.FirstOrDefault(), type.Name));
        }

        if (type.GetAttributes().Count(attribute =>
            SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker)) != 1)
        {
            diagnostics.Add(Diagnostic.Create(SettingsDiagnostics.InvalidAttribute,
                type.Locations.FirstOrDefault(), type.Name, "apply GenerateSettings exactly once"));
        }

        foreach (string methodName in new[] { "SetProperty", "GetProperty" })
        {
            if (type.Name == methodName || type.TypeParameters.Any(parameter => parameter.Name == methodName) ||
                type.GetMembers(methodName).Any(member =>
                ConflictsWithAccessor(member, methodName, compilation)))
            {
                diagnostics.Add(Diagnostic.Create(SettingsDiagnostics.MemberConflict,
                    type.GetMembers(methodName).FirstOrDefault()?.Locations.FirstOrDefault() ??
                    type.Locations.FirstOrDefault(), methodName));
            }
        }

        var properties = new List<Property>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = property.GetAttributes().Where(attribute =>
                jsonAttribute is not null &&
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, jsonAttribute)).ToArray();
            if (attributes.Length == 0)
            {
                continue;
            }

            Location? location = attributes[0].ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation()
                ?? property.Locations.FirstOrDefault();
            if (attributes.Length != 1 || attributes[0].ConstructorArguments.Length != 1 ||
                attributes[0].ConstructorArguments[0].Value is not string jsonName ||
                string.IsNullOrEmpty(jsonName) || jsonName.IndexOf('.') >= 0)
            {
                diagnostics.Add(Diagnostic.Create(SettingsDiagnostics.InvalidAttribute, location,
                    property.Name, "use one JsonPropertyName with a nonempty constant string containing no dots"));
                continue;
            }

            if (!names.Add(jsonName))
            {
                diagnostics.Add(Diagnostic.Create(SettingsDiagnostics.DuplicateName, location, jsonName, type.Name));
            }

            INamedTypeSymbol? nestedType = property.Type as INamedTypeSymbol;
            if (nestedType is not null && !nestedType.GetAttributes().Any(attribute =>
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker)))
            {
                nestedType = null;
            }

            if (property.IsStatic || property.IsIndexer || property.RefKind != RefKind.None ||
                property.ExplicitInterfaceImplementations.Length != 0 ||
                (property.Type.SpecialType != SpecialType.System_String && nestedType is null) ||
                (nestedType is not null && !nestedType.IsReferenceType))
            {
                diagnostics.Add(Diagnostic.Create(SettingsDiagnostics.UnsupportedProperty,
                    property.Locations.FirstOrDefault(), property.Name));
                continue;
            }

            if (property.GetMethod is null || !compilation.IsSymbolAccessibleWithin(property.GetMethod, type) ||
                (nestedType is null && (property.SetMethod is null || property.SetMethod.IsInitOnly ||
                    !compilation.IsSymbolAccessibleWithin(property.SetMethod, type))))
            {
                diagnostics.Add(Diagnostic.Create(SettingsDiagnostics.InvalidAccessor,
                    property.Locations.FirstOrDefault(), property.Name));
                continue;
            }

            properties.Add(new Property(property, jsonName, nestedType));
        }

        return properties.OrderBy(property => property.JsonName, StringComparer.Ordinal).ToList();
    }

    private static bool IsValidNestedGraph(
        INamedTypeSymbol type,
        Compilation compilation,
        INamedTypeSymbol marker,
        INamedTypeSymbol? jsonAttribute,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<INamedTypeSymbol>();
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        pending.Push(type.OriginalDefinition);
        while (pending.Count != 0)
        {
            INamedTypeSymbol current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            var diagnostics = new List<Diagnostic>();
            var properties = Validate(current, compilation, marker, jsonAttribute, diagnostics, cancellationToken);
            if (diagnostics.Count != 0)
            {
                return false;
            }

            foreach (var property in properties)
            {
                if (property.NestedType is not null)
                {
                    pending.Push(property.NestedType.OriginalDefinition);
                }
            }
        }

        return true;
    }

    private static bool ConflictsWithAccessor(ISymbol member, string name, Compilation compilation)
    {
        if (member is not IMethodSymbol method)
        {
            return true;
        }

        int parameterCount = name == "GetProperty" ? 1 : 2;
        if (method.Arity != 0 || method.Parameters.Length != parameterCount ||
            method.Parameters.Any(parameter => parameter.RefKind != RefKind.None))
        {
            return false;
        }

        var queue = compilation.GetTypeByMetadataName("System.Collections.Generic.Queue`1")
            ?.Construct(compilation.GetSpecialType(SpecialType.System_String));
        return SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, queue) &&
            (parameterCount == 1 || method.Parameters[1].Type.SpecialType == SpecialType.System_String);
    }

    private static string Declaration(INamedTypeSymbol type)
    {
        string accessibility = type.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Private => "private",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            _ => "internal"
        };
        string parameters = type.TypeParameters.Length == 0 ? "" :
            "<" + string.Join(", ", type.TypeParameters.Select(parameter => Identifier(parameter.Name))) + ">";
        return $"{accessibility} {(type.IsStatic ? "static " : "")}partial class {Identifier(type.Name)}{parameters}\n{{";
    }

    private static string Identifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ||
        SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static string Emit(SettingsGenerationModel model)
    {
        var source = new StringBuilder("#nullable enable\n");
        if (model.Namespace is not null)
        {
            source.Append("namespace ").Append(model.Namespace).AppendLine("\n{");
        }

        source.AppendLine(model.Declarations);
        EmitMethod(source, model.Properties, set: true);
        EmitMethod(source, model.Properties, set: false);
        source.Append('}', model.ContainerCount + (model.Namespace is null ? 0 : 1)).AppendLine();
        return source.ToString();
    }

    private static void EmitMethod(StringBuilder source, ImmutableArray<SettingsPropertyModel> properties, bool set)
    {
        source.AppendLine(set
            ? "public void SetProperty(global::System.Collections.Generic.Queue<string> propertyPath, string value)"
            : "public object? GetProperty(global::System.Collections.Generic.Queue<string> propertyPath)");
        source.AppendLine("""
            {
                if (propertyPath.Count == 0)
                {
                    throw new global::System.ArgumentException("Property path cannot be empty", nameof(propertyPath));
                }
                var currentProperty = propertyPath.Dequeue();
                switch (currentProperty)
                {
            """);

        foreach (var property in properties)
        {
            source.Append("case ").Append(SymbolDisplay.FormatLiteral(property.JsonName, quote: true)).AppendLine(":");
            source.AppendLine("{");
            source.Append("if (propertyPath.Count ").Append(property.IsBranch ? "==" : ">").AppendLine(" 0)");
            source.AppendLine("""
                {
                    throw new global::System.ArgumentException("Property path must point to a valid property", nameof(propertyPath));
                }
                """);
            string member = "this." + Identifier(property.MemberName);
            if (property.IsBranch)
            {
                source.Append("var nested = ").Append(member).AppendLine(";");
                source.AppendLine("if (nested is null)\n{");
                source.Append("throw new global::System.InvalidOperationException(")
                    .Append(SymbolDisplay.FormatLiteral($"Settings object '{property.JsonName}' is null.", quote: true))
                    .AppendLine(");\n}");
                source.AppendLine(set
                    ? "nested.SetProperty(propertyPath, value);"
                    : "return nested.GetProperty(propertyPath);");
            }
            else
            {
                source.AppendLine(set ? $"{member} = value;" : $"return {member};");
            }

            if (set)
            {
                source.AppendLine("break;");
            }

            source.AppendLine("}");
        }

        source.AppendLine("""
                    default:
                        throw new global::System.ArgumentException($"Unknown property: {currentProperty}", nameof(propertyPath));
                }
            }
            """);
    }

    private sealed class Analysis
    {
        public Analysis(SettingsGenerationModel? model, ImmutableArray<Diagnostic> diagnostics)
        {
            Model = model;
            Diagnostics = diagnostics;
        }

        public SettingsGenerationModel? Model { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }
    }

    private sealed class Property
    {
        public Property(IPropertySymbol symbol, string jsonName, INamedTypeSymbol? nestedType)
        {
            Symbol = symbol;
            JsonName = jsonName;
            NestedType = nestedType;
        }

        public IPropertySymbol Symbol { get; }
        public string JsonName { get; }
        public INamedTypeSymbol? NestedType { get; }
    }
}
