using System.CommandLine;
using System.CommandLine.Completions;

namespace Valleysoft.Dredge.Commands;

internal interface IJsonOutputOption
{
}

internal sealed class JsonOutputOption : Option<string>, IJsonOutputOption
{
    public JsonOutputOption(string description, string defaultValue, params string[] values)
        : base("--output")
    {
        string expectedValues = string.Join(", ", values.Select(item => $"'{item}'"));

        Description = description;
        HelpName = string.Join('|', values);
        DefaultValueFactory = _ => defaultValue;
        CompletionSources.Add(
            _ => [.. values.Select(item => new CompletionItem(item))]);
        Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (value is null || !values.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                result.AddError(
                    $"Invalid output value '{value}'. Expected one of: {expectedValues}.");
            }
        });
    }
}

internal sealed class CliOutputPathOption : Option<string>
{
    public CliOutputPathOption(string description)
        : base("--output")
    {
        Description = description;
    }
}

internal sealed class CliOutputOption<T>
    where T : struct, Enum
{
    private readonly Dictionary<string, T> values;

    public Option<string> Option { get; }

    public CliOutputOption(
        string description,
        T defaultValue,
        params (string Name, T Value)[] values)
    {
        this.values = values.ToDictionary(
            item => item.Name,
            item => item.Value,
            StringComparer.OrdinalIgnoreCase);

        string defaultName = values.Single(
            item => EqualityComparer<T>.Default.Equals(item.Value, defaultValue)).Name;

        Option = new JsonOutputOption(
            description,
            defaultName,
            values.Select(item => item.Name).ToArray());
    }

    public T GetValue(string? value) =>
        value is not null && values.TryGetValue(value, out T mappedValue)
            ? mappedValue
            : throw new NotSupportedException($"Unsupported output value '{value}'.");
}
