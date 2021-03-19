using System;
using System.CommandLine;
using System.CommandLine.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Rest;
using Valleysoft.DockerCredsProvider;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge
{
    internal static class CommandHelper
    {
        public static async Task<DockerRegistryClient.DockerRegistryClient> GetRegistryClientAsync(string? registry)
        {
            DockerCredentials creds;

            try
            {
                creds = await CredsProvider.GetCredentialsAsync(DockerHubHelper.GetAuthRegistry(registry));
            }
            catch (CredsNotFoundException)
            {
                return new DockerRegistryClient.DockerRegistryClient(DockerHubHelper.GetApiRegistry(registry));
            }
            
            BasicAuthenticationCredentials basicCreds = new()
            {
                UserName = creds.Username,
                Password = creds.Password
            };
         
            return new DockerRegistryClient.DockerRegistryClient(DockerHubHelper.GetApiRegistry(registry), basicCreds);
        }

        public static async Task ExecuteCommandAsync(IConsole console, string? registry, Func<Task> execute)
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
                if (e is DockerRegistryClient.DockerRegistryException dockerRegistryException)
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

                console.Error.WriteLine(message);
                Console.ForegroundColor = savedColor;
            }
        }
    }
}
