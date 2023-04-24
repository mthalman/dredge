using Newtonsoft.Json;
using System.Reflection;
using Valleysoft.Dredge.Core;

namespace Valleysoft.Dredge.Commands.Settings;

internal partial class GetCommand : CommandWithOptions<GetOptions>
{
    public GetCommand()
        : base("get", "Gets the value of the specified setting")
    {
    }

    protected override Task ExecuteAsync()
    {
        AppSettings settings = AppSettingsHelper.Load();

        Queue<string> names = new(Options.Name.Split('.'));

        object? value = GetSettingProperty(settings, names);

        if (value is not null)
        {
            if (value.GetType().IsValueType || value is string)
            {
                Console.WriteLine(value);
            }
            else
            {
                Console.WriteLine(JsonConvert.SerializeObject(value, Formatting.Indented));
            }
        }

        return Task.CompletedTask;
    }

    private object? GetSettingProperty(object obj, Queue<string> names)
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

            return GetSettingProperty(propertyValue, names);
        }
        else
        {
            return property.GetValue(obj);
        }
    }
}
