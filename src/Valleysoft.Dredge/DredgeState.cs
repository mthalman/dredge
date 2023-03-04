namespace Valleysoft.Dredge;

internal static class DredgeState
{
    public static readonly string DredgeTempPath = Path.Combine(Path.GetTempPath(), "Valleysoft.Dredge");
    public static readonly string LayersTempPath = Path.Combine(DredgeTempPath, "layers");
}
