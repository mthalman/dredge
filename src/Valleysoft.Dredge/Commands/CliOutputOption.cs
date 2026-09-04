using System.CommandLine;
using System.CommandLine.Completions;

namespace Valleysoft.Dredge.Commands;

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
        string expectedValues = string.Join(", ", values.Select(item => $"'{item.Name}'"));

        Option = new Option<string>("--output")
        {
            Description = description,
            HelpName = string.Join('|', values.Select(item => item.Name)),
            DefaultValueFactory = _ => defaultName
        };
        Option.CompletionSources.Add(
            _ => values.Select(item => new CompletionItem(item.Name)).ToArray());
        Option.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (value is null || !this.values.ContainsKey(value))
            {
                result.AddError(
                    $"Invalid output value '{value}'. Expected one of: {expectedValues}.");
            }
        });
    }

    public T GetValue(string? value) =>
        value is not null && values.TryGetValue(value, out T mappedValue)
            ? mappedValue
            : throw new NotSupportedException($"Unsupported output value '{value}'.");
}
