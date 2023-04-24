using Newtonsoft.Json;
using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge.Commands.Image;

public class OsCommand : RegistryCommandBase<OsOptions>
{
    public OsCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("os", "Gets OS info about the container image", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
        {
            OsInfo osInfo = await OsHelper.GetOsInfoAsync(Options.Image, DockerRegistryClientFactory, AppSettingsHelper.Load(), Options.ToPlatformOptions());

            if (osInfo.WindowsOsInfo is null && osInfo.LinuxOsInfo is null)
            {
                throw new Exception("Unable to derive OS information from the image.");
            }

            string output = JsonConvert.SerializeObject((object?)osInfo.WindowsOsInfo ?? osInfo.LinuxOsInfo, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented
            });
            Console.Out.WriteLine(output);
        });
    }
}
