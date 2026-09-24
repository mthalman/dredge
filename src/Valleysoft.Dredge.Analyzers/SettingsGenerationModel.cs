using System.Collections.Immutable;

namespace Valleysoft.Dredge.Analyzers;

internal sealed class SettingsGenerationModel : IEquatable<SettingsGenerationModel>
{
    public SettingsGenerationModel(
        string hintName, string? @namespace, string declarations, int containerCount,
        ImmutableArray<SettingsPropertyModel> properties)
    {
        HintName = hintName;
        Namespace = @namespace;
        Declarations = declarations;
        ContainerCount = containerCount;
        Properties = properties;
    }

    public string HintName { get; }
    public string? Namespace { get; }
    public string Declarations { get; }
    public int ContainerCount { get; }
    public ImmutableArray<SettingsPropertyModel> Properties { get; }

    public bool Equals(SettingsGenerationModel? other) =>
        other is not null && HintName == other.HintName && Namespace == other.Namespace &&
        Declarations == other.Declarations && ContainerCount == other.ContainerCount &&
        Properties.SequenceEqual(other.Properties);

    public override bool Equals(object? obj) => obj is SettingsGenerationModel other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(HintName);
}

internal readonly struct SettingsPropertyModel : IEquatable<SettingsPropertyModel>
{
    public SettingsPropertyModel(string jsonName, string memberName, bool isBranch)
    {
        JsonName = jsonName;
        MemberName = memberName;
        IsBranch = isBranch;
    }

    public string JsonName { get; }
    public string MemberName { get; }
    public bool IsBranch { get; }

    public bool Equals(SettingsPropertyModel other) =>
        JsonName == other.JsonName && MemberName == other.MemberName && IsBranch == other.IsBranch;

    public override bool Equals(object? obj) => obj is SettingsPropertyModel other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(JsonName);
}
