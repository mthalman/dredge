namespace Valleysoft.Dredge;

internal static class DockerHubHelper
{
    public static string GetAuthRegistry(string? registry) =>
        string.IsNullOrEmpty(registry) ? "https://index.docker.io/v1/" : registry;

    public static string GetApiRegistry(string? registry) =>
        string.IsNullOrEmpty(registry) ? "registry-1.docker.io" : registry;

    public static string ResolveRepoName(string? registry, string repoName)
    {
        if (registry is null && !repoName.Contains('/'))
        {
            return $"library/{repoName}";
        }

        return repoName;
    }
}
