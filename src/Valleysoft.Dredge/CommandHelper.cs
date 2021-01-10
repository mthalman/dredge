using System;
using System.CommandLine;
using System.CommandLine.IO;
using System.Linq;
using System.Threading.Tasks;
using Valleysoft.DockerCredsProvider;
using Microsoft.Rest;

namespace Valleysoft.DockerRegistryClient.Cli
{
    internal static class CommandHelper
    {
        public static async Task<DockerRegistryClient> GetRegistryClientAsync(string? registry)
        {
            DockerCredentials creds;

            try
            {
                creds = await CredsProvider.GetCredentialsAsync(RegistryHelper.GetAuthRegistry(registry));
            }
            catch (CredsNotFoundException e)
            {
                string loginCommand = "docker login";
                if (registry is not null)
                {
                    loginCommand += $" {registry}";
                }

                throw new InvalidOperationException(
                    $"Credentials not found. Ensure that your credentials are stored for the registry by running '{loginCommand}'.", e);
            }
            
            BasicAuthenticationCredentials basicCreds = new BasicAuthenticationCredentials
            {
                UserName = creds.Username,
                Password = creds.Password
            };
         
            return new DockerRegistryClient(RegistryHelper.GetApiRegistry(registry), basicCreds);
        }

        public static Option GetRegistryOption() =>
            new Option<string>(new string[] { "--registry", "-r" }, "Name of the Docker registry (by default, Docker Hub registry is used)");

        public static Argument GetRepositoryArgument() =>
            new Argument<string>("repository", "Name of the Docker repository");

        public static async Task ExecuteCommandAsync(IConsole console, Func<Task> execute)
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
                if (e is DockerRegistryException dockerRegistryException)
                {
                    message = dockerRegistryException.Errors.FirstOrDefault()?.Message ?? message;
                }

                console.Error.WriteLine(message);
                Console.ForegroundColor = savedColor;
            }
        }
    }
}
