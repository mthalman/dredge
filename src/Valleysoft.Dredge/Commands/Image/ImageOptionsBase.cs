using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class ImageOptionsBase : PlatformOptionsBase
{
    private readonly Option<string?> dockerContextOption;
    private readonly Option<string?> dockerHostOption;

    public ImageOptionsBase()
    {
        dockerContextOption = Add(new Option<string?>("--docker-context")
        {
            Description = "Docker context for docker:// image references"
        });
        dockerHostOption = Add(new Option<string?>("--docker-host")
        {
            Description = "Docker endpoint for docker:// image references (cannot be combined with --docker-context)"
        });
    }

    protected override void GetValues()
    {
        base.GetValues();
        DockerContext = GetValue(dockerContextOption);
        DockerHost = GetValue(dockerHostOption);
    }

    public override void SetCommandOptions(Command cmd)
    {
        base.SetCommandOptions(cmd);
        cmd.Validators.Add(result =>
        {
            if (result.GetValue(dockerContextOption) is not null &&
                result.GetValue(dockerHostOption) is not null)
            {
                result.AddError("--docker-context and --docker-host cannot be combined.");
            }
        });
    }
}
