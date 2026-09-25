namespace Valleysoft.Dredge;

using System.Runtime.InteropServices;

internal static class ImageFileSystemExtractor
{
    public static async Task CopyContentEntriesAsync(
        IEnumerable<(ImageFileSystemEntry Entry, Stream Destination)> requests,
        LayerStore store,
        IDockerRegistryClient client,
        ImageName imageName,
        Func<int, CancellationToken, Task<StoredLayerIndex>> getIndexAsync,
        CancellationToken cancellationToken)
    {
        List<(ImageFileSystemEntry Entry, Stream Destination)> requestList = requests.ToList();
        foreach (IGrouping<int, (ImageFileSystemEntry Entry, Stream Destination)> group in
            requestList.GroupBy(request => request.Entry.ContentLayerIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<(string Path, int EntryIndex), Queue<Stream>> destinations = group
                .GroupBy(request => (
                    Path: request.Entry.ContentPath ?? request.Entry.Path,
                    EntryIndex: request.Entry.ContentEntryIndex))
                .ToDictionary(
                    item => item.Key,
                    item => new Queue<Stream>(item.Select(request => request.Destination)));

            StoredLayerIndex index = await getIndexAsync(group.Key, cancellationToken);
            using Stream blob = await store.OpenIndexedBlobAsync(
                client, imageName, index, destinations.Keys.Select(key => key.EntryIndex), cancellationToken);
            using LayerContentReader reader = new(blob);
            foreach (ScannedEntry entry in index.Changes.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!destinations.Remove(
                    (entry.Path, entry.EntryIndex),
                    out Queue<Stream>? outputs))
                {
                    continue;
                }

                using FileStream? buffer = outputs.Count > 1 ? store.CreateScratchFile() : null;
                Stream first = buffer ?? outputs.Dequeue();
                await reader.CopyToAsync(entry, first, cancellationToken);
                if (buffer is not null)
                {
                    foreach (Stream output in outputs)
                    {
                        buffer.Position = 0;
                        await buffer.CopyToAsync(output, cancellationToken);
                    }
                }
            }

            if (destinations.Count > 0)
            {
                throw new InvalidDataException(
                    $"Could not locate effective content for '/{destinations.Keys.First().Path}' in layer {group.Key}.");
            }
        }
    }

    public static async Task ExtractContentEntriesAsync(
        IEnumerable<(ImageFileSystemEntry Entry, string Destination)> requests,
        LayerStore store,
        IDockerRegistryClient client,
        ImageName imageName,
        Func<int, CancellationToken, Task<StoredLayerIndex>> getIndexAsync,
        CancellationToken cancellationToken)
    {
        foreach (IGrouping<int, (ImageFileSystemEntry Entry, string Destination)> group in
            requests.GroupBy(request => request.Entry.ContentLayerIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<(string Path, int EntryIndex), Queue<string>> destinations = group
                .GroupBy(request => (
                    Path: request.Entry.ContentPath ?? request.Entry.Path,
                    EntryIndex: request.Entry.ContentEntryIndex))
                .ToDictionary(
                    item => item.Key,
                    item => new Queue<string>(item.Select(request => request.Destination)));

            StoredLayerIndex index = await getIndexAsync(group.Key, cancellationToken);
            using Stream blob = await store.OpenIndexedBlobAsync(
                client, imageName, index, destinations.Keys.Select(key => key.EntryIndex), cancellationToken);
            using LayerContentReader reader = new(blob);
            foreach (ScannedEntry entry in index.Changes.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!destinations.Remove(
                    (entry.Path, entry.EntryIndex),
                    out Queue<string>? outputs))
                {
                    continue;
                }

                string first = outputs.Dequeue();
                Directory.CreateDirectory(Path.GetDirectoryName(first)!);
                await using (FileStream destination = new(
                    first,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.Asynchronous))
                {
                    await reader.CopyToAsync(entry, destination, cancellationToken);
                }

                foreach (string output in outputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.Copy(first, output);
                }
            }

            if (destinations.Count > 0)
            {
                throw new InvalidDataException(
                    $"Could not locate effective content for '/{destinations.Keys.First().Path}' in layer {group.Key}.");
            }
        }
    }

    public static void CreateExtractionRoot(ExtractionPlan plan, ExtractionState state)
    {
        if (plan.ExtractingRoot || plan.Source!.Type == ImageFileType.Directory)
        {
            Directory.CreateDirectory(plan.OutputPath);
            state.OutputCreated = true;
        }
    }

