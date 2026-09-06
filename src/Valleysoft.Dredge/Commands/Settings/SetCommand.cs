namespace Valleysoft.Dredge.Commands.Settings;

internal partial class SetCommand : CommandWithOptions<SetOptions>
{
    private readonly IAppSettingsStore settingsStore;

    public SetCommand()
        : this(new AppSettingsStore())
    {
    }

    internal SetCommand(IAppSettingsStore settingsStore)
        : base("set", "Sets the specified setting name to a value")
    {
        this.settingsStore = settingsStore;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppSettings settings = settingsStore.Load();

        Queue<string> names = new([..Options.Name.Split('.')]);

        settings.SetProperty(names, Options.Value);

        settings.Save();
        return Task.CompletedTask;
    }
}
