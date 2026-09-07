using System.CommandLine;

namespace Valleysoft.Dredge.Commands;

internal static class ImageReferenceArgument
{
    public const string Syntax = "(<image>, <image>:<tag>, or <image>@<digest>)";

    public static Argument<string> Create(string name, string subject = "Container image")
    {
        Argument<string> argument = new(name)
        {
            Description = $"{subject} reference {Syntax}"
        };
        argument.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (!ImageName.TryParse(value, out _, out string? error))
            {
                result.AddError(
                    $"{error} Expected <image>, <image>:<tag>, or <image>@<digest>.");
            }
        });
        return argument;
    }

    public static Argument<string> CreateRepository()
    {
        Argument<string> argument = new("repository")
        {
            Description = "Container repository name"
        };
        argument.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (!ImageName.TryParseRepository(value, out _, out string? error))
            {
                result.AddError(
                    $"{error} Expected <repository> or <registry>/<repository>.");
            }
        });
        return argument;
    }
}
