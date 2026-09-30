namespace Valleysoft.Dredge.Explorer;

internal sealed class ContentTotals
{
    private readonly Dictionary<ImageContentId, long> contents = [];
    private readonly HashSet<ImageContentId> linkedContents = [];
    private long untrackedBytes;

    public long Bytes { get; private set; }
    public bool HasEntries { get; private set; }
    public int HardLinks { get; private set; }
    public string? Sharing => HardLinks == 0 ? null
        : linkedContents.Count == 0 ? Fmt.Count(HardLinks, "hard link")
        : $"{Fmt.Count(HardLinks, "hard link")}, {Fmt.Count(linkedContents.Count, "shared file")}";

    public void Add(long bytes, ImageContentId? id, bool hardLink)
    {
        HasEntries = true;
        if (id is ImageContentId content)
        {
            if (contents.TryAdd(content, bytes))
            {
                Bytes += bytes;
            }
            if (hardLink)
            {
                linkedContents.Add(content);
            }
        }
        else
        {
            untrackedBytes += bytes;
            Bytes += bytes;
        }
        if (hardLink)
        {
            HardLinks++;
        }
    }

    public void Add(ImageFileSystemEntry? entry)
    {
        if (entry is not null && entry.Type != ImageFileType.Directory)
        {
            Add(entry.Size, entry.Type is ImageFileType.File or ImageFileType.HardLink ? entry.ContentId : null,
                entry.Type == ImageFileType.HardLink);
        }
    }

    public void UnionWith(ContentTotals other)
    {
        foreach ((ImageContentId id, long bytes) in other.contents)
        {
            if (contents.TryAdd(id, bytes))
            {
                Bytes += bytes;
            }
        }
        Bytes += other.untrackedBytes;
        untrackedBytes += other.untrackedBytes;
        HasEntries |= other.HasEntries;
        HardLinks += other.HardLinks;
        linkedContents.UnionWith(other.linkedContents);
    }
}
