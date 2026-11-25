using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public enum SemanticCompareOutput { Text, Json }

public sealed class CompareOptions : CompareOptionsBase
{
    internal static readonly string[] AllScopes = ["files", "packages", "config", "layers", "sizes"];
    private readonly CliOutputOption<SemanticCompareOutput> output;
    private readonly Option<string[]> scopes;
    private readonly Option<string[]> ignores;
    private readonly Option<bool> timestamps;
    private readonly Option<int> limit;
    private readonly Option<bool> noColor;

    public SemanticCompareOutput OutputFormat { get; set; }
    public string[] Scopes { get; set; } = AllScopes;
    public string[] IgnorePaths { get; set; } = [];
    public bool IncludeTimestamps { get; set; }
    public int Limit { get; set; } = 100;
    public bool IsColorDisabled { get; set; }

    public CompareOptions()
    {
        output = new("Output format", SemanticCompareOutput.Text,
            ("text", SemanticCompareOutput.Text), ("json", SemanticCompareOutput.Json));
        Add(output.Option);
        scopes = Add(new Option<string[]>("--scope")
        {
            Description = "Select files, packages, config, layers, or sizes (repeatable; default all)",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false
        });
        ignores = Add(new Option<string[]>("--ignore-path")
        {
            Description = "Ignore filesystem differences matching an image-relative glob (repeatable)",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false
        });
        timestamps = Add(new Option<bool>("--include-timestamps") { Description = "Compare file modification timestamps" });
        limit = Add(new Option<int>("--limit") { Description = "Maximum text detail rows; JSON is unlimited", DefaultValueFactory = _ => 100 });
        noColor = Add(new Option<bool>("--no-color") { Description = "Disable color output" });
    }

    public override void SetCommandOptions(Command cmd)
    {
        base.SetCommandOptions(cmd);
        cmd.Validators.Add(result =>
        {
            foreach (string scope in result.GetValue(scopes) ?? [])
            {
                if (!AllScopes.Contains(scope, StringComparer.Ordinal))
                {
                    result.AddError($"Invalid scope '{scope}'. Expected {string.Join(", ", AllScopes)}.");
                }
            }
            foreach (string pattern in result.GetValue(ignores) ?? [])
            {
                try { _ = ImageComparison.CreateIgnore(pattern, false); }
                catch (ArgumentException exception) { result.AddError(exception.Message); }
            }
            if (result.GetValue(limit) < 0) { result.AddError("--limit must be nonnegative."); }
        });
    }

    protected override void GetValues()
    {
        base.GetValues();
        OutputFormat = output.GetValue(GetValue(output.Option));
        string[] selected = GetValue(scopes) ?? [];
        Scopes = selected.Length == 0 ? AllScopes : AllScopes.Where(selected.Contains).ToArray();
        IgnorePaths = (GetValue(ignores) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        IncludeTimestamps = GetValue(timestamps);
        Limit = GetValue(limit);
        IsColorDisabled = GetValue(noColor);
    }
}
