using Newtonsoft.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Globalization;

namespace Valleysoft.Dredge.Commands.Image;

public class LsCommand : RegistryCommandBase<LsOptions>
{
    private const int DisplayDigestLength = 12;
    private readonly IAnsiConsole ansiConsole;

    public LsCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        IAnsiConsole? ansiConsole = null)
        : base(
            "ls",
            "Lists files in an image filesystem with layer provenance",
            dockerRegistryClientFactory)
    {
        this.ansiConsole = ansiConsole ?? AnsiConsole.Console;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client =
                await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            ImageFileSystem fileSystem =
                await ImageFileSystem.CreateAsync(client, imageName, Options, ct);
            IReadOnlyList<ImageFileSystemEntry> entries =
                fileSystem.List(Options.Path, Options.Recursive, Options.ShowDeleted);

            if (Options.OutputFormat == LsOutput.Json)
            {
                ansiConsole.Profile.Out.Writer.WriteLine(
                    JsonConvert.SerializeObject(entries, JsonHelper.Settings));
            }
            else
            {
                WriteTextOutput(entries, Options);
            }
        });
    }

    private void WriteTextOutput(
        IEnumerable<ImageFileSystemEntry> entries,
        LsOptions options)
    {
        ImageFileSystemEntry[] entryArray = entries.ToArray();
        string listedPath = ImagePath.NormalizeRequested(options.Path);
        if (!options.Long && !options.ShowProvenance)
        {
            IEnumerable<string> namePaths = entryArray.Select(
                entry => FormatPath(entry, listedPath));
            if (ansiConsole.Profile.Out.IsTerminal)
            {
                ansiConsole.Write(new Columns(namePaths.Select(path => new Text(path))));
            }
            else
            {
                foreach (string path in namePaths)
                {
                    ansiConsole.Profile.Out.Writer.WriteLine(path);
                }
            }
            return;
        }

        string[] paths = entryArray
            .Select(entry => options.Long
                ? FormatLongPath(entry, listedPath)
                : FormatPath(entry, listedPath))
            .ToArray();
        int userIdWidth = entryArray
            .Select(entry => entry.UserId.ToString(CultureInfo.InvariantCulture).Length)
            .DefaultIfEmpty()
            .Max();
        int groupIdWidth = entryArray
            .Select(entry => entry.GroupId.ToString(CultureInfo.InvariantCulture).Length)
            .DefaultIfEmpty()
            .Max();
        int sizeWidth = entryArray
            .Select(entry => entry.Size.ToString(CultureInfo.InvariantCulture).Length)
            .DefaultIfEmpty()
            .Max();
        for (int index = 0; index < entryArray.Length; index++)
        {
            ImageFileSystemEntry entry = entryArray[index];
            TextWriter writer = ansiConsole.Profile.Out.Writer;
            if (options.Long)
            {
                writer.Write(FormatMode(entry));
                writer.Write(' ');
                writer.Write(entry.UserId.ToString(CultureInfo.InvariantCulture).PadLeft(userIdWidth));
                writer.Write(' ');
                writer.Write(entry.GroupId.ToString(CultureInfo.InvariantCulture).PadLeft(groupIdWidth));
                writer.Write(' ');
                writer.Write(entry.Size.ToString(CultureInfo.InvariantCulture).PadLeft(sizeWidth));
                writer.Write(' ');
                writer.Write(entry.ModifiedTime?.ToString(
                    "yyyy-MM-dd HH:mm'Z'",
                    CultureInfo.InvariantCulture) ?? new string(' ', 17));
                writer.Write(' ');
            }
            writer.Write(paths[index]);
            if (options.ShowProvenance)
            {
                writer.Write("  ");
                writer.Write(FormatLayers(entry));
            }
            writer.WriteLine();
        }
    }

    private static string FormatPath(
        ImageFileSystemEntry entry,
        string listedPath)
    {
        string path = listedPath.Length > 0 &&
            entry.Path.StartsWith($"{listedPath}/", StringComparison.Ordinal)
                ? entry.Path[(listedPath.Length + 1)..]
                : entry.Path;
        return $"{path}{(entry.IsDeleted ? " (deleted)" : string.Empty)}";
    }

    private static string FormatLongPath(
        ImageFileSystemEntry entry,
        string listedPath)
    {
        string path = FormatPath(entry, listedPath);
        return entry.LinkTarget is null ? path : $"{path} -> {entry.LinkTarget}";
    }

    private static string FormatMode(ImageFileSystemEntry entry)
    {
        char type = entry.Type switch
        {
            ImageFileType.Directory => 'd',
            ImageFileType.SymbolicLink => 'l',
            ImageFileType.File or ImageFileType.HardLink => '-',
            _ => '?'
        };
        Span<char> permissions = stackalloc char[10];
        permissions[0] = type;
        int[] masks = [0x100, 0x80, 0x40, 0x20, 0x10, 0x8, 0x4, 0x2, 0x1];
        char[] symbols = ['r', 'w', 'x', 'r', 'w', 'x', 'r', 'w', 'x'];
        for (int index = 0; index < masks.Length; index++)
        {
            permissions[index + 1] = (entry.Mode & masks[index]) == 0
                ? '-'
                : symbols[index];
        }
        permissions[3] = FormatSpecialPermission(
            permissions[3], entry.Mode, 0x800, 's', 'S');
        permissions[6] = FormatSpecialPermission(
            permissions[6], entry.Mode, 0x400, 's', 'S');
        permissions[9] = FormatSpecialPermission(
            permissions[9], entry.Mode, 0x200, 't', 'T');
        return new string(permissions);
    }

    private static char FormatSpecialPermission(
        char executePermission,
        int mode,
        int mask,
        char withExecute,
        char withoutExecute) =>
        (mode & mask) == 0
            ? executePermission
            : executePermission == 'x' ? withExecute : withoutExecute;

    private static string FormatLayer(ImageLayerReference? layer)
    {
        if (layer is null)
        {
            return string.Empty;
        }

        int separatorIndex = layer.Digest.IndexOf(':');
        string value = separatorIndex < 0
            ? layer.Digest
            : layer.Digest[(separatorIndex + 1)..];
        string abbreviatedDigest = value.Length <= DisplayDigestLength
            ? value
            : value[..DisplayDigestLength];
        return $"{layer.Index}:{abbreviatedDigest}";
    }

    private static string FormatLayers(ImageFileSystemEntry entry)
    {
        List<string> layers = [$"i={FormatLayer(entry.IntroducedLayer)}"];
        if (entry.ModifiedLayer is not null)
        {
            layers.Add($"m={FormatLayer(entry.ModifiedLayer)}");
        }
        if (entry.DeletedLayer is not null)
        {
            layers.Add($"d={FormatLayer(entry.DeletedLayer)}");
        }
        return string.Join(' ', layers);
    }
}
