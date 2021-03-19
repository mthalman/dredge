namespace Valleysoft.Dredge
{
    internal static class DockerHubHelper
    {
        public static string GetAuthRegistry(string? registry) =>
            registry ?? "https://index.docker.io/v1/";

        public static string GetApiRegistry(string? registry) =>
            registry ?? "registry-1.docker.io";

        public static string ResolveRepoName(string? registry, string repoName)
        {
            if (registry is null && !repoName.Contains('/'))
            {
                return $"library/{repoName}";
            }

            return repoName;
        }
    }
}
