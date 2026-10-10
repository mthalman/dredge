using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Tag;

public class ListOptions : BoundedListOptionsBase
{
    private readonly Argument<string> repositoryArg;
    private readonly CliOutputOption<CliOutputFormat> outputOption;

    public string Repo { get; set; } = string.Empty;
    public CliOutputFormat OutputFormat { get; set; }

    public ListOptions()
    {
        repositoryArg = Add(ImageReferenceArgument.CreateRepository());
        outputOption = new CliOutputOption<CliOutputFormat>(
            "Output format",
            CliOutputFormat.Json,
            ("json", CliOutputFormat.Json),
            ("text", CliOutputFormat.Text));
        Add(outputOption.Option);
    }

    protected override void GetValues()
    {
        Repo = GetValue(repositoryArg);
        OutputFormat = outputOption.GetValue(GetValue(outputOption.Option));
        GetBoundedListValues();
    }
}
