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
                    $"{error} Expected <{name}>, <{name}>:<tag>, or <{name}>@<digest>.");
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

    public static Argument<string> CreateForDeletion(bool tagOnly)
    {
        string syntax = tagOnly ? "<image>:<tag>" : "<image>:<tag> or <image>@<digest>";
        Argument<string> argument = new("image")
        {
            Description = $"Explicit reference to delete ({syntax})"
        };
        argument.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (!ImageName.TryParse(value, out ImageName? image, out string? error))
            {
                result.AddError($"{error} Expected {syntax}.");
            }
            else if (tagOnly && image.Digest is not null)
            {
                result.AddError("Tag deletion requires an explicit tag, not a digest. Expected <image>:<tag>.");
            }
            else if (image.Digest is null &&
                !value![(value.LastIndexOf('/') + 1)..].Contains(':'))
            {
                result.AddError($"Deletion requires an explicit tag or digest; 'latest' is not implied. Expected {syntax}.");
            }
        });
        return argument;
    }
}
