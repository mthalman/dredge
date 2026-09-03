using System.Formats.Tar;

namespace Valleysoft.Dredge;

internal static class ImageTarReader
{
    public static async ValueTask<TarEntry?> GetNextEntryAsync(
        TarReader reader,
        ImageLayerReference layer,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.GetNextEntryAsync(
                copyData: false,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or NotSupportedException)
        {
            throw CreateInvalidLayerException(layer, exception);
        }
    }

    public static async Task DrainEntryAsync(
        TarEntry entry,
        ImageLayerReference layer,
        CancellationToken cancellationToken)
    {
        if (entry.DataStream is null)
        {
            return;
        }

        // On .NET 9, GetNextEntryAsync(copyData: false) does not reliably advance past
        // skipped data, so every unconsumed entry must be drained before reading the next.
        try
        {
            await entry.DataStream.CopyToAsync(Stream.Null, cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or NotSupportedException)
        {
            throw CreateInvalidLayerException(layer, exception);
        }
    }

    public static async Task CopyEntryAsync(
        TarEntry entry,
        Stream destination,
        ImageLayerReference layer,
        string path,
        CancellationToken cancellationToken)
    {
        Stream data = entry.DataStream ??
            throw new InvalidDataException($"File '/{path}' has no content stream.");
        try
        {
            await data.CopyToAsync(destination, cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or NotSupportedException)
        {
            throw CreateInvalidLayerException(layer, exception);
        }
    }

    private static InvalidDataException CreateInvalidLayerException(
        ImageLayerReference layer,
        Exception innerException) =>
        new(
            $"Layer {layer.Index} ('{layer.Digest}') is not a supported gzip-compressed Linux tar layer.",
            innerException);
}
