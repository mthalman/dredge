using Newtonsoft.Json;
using System.Reflection;

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

        Queue<string> names = new(Options.Name.Split('.'));

        SetSettingProperty(settings, names, Options.Value);
        settings.Save();
        return Task.CompletedTask;
    }

    private void SetSettingProperty(object obj, Queue<string> names, string value)
    {
        string propertyName = names.Dequeue();
        PropertyInfo? property = obj.GetType().GetProperties()
            .FirstOrDefault(prop => prop.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName == propertyName);
        if (property is null)
        {
            throw new Exception($"Could not find property '{propertyName}' in object of type '{obj.GetType()}'");
        }

        if (names.Any())
        {
            object? propertyValue = property.GetValue(obj);
            if (propertyValue is null)
            {
                propertyValue = Activator.CreateInstance(property.PropertyType);
                if (propertyValue is null)
                {
                    throw new Exception($"Unable to create instance of '{property.PropertyType}'.");
                }
            }
            property.SetValue(obj, propertyValue);

            SetSettingProperty(propertyValue, names, value);
        }
        else
        {
            property.SetValue(obj, value);
        }
    }
}
