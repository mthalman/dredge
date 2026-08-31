namespace Valleysoft.Dredge;

internal interface IEnvironmentVariableProvider
{
    string? GetVariable(string name);
}
