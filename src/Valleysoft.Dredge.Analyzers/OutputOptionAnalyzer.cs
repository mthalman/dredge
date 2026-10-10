using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Valleysoft.Dredge.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OutputOptionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [CliDiagnostics.OutputOptionMustUseSharedHelper];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeObjectCreation, OperationKind.ObjectCreation);
        context.RegisterSyntaxNodeAction(
            AnalyzeConstructorInitializer,
            SyntaxKind.ConstructorDeclaration);
        context.RegisterSyntaxNodeAction(
            AnalyzeAliasAddition,
            SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(
            AnalyzeAliasCollectionInitializer,
            SyntaxKind.SimpleAssignmentExpression);
    }

    private static void AnalyzeObjectCreation(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        INamedTypeSymbol? optionType = creation.Constructor?.ContainingType;
        if (optionType is null)
        {
            return;
        }

        bool isRawOutputOption = optionType.Name == "Option" &&
            optionType.ContainingNamespace.ToDisplayString() == "System.CommandLine" &&
            optionType.TypeArguments.Length == 1 &&
            creation.Arguments.Any(argument =>
                ContainsOutputName(argument.Value));
        bool bypassesTypedOutputOption = optionType.Name == "JsonOutputOption" &&
            optionType.ContainingNamespace.ToDisplayString() == "Valleysoft.Dredge.Commands" &&
            !IsInsideCliOutputOption(creation.Syntax);
        if (!isRawOutputOption && !bypassesTypedOutputOption)
        {
            return;
        }

        Location location = isRawOutputOption
            ? creation.Arguments.First(static argument =>
                ContainsOutputName(argument.Value))
                .Syntax.GetLocation()
            : creation.Syntax.GetLocation();
        context.ReportDiagnostic(Diagnostic.Create(
            CliDiagnostics.OutputOptionMustUseSharedHelper,
            location));
    }

    private static void AnalyzeConstructorInitializer(SyntaxNodeAnalysisContext context)
    {
        ConstructorInitializerSyntax? initializer =
            ((ConstructorDeclarationSyntax)context.Node).Initializer;
        if (initializer is null)
        {
            return;
        }

        if (!initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.BaseKeyword) ||
            context.SemanticModel.GetSymbolInfo(initializer, context.CancellationToken).Symbol
                is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor ||
            constructor.ContainingType.Name != "Option" ||
            constructor.ContainingType.ContainingNamespace.ToDisplayString() != "System.CommandLine" ||
            constructor.ContainingType.TypeArguments.Length != 1)
        {
            return;
        }

        ArgumentSyntax? outputArgument = initializer.ArgumentList.Arguments.FirstOrDefault(
            argument => ContainsOutputName(
                context.SemanticModel.GetOperation(argument.Expression, context.CancellationToken)!));
        if (outputArgument is null ||
            IsSharedOutputOption(initializer))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            CliDiagnostics.OutputOptionMustUseSharedHelper,
            outputArgument.GetLocation()));
    }

    private static void AnalyzeAliasAddition(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Add",
                Expression: MemberAccessExpressionSyntax aliasesAccess
            } ||
            context.SemanticModel.GetSymbolInfo(
                aliasesAccess, context.CancellationToken).Symbol is not IPropertySymbol
            {
                Name: "Aliases",
                ContainingType.Name: "Option",
                ContainingType.ContainingNamespace: var containingNamespace
            } ||
            containingNamespace.ToDisplayString() != "System.CommandLine")
        {
            return;
        }

        ArgumentSyntax? outputArgument = invocation.ArgumentList.Arguments.FirstOrDefault(
            argument => ContainsOutputName(
                context.SemanticModel.GetOperation(argument.Expression, context.CancellationToken)!));
        if (outputArgument is null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            CliDiagnostics.OutputOptionMustUseSharedHelper,
            outputArgument.GetLocation()));
    }

    private static void AnalyzeAliasCollectionInitializer(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Right is not InitializerExpressionSyntax initializer ||
            context.SemanticModel.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol
                is not IPropertySymbol
                {
                    Name: "Aliases",
                    ContainingType.Name: "Option",
                    ContainingType.ContainingNamespace: var containingNamespace
                } ||
            containingNamespace.ToDisplayString() != "System.CommandLine")
        {
            return;
        }

        ExpressionSyntax? outputName = initializer.DescendantNodesAndSelf()
            .OfType<ExpressionSyntax>()
            .FirstOrDefault(expression =>
                context.SemanticModel.GetConstantValue(expression, context.CancellationToken) is
                    { HasValue: true, Value: "--output" });
        if (outputName is null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            CliDiagnostics.OutputOptionMustUseSharedHelper,
            outputName.GetLocation()));
    }

    private static bool ContainsOutputName(IOperation operation)
    {
        Stack<IOperation> operations = new();
        operations.Push(operation);
        while (operations.TryPop(out IOperation? current))
        {
            if (current.ConstantValue is { HasValue: true, Value: "--output" })
            {
                return true;
            }

            foreach (IOperation child in current.ChildOperations)
            {
                operations.Push(child);
            }
        }

        return false;
    }

    private static bool IsSharedOutputOption(ConstructorInitializerSyntax initializer)
    {
        TypeDeclarationSyntax? declaration = initializer.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();
        if (declaration is null)
        {
            return false;
        }

        string? namespaceName = declaration.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?.Name.ToString();
        return namespaceName == "Valleysoft.Dredge.Commands" &&
            declaration.Identifier.ValueText is "JsonOutputOption" or "CliOutputPathOption";
    }

    private static bool IsInsideCliOutputOption(SyntaxNode syntax)
    {
        TypeDeclarationSyntax? declaration = syntax.Ancestors().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();
        if (declaration is null)
        {
            return false;
        }

        string? namespaceName = declaration.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?.Name.ToString();
        return declaration.Identifier.ValueText == "CliOutputOption" &&
            namespaceName == "Valleysoft.Dredge.Commands";
    }
}
