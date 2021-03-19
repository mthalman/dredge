using System.CommandLine;

namespace Valleysoft.Dredge
{
    public class Program
    {
        public static int Main(string[] args)
        {
            RootCommand rootCmd = new ("CLI for executing commands on a Docker registry's HTTP API.")
            {
                new RepoCommand(),
                new TagCommand(),
                new ManifestCommand()
            };

            return rootCmd.Invoke(args);
        }
    }
}
