using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.Threading.Tasks;
using Valleysoft.DockerRegistryClient.Models;
using Newtonsoft.Json;

namespace Valleysoft.DockerRegistryClient.Cli
{
    public class ManifestCommand : Command
    {
        public ManifestCommand() : base("manifest", "Commands related to Docker manifests")
        {
            AddCommand(new GetCommand());
            AddCommand(new DigestCommand());
        }

        private class GetCommand : Command
        {
            public GetCommand() : base("get", "Queries a Docker manifest")
            {
                AddArgument(CommandHelper.GetRepositoryArgument());
                AddArgument(new Argument<string>("tagOrDigest", "Tag or digest value of the manifest to query"));
                AddOption(CommandHelper.GetRegistryOption());
                Handler = CommandHandler.Create<string, string, string?, IConsole>(ExecuteAsync);
            }

            private Task ExecuteAsync(string repository, string tagOrDigest, string? registry, IConsole console)
            {
                return CommandHelper.ExecuteCommandAsync(console, async () =>
                {
                    using DockerRegistryClient client = await CommandHelper.GetRegistryClientAsync(registry);

                    ManifestInfo manifestInfo = await client.Manifests.GetAsync(repository, tagOrDigest);

                    string output = JsonConvert.SerializeObject(manifestInfo.Manifest, Formatting.Indented);

                    console.Out.WriteLine(output);
                });
            }
        }

        private class DigestCommand : Command
        {
            public DigestCommand() : base("digest", "Queries the digest of a Docker manifest")
            {
                AddArgument(CommandHelper.GetRepositoryArgument());
                AddArgument(new Argument<string>("tag", "Tag of the manifest to query"));
                AddOption(CommandHelper.GetRegistryOption());
                Handler = CommandHandler.Create<string, string, string?, IConsole>(ExecuteAsync);
            }

            private Task ExecuteAsync(string repository, string tag, string? registry, IConsole console)
            {
                return CommandHelper.ExecuteCommandAsync(console, async () =>
                {
                    using DockerRegistryClient client = await CommandHelper.GetRegistryClientAsync(registry);

                    string digest = await client.Manifests.GetDigestAsync(repository, tag);

                    console.Out.WriteLine(digest);
                });
            }
        }
    }
}
