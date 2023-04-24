namespace Valleysoft.Dredge.Core;

public class LayerCompareOptions
{
    public bool IncludeHistory { get; set; }
    public bool IncludeCompressedSize { get; set; }
    public PlatformOptions PlatformOptions { get; set; } = new();
}
