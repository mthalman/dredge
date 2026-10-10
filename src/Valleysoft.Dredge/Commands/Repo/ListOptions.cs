using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Repo;

public class ListOptions : BoundedListOptionsBase
{
    private readonly Argument<string> registryArg;
    private readonly CliOutputOption<CliOutputFormat> outputOption;

    public string Registry { get; set; } = string.Empty;
    public CliOutputFormat OutputFormat { get; set; }

    public ListOptions()
    {
        registryArg = Add(new Argument<string>("registry") { Description = "Container registry host" });
        outputOption = new CliOutputOption<CliOutputFormat>(
            "Output format",
            CliOutputFormat.Json,
            ("json", CliOutputFormat.Json),
            ("text", CliOutputFormat.Text));
        Add(outputOption.Option);
    }

    protected override void GetValues()
    {
        Registry = GetValue(registryArg);
        OutputFormat = outputOption.GetValue(GetValue(outputOption.Option));
        GetBoundedListValues();
    }
}
