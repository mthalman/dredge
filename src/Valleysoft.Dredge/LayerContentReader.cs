using System.IO.Compression;

namespace Valleysoft.Dredge;

internal sealed class LayerContentReader : IDisposable
{
    private readonly GZipStream gzip;
    private readonly byte[] buffer = new byte[81920];
    private long position;

    public LayerContentReader(Stream blob)
    {
        gzip = new(blob, CompressionMode.Decompress, leaveOpen: true);
    }

    public async Task CopyToAsync(ScannedEntry entry, Stream output, CancellationToken cancellationToken)
    {
        // Offsets come from a verified index, not from headers in an unverified ranged prefix.
        await LayerStore.CopyBytesAsync(gzip, Stream.Null, entry.UncompressedOffset - position, buffer, cancellationToken);
        await LayerStore.CopyBytesAsync(gzip, output, entry.Size, buffer, cancellationToken);
        position = checked(entry.UncompressedOffset + entry.Size);
    }

    public void Dispose() => gzip.Dispose();
}
