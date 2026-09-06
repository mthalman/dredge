namespace Valleysoft.Dredge;

internal interface IDredgePathProvider
{
    string TempPath { get; }
}

internal sealed class DredgePathProvider : IDredgePathProvider
{
    public string TempPath => DredgeState.DredgeTempPath;
}
