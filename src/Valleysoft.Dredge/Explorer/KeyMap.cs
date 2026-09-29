namespace Valleysoft.Dredge.Explorer;

internal enum KeyAction
{
    Quit, Help, Insights, Search, WholeFilesystem, Compare, FindingsOnly,
    PreviousLayer, NextLayer, ToggleAdded, ToggleModified, ToggleIdentical, ToggleDeleted,
    Extract, CopyCommand, Viewer, SwapSides, Retry, Packages
}

// Single-character action keys, matched by the typed character so they work
// on any keyboard layout.
internal sealed class KeyMap
{
    private readonly Dictionary<KeyAction, char> keys = new()
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

    private readonly Dictionary<char, KeyAction> actions;

    private KeyMap()
    {
        actions = keys.ToDictionary(static pair => pair.Value, static pair => pair.Key);
    }

    public static KeyMap Default { get; } = new();

    public char this[KeyAction action] => keys[action];

    public string Label(KeyAction action) => keys[action] == '\u0003' ? "^C" : keys[action].ToString();

    public KeyAction? Lookup(char key) => actions.TryGetValue(key, out KeyAction action) ? action : null;

}