    public static void CreateExtractionSubdirectories(
        ExtractionPlan plan,
        CancellationToken cancellationToken)
    {
        foreach (ImageFileSystemEntry directory in plan.Entries
            .Where(entry => entry.Type == ImageFileType.Directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(plan.Destinations[directory.Path]);
        }
    }

    public static void CreatePreservedHardLinks(
        ExtractionPlan plan,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        List<ImageFileSystemEntry> pending = plan.Entries
            .Where(entry =>
                entry.Type == ImageFileType.HardLink &&
                plan.PreservableHardLinks.Contains(entry.Path))
            .ToList();
        // Multiple passes allow hard-link chains whose immediate target has not been
        // materialized yet, without replacing them with independent file copies.
        while (pending.Count > 0)
        {
            int createdCount = 0;
            for (int index = pending.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImageFileSystemEntry hardLink = pending[index];
                string target = plan.HardLinkTargets[hardLink.Path];
                if (!File.Exists(plan.Destinations[target]))
                {
                    continue;
                }
                FileHelper.CreateHardLink(
                    plan.Destinations[hardLink.Path],
                    plan.Destinations[target]);
                state.OutputCreated = true;
                pending.RemoveAt(index);
                createdCount++;
            }
            if (createdCount == 0)
            {
                throw new InvalidDataException(
                    $"Unable to create hard link '/{pending[0].Path}'.");
            }
        }
    }

    public static void CreateSymbolicLinks(
        ExtractionPlan plan,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        foreach (ImageFileSystemEntry symbolicLink in plan.Entries
            .Where(entry => entry.Type == ImageFileType.SymbolicLink))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = symbolicLink.LinkTarget ??
                throw new InvalidDataException(
                    $"Symbolic link '/{symbolicLink.Path}' has no target.");
            FileHelper.CreateSymbolicLink(
                plan.Destinations[symbolicLink.Path],
                target,
                plan.SymbolicLinkDirectoryTargets[symbolicLink.Path]);
            state.OutputCreated = true;
        }
    }

    public static void CreateSymbolicHardLinks(
        ExtractionPlan plan,
        ExtractionState state,
        CancellationToken cancellationToken)
    {
        foreach (ImageFileSystemEntry hardLink in plan.Entries
            .Where(entry =>
                entry.Type == ImageFileType.HardLink &&
                entry.ContentLinkTarget is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileHelper.CreateSymbolicLink(
                plan.Destinations[hardLink.Path],
                hardLink.ContentLinkTarget!,
                plan.SymbolicLinkDirectoryTargets[hardLink.Path]);
            state.OutputCreated = true;
        }
    }

    public static void ApplyExtractionMetadata(ExtractionPlan plan)
    {
        foreach (ImageFileSystemEntry entry in plan.Entries
            .Where(entry =>
                entry.Type is ImageFileType.File or ImageFileType.Directory ||
                (entry.Type == ImageFileType.HardLink &&
                    !plan.PreservableHardLinks.Contains(entry.Path) &&
                    entry.ContentLinkTarget is null))
            .OrderByDescending(entry => entry.Path.Count(c => c == '/')))
        {
            ApplyMetadata(plan.Destinations[entry.Path], entry);
        }
    }

    public static void ValidateNewDestination(string outputPath)
    {
        if (PathExists(outputPath))
        {
            throw new IOException($"Destination '{outputPath}' already exists.");
        }
    }

    public static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public static string? GetMissingParentRoot(string outputPath)
    {
        string? missingRoot = null;
        string? parent = Path.GetDirectoryName(outputPath);
        while (parent is not null && !PathExists(parent))
        {
            missingRoot = parent;
            parent = Path.GetDirectoryName(parent);
        }
        return missingRoot;
    }

    public static string GetContainedDestination(string root, string relativePath)
    {
        if (OperatingSystem.IsWindows())
        {
            ValidateWindowsDestinationPath(relativePath);
        }

        string result = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(root, result);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Extraction path '{relativePath}' is outside the destination.");
        }
        return result;
    }

    public static void ValidateWindowsDestinationPath(string relativePath)
    {
        foreach (string segment in relativePath.Split('/'))
        {
            string stem = segment.Split('.')[0];
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                segment.EndsWith('.') ||
                segment.EndsWith(' ') ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                IsWindowsNumberedDevice(stem, "COM") ||
                IsWindowsNumberedDevice(stem, "LPT"))
            {
                throw new InvalidDataException(
                    $"Extraction path '{relativePath}' is not a valid Windows path.");
            }
        }
    }

    public static bool IsWindowsNumberedDevice(string value, string prefix) =>
        value.Length == prefix.Length + 1 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[^1] is >= '1' and <= '9';

    public static void ApplyMetadata(string path, ImageFileSystemEntry entry)
    {
        if (entry.ModifiedTime is DateTime modifiedTime)
        {
            DateTime utcModifiedTime = DateTime.SpecifyKind(modifiedTime, DateTimeKind.Utc);
            if (entry.Type == ImageFileType.Directory)
            {
                Directory.SetLastWriteTimeUtc(path, utcModifiedTime);
            }
            else
            {
                File.SetLastWriteTimeUtc(path, utcModifiedTime);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, (UnixFileMode)(entry.Mode & 0xFFF));
        }
    }

    public static void DeleteOutput(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static void CleanupFailedExtraction(
        ExtractionPlan plan,
        bool outputCreated,
        Exception exception)
    {
        if (outputCreated)
        {
            try
            {
                DeleteOutput(plan.OutputPath);
            }
            catch (Exception cleanupException)
            {
                exception.Data["ExtractionCleanupException"] = cleanupException;
            }
        }
        if (plan.MissingParentRoot is not null &&
            Directory.Exists(plan.MissingParentRoot))
        {
            try
            {
                Directory.Delete(plan.MissingParentRoot, recursive: true);
            }
            catch (Exception cleanupException)
            {
                exception.Data["ExtractionParentCleanupException"] = cleanupException;
            }
        }
    }

    internal sealed class ExtractionState
    {
        public bool OutputCreated { get; set; }
    }
}
