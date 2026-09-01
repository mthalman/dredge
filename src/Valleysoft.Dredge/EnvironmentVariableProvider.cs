namespace Valleysoft.Dredge;

internal class EnvironmentVariableProvider : IEnvironmentVariableProvider
{
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);
}
