using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

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

            Console.Error.WriteLine(message);
            Console.ForegroundColor = savedColor;
        }
    }
}
