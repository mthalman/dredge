using System.Diagnostics;
using Spectre.Console.Rendering;
using System.Text;

namespace Valleysoft.Dredge.Tests;

public static class TestHelper
{
    public static string GetString(IEnumerable<Segment> segments)
    {
        StringBuilder builder = new();
        foreach (Segment segment in segments)
        {
            builder.Append(segment.Text);
        }
        return builder.ToString();
    }

    public static string Normalize(string val) =>
        val.Replace("\r", string.Empty).TrimEnd();
}

internal sealed class ReadCountingStream(byte[] bytes) : Stream
{
    private readonly MemoryStream content = new(bytes);
    public long BytesRead { get; private set; }
    public override bool CanRead => content.CanRead;
    public override bool CanSeek => content.CanSeek;
    public override bool CanWrite => false;
    public override long Length => content.Length;
    public override long Position { get => content.Position; set => content.Position = value; }

    public override int ReadByte()
    {
        int result = content.ReadByte();
        BytesRead += result < 0 ? 0 : 1;
        return result;
    }

    public override int Read(Span<byte> buffer)
    {
        int read = content.Read(buffer);
        BytesRead += read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = content.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => content.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            content.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class TestProcessTerminator : IProcessTerminator
{
    public void Exit(int exitCode) =>
        throw new InvalidOperationException($"Command requested process exit code {exitCode}.");
}

internal sealed class RecordingProcessTerminator : IProcessTerminator
{
    public int? ExitCode { get; private set; }

    public void Exit(int exitCode)
    {
        ExitCode = exitCode;
    }
}

internal sealed class TestAppSettingsStore(
    AppSettings settings,
    string settingsPath = "settings.json") : IAppSettingsStore
{
    public string SettingsPath { get; } = settingsPath;

    public AppSettings Load() => settings;
}

internal sealed class TestDredgePathProvider(string tempPath, string? cachePath = null) : IDredgePathProvider
{
    public string TempPath { get; } = tempPath;
    public string CachePath => cachePath ?? Path.Combine(TempPath, "cache");
}

internal sealed class TestProcessLauncher : IProcessLauncher
{
    public ProcessStartInfo? StartInfo { get; private set; }

    public void Start(ProcessStartInfo startInfo)
    {
        StartInfo = startInfo;
    }
}

internal sealed class ThrowingProcessLauncher(Exception exception) : IProcessLauncher
{
    public void Start(ProcessStartInfo startInfo) => throw exception;
}
