using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge;

internal static class CommandHelper
{
    public static async Task ExecuteCommandAsync(string? registry, Func<Task> execute)
    {
        try
        {
            await execute();
        }
        catch (Exception e)
        {
            ConsoleColor savedColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;

            string message = e.Message;
            if (e is RegistryException dockerRegistryException)
            {
                Error? error = dockerRegistryException.Errors.FirstOrDefault();
                if (error?.Code == "UNAUTHORIZED")
                {
                    string loginCommand = "docker login";
                    if (registry is not null)
                    {
                        loginCommand += $" {registry}";
                    }

                    message = $"Authentication required. Ensure that your credentials are stored for the registry by running '{loginCommand}'.";
                }
                else
                {
                    message = error?.Message ?? message;
                }
            }
            else if (e is ManifestListResolutionException)
            {
                message = $"Unable to resolve the manifest list tag to a single matching platform. Run \"dredge manifest get\" to view the underlying manifests of this tag. Use {PlatformOptionsBase.OsOptionName}, {PlatformOptionsBase.ArchOptionName}, and {PlatformOptionsBase.OsVersionOptionName} to specify the target platform to match.";
            }

            Console.Error.WriteLine(message);
            Console.ForegroundColor = savedColor;
        }
    }
}
