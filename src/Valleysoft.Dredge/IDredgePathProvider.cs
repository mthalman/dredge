namespace Valleysoft.Dredge;

internal interface IDredgePathProvider
{
    string TempPath { get; }
    string CachePath { get; }
}

internal sealed class DredgePathProvider : IDredgePathProvider
{
    public string TempPath => DredgeState.DredgeTempPath;
    public string CachePath => AppSettings.Load().Cache.GetPath();
}
