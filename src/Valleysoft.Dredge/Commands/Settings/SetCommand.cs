namespace Valleysoft.Dredge.Commands.Settings;

internal partial class SetCommand : CommandWithOptions<SetOptions>
{
    public SetCommand()
        : base("set", "Sets the specified setting name to a value")
    {
    }

    protected override Task ExecuteAsync()
    {
        AppSettings settings = AppSettings.Load();

        Queue<string> names = new([..Options.Name.Split('.')]);

        settings.SetProperty(names, Options.Value);

        settings.Save();
        return Task.CompletedTask;
    }
}
