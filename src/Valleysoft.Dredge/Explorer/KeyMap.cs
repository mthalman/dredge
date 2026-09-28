namespace Valleysoft.Dredge.Explorer;

internal enum KeyAction
{
    Quit, Help, Insights, Search, WholeFilesystem, Compare, FindingsOnly,
    PreviousLayer, NextLayer, ToggleAdded, ToggleModified, ToggleIdentical, ToggleDeleted,
    Extract, CopyCommand, Viewer, SwapSides, Retry, Packages
}

// Single-character action keys, matched by the typed character so they work
// on any keyboard layout. Settings under explore.keys.<action> remap them.
internal sealed class KeyMap
{
    private static readonly Dictionary<KeyAction, char> Defaults = new()
    {
        [KeyAction.Quit] = 'q',
        [KeyAction.Help] = '?',
        [KeyAction.Insights] = 'i',
        [KeyAction.Search] = '/',
        [KeyAction.WholeFilesystem] = 'a',
        [KeyAction.Compare] = 'c',
        [KeyAction.FindingsOnly] = 'w',
        [KeyAction.PreviousLayer] = '[',
        [KeyAction.NextLayer] = ']',
        [KeyAction.ToggleAdded] = '+',
        [KeyAction.ToggleModified] = '~',
        [KeyAction.ToggleIdentical] = '=',
        [KeyAction.ToggleDeleted] = '-',
        [KeyAction.Extract] = 'x',
        [KeyAction.CopyCommand] = '\u0003',
        [KeyAction.Viewer] = 'o',
        [KeyAction.SwapSides] = 's',
        [KeyAction.Retry] = 'r',
        [KeyAction.Packages] = 'p',
    };

    private readonly Dictionary<KeyAction, char> keys;
    private readonly Dictionary<char, KeyAction> actions;

    private KeyMap(Dictionary<KeyAction, char> keys)
    {
        this.keys = keys;
        actions = keys.ToDictionary(pair => pair.Value, pair => pair.Key);
    }

    public static KeyMap Default { get; } = new(new(Defaults));

    public char this[KeyAction action] => keys[action];

    public string Label(KeyAction action) => keys[action] == '\u0003' ? "^C" : keys[action].ToString();

    public KeyAction? Lookup(char key) => actions.TryGetValue(key, out KeyAction action) ? action : null;

    public static KeyMap FromSettings(ExploreKeysSettings settings)
    {
        Dictionary<KeyAction, char> map = new(Defaults);
        foreach (KeyAction action in Enum.GetValues<KeyAction>())
        {
            string name = char.ToLowerInvariant(action.ToString()[0]) + action.ToString()[1..];
            string value = Setting(settings, action) ?? "";
            if (value.Length == 0)
            {
                continue;
            }
            if (value.Length != 1 || value[0] <= ' ' || value[0] > '~')
            {
                throw new InvalidOperationException(
                    $"Invalid explore.keys.{name} value '{value}'; expected a single printable ASCII character.");
            }
            map[action] = value[0];
        }
        foreach (IGrouping<char, KeyAction> duplicate in map.GroupBy(pair => pair.Value, pair => pair.Key)
            .Where(group => group.Count() > 1))
        {
            throw new InvalidOperationException(
                $"The explorer key '{duplicate.Key}' is assigned to more than one action: " +
                string.Join(", ", duplicate.Select(action => action.ToString())) + ".");
        }
        return new(map);
    }

    private static string? Setting(ExploreKeysSettings settings, KeyAction action) => action switch
    {
        KeyAction.Quit => settings.Quit,
        KeyAction.Help => settings.Help,
        KeyAction.Insights => settings.Insights,
        KeyAction.Search => settings.Search,
        KeyAction.WholeFilesystem => settings.WholeFilesystem,
        KeyAction.Compare => settings.Compare,
        KeyAction.FindingsOnly => settings.FindingsOnly,
        KeyAction.PreviousLayer => settings.PreviousLayer,
        KeyAction.NextLayer => settings.NextLayer,
        KeyAction.ToggleAdded => settings.ToggleAdded,
        KeyAction.ToggleModified => settings.ToggleModified,
        KeyAction.ToggleIdentical => settings.ToggleIdentical,
        KeyAction.ToggleDeleted => settings.ToggleDeleted,
        KeyAction.Extract => settings.Extract,
        KeyAction.CopyCommand => settings.CopyCommand,
        KeyAction.Viewer => settings.Viewer,
        KeyAction.SwapSides => settings.SwapSides,
        KeyAction.Retry => settings.Retry,
        KeyAction.Packages => settings.Packages,
        _ => null
    };
}
