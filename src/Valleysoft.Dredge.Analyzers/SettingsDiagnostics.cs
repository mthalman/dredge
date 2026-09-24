using Microsoft.CodeAnalysis;

namespace Valleysoft.Dredge.Analyzers;

internal static class SettingsDiagnostics
{
    public static readonly DiagnosticDescriptor NonPartial = Create(
        "DRG001", "Settings declarations must be partial",
        "Type '{0}' must be partial to generate settings accessors");

    public static readonly DiagnosticDescriptor UnsupportedType = Create(
        "DRG002", "Unsupported settings type",
        "Type '{0}' must be a non-file-local partial class; settings targets cannot be static, records, or inherit a base class other than object");

    public static readonly DiagnosticDescriptor InvalidAttribute = Create(
        "DRG003", "Invalid settings attribute",
        "Invalid attribute on '{0}': {1}");

    public static readonly DiagnosticDescriptor DuplicateName = Create(
        "DRG004", "Duplicate settings path",
        "JSON path segment '{0}' is declared more than once in settings type '{1}'");

    public static readonly DiagnosticDescriptor UnsupportedProperty = Create(
        "DRG005", "Unsupported settings property",
        "Property '{0}' must be an ordinary instance property of type string, string?, or an explicitly marked settings class");

    public static readonly DiagnosticDescriptor InvalidAccessor = Create(
        "DRG006", "Settings accessor is not usable",
        "Property '{0}' requires an accessible getter and, for string leaves, an accessible non-init setter");

    public static readonly DiagnosticDescriptor MemberConflict = Create(
        "DRG007", "Generated settings member conflicts",
        "Member '{0}' conflicts with a generated settings accessor; rename or remove the existing member");

    public static readonly DiagnosticDescriptor InvalidNestedType = Create(
        "DRG008", "Nested settings contract is invalid",
        "Property '{0}' references invalid settings type '{1}'; fix the diagnostics on the nested settings declarations");

    private static DiagnosticDescriptor Create(string id, string title, string message) =>
        new(id, title, message, "SettingsGeneration", DiagnosticSeverity.Error, isEnabledByDefault: true);
}
