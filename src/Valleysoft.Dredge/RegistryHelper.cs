namespace Valleysoft.DockerRegistryClient.Cli
{
    internal static class RegistryHelper
    {
        public static string GetAuthRegistry(string? registry) =>
            registry ?? "https://index.docker.io/v1/";

        public static string GetApiRegistry(string? registry) =>
            registry ?? "registry-1.docker.io";
    }
}
