namespace Valleysoft.Dredge;

internal sealed class LayerContentReader : IDisposable
{
    private readonly Stream content;
    private readonly Stream? raw;
    private readonly byte[] buffer = new byte[81920];
    private long position;

    public LayerContentReader(Stream blob, string? encoding = null)
    {
        encoding ??= ImageTarReader.DetectEncoding(blob);
        content = ImageTarReader.OpenContent(blob, encoding);
        raw = encoding == "tar" && blob.CanSeek ? blob : null;
    }

    public async Task CopyToAsync(ScannedEntry entry, Stream output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.UncompressedOffset < 0 || entry.Size < 0 ||
            entry.UncompressedOffset > long.MaxValue - entry.Size)
        {
            throw new InvalidDataException("Invalid indexed content offset or size.");
        }
        long end = checked(entry.UncompressedOffset + entry.Size);
        // Callers supply a validated complete blob or a content-hash-verified ranged prefix.
        // Keep the scanner's non-seekable counting stream unchanged.
        if (raw is not null)
        {
            if (end > raw.Length)
            {
                throw new InvalidDataException("Layer content ended prematurely.");
            }
            raw.Seek(entry.UncompressedOffset, SeekOrigin.Begin);
        }
        else
        {
            await LayerStore.CopyBytesAsync(content, Stream.Null, entry.UncompressedOffset - position, buffer, cancellationToken);
        }
        await LayerStore.CopyBytesAsync(content, output, entry.Size, buffer, cancellationToken);
        position = end;
    }

    public void Dispose() => content.Dispose();
}
