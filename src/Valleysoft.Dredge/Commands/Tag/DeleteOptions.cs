using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Tag;

public class DeleteOptions : OptionsBase
{
    private readonly Argument<string> imageArgument;
    private readonly Option<bool> yesOption;

    public string Image { get; set; } = string.Empty;
    public bool Yes { get; set; }

    public DeleteOptions()
    {
        imageArgument = Add(ImageReferenceArgument.CreateForDeletion(tagOnly: true));
        yesOption = Add(new Option<bool>("--yes", "-y")
        {
            Description = "Skip confirmation; required when standard input is redirected"
        });
    }

    protected override void GetValues()
    {
        Image = GetValue(imageArgument);
        Yes = GetValue(yesOption);
    }
}
