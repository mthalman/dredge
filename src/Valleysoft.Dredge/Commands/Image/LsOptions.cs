using System.CommandLine;
using System.CommandLine.Completions;

namespace Valleysoft.Dredge.Commands.Image;

public class LsOptions : PlatformOptionsBase
{
    private const string TextOutput = "text";
    private const string JsonOutput = "json";

    private readonly Argument<string> imageArgument;
    private readonly Argument<string?> pathArgument;
    private readonly Option<bool> recursiveOption;
    private readonly Option<bool> showDeletedOption;
    private readonly Option<bool> longOption;
    private readonly Option<bool> provenanceOption;
    private readonly Option<string> outputOption;

    public string Image { get; set; } = string.Empty;
    public string? Path { get; set; }
    public bool Recursive { get; set; }
    public bool ShowDeleted { get; set; }
    public bool Long { get; set; }
    public bool ShowProvenance { get; set; }
    public LsOutput OutputFormat { get; set; }

    public LsOptions()
    {
        imageArgument = Add(new Argument<string>("image")
        {
            Description = "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)"
        });
        pathArgument = Add(new Argument<string?>("path")
        {
            Description = "Image path to list",
            DefaultValueFactory = _ => null
        });
        recursiveOption = Add(new Option<bool>("--recursive")
        {
            Description = "List all descendants recursively"
        });
        showDeletedOption = Add(new Option<bool>("--show-deleted")
        {
            Description = "Include paths removed by image whiteouts"
        });
        longOption = Add(new Option<bool>("--long")
        {
            Description = "Use a long listing format"
        });
        longOption.Aliases.Add("-l");
        provenanceOption = Add(new Option<bool>("--provenance")
        {
            Description = "Show layer provenance"
        });
        outputOption = Add(new Option<string>("--output")
        {
            Description = "Output format",
            HelpName = $"{TextOutput}|{JsonOutput}",
            DefaultValueFactory = _ => TextOutput
        });
        outputOption.CompletionSources.Add(
            _ => [new CompletionItem(TextOutput), new CompletionItem(JsonOutput)]);
        outputOption.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (!string.Equals(value, TextOutput, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(value, JsonOutput, StringComparison.OrdinalIgnoreCase))
            {
                result.AddError(
                    $"Invalid output format '{value}'. Expected '{TextOutput}' or '{JsonOutput}'.");
            }
        });
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArgument);
        Path = GetValue(pathArgument);
        Recursive = GetValue(recursiveOption);
        ShowDeleted = GetValue(showDeletedOption);
        Long = GetValue(longOption);
        ShowProvenance = GetValue(provenanceOption);
        OutputFormat = GetValue(outputOption)?.ToLowerInvariant() switch
        {
            TextOutput => LsOutput.Text,
            JsonOutput => LsOutput.Json,
            var value => throw new NotSupportedException(
                $"Unsupported output format '{value}'.")
        };
    }
}
