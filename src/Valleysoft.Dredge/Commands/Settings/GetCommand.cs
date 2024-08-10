using Newtonsoft.Json;

namespace Valleysoft.Dredge.Commands.Settings;

internal partial class GetCommand : CommandWithOptions<GetOptions>
{
    public GetCommand()
        : base("get", "Gets the value of the specified setting")
    {
    }

    protected override Task ExecuteAsync()
    {
        AppSettings settings = AppSettings.Load();

        Queue<string> names = new(Options.Name.Split('.'));

        object? value = settings.GetProperty(names);

        if (value is not null)
        {
            if (value.GetType().IsValueType || value is string)
            {
                Console.WriteLine(value);
            }
            else
            {
                Console.WriteLine(JsonConvert.SerializeObject(value, JsonHelper.Settings));
            }
        }

        return Task.CompletedTask;
    }
}
