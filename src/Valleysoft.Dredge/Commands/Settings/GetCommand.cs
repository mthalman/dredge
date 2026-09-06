using Newtonsoft.Json;

namespace Valleysoft.Dredge.Commands.Settings;

internal partial class GetCommand : CommandWithOptions<GetOptions>
{
    private readonly IAppSettingsStore settingsStore;
    private readonly TextWriter output;

    public GetCommand()
        : this(new AppSettingsStore(), Console.Out)
    {
    }

    internal GetCommand(IAppSettingsStore settingsStore, TextWriter output)
        : base("get", "Gets the value of the specified setting")
    {
        this.settingsStore = settingsStore;
        this.output = output;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppSettings settings = settingsStore.Load();

        Queue<string> names = new([..Options.Name.Split('.')]);

        object? value = settings.GetProperty(names);

        if (value is not null)
        {
            if (value.GetType().IsValueType || value is string)
            {
                output.WriteLine(value);
            }
            else
            {
                output.WriteLine(JsonConvert.SerializeObject(value, JsonHelper.Settings));
            }
        }

        return Task.CompletedTask;
    }
}
