namespace Valleysoft.Dredge.Core;

public class DockerfileOptions
{
    public bool NoFormat { get; set; }
    public PlatformOptions PlatformOptions { get; set; } = new();
}
