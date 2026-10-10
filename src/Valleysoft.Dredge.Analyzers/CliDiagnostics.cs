using Microsoft.CodeAnalysis;

namespace Valleysoft.Dredge.Analyzers;

internal static class CliDiagnostics
{
    public static readonly DiagnosticDescriptor OutputOptionMustUseSharedHelper = new(
        "DRG009",
        "Output options must use the shared helper",
        "Use CliOutputOption<T> for format values or CliOutputPathOption for file destinations instead of declaring '--output' directly",
        "Cli",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
