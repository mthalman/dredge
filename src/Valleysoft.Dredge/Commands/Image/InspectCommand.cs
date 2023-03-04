using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge.Commands.Image;

public class InspectCommand : RegistryCommandBase<InspectOptions>
{
    public InspectCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("inspect", "Return low-level information on a container image", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
        {
            string json = await ImageInspector.GetImageConfigJson(Options.Image, DockerRegistryClientFactory, AppSettingsHelper.Load(), Options.ToPlatformOptions());
            Console.Out.WriteLine(json);
        });
    }
}
