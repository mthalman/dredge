using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Valleysoft.Dredge.Explorer;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerNativeArgvTests
{
    [Theory]
    [InlineData("Standard", false)]
    [InlineData("Windows", false)]
    [InlineData("Standard", true)]
    [InlineData("Windows", true)]
    public async Task SupportedPowerShellModesPreserveArgumentsAcrossANativeExecutableBoundary(string mode, bool copiedCommand)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Validates the Windows native PowerShell argument contract.");
        string[] expected;
        string arguments;
        if (copiedCommand)
        {
            const string path = "app/a 'file' \"$HOME\"; & `text` \u754c.txt";
            ExplorerPresenter presenter = new(ExplorerSamples.Image(), 150, 42);
            arguments = presenter.CopyCommandText(new(), path, false)["dredge ".Length..];
            expected = ["image", "cat", presenter.Image.ResolvedReference, "/" + path];
        }
        else
        {
            expected = ["", "plain", "\"double quotes\"", "'single quotes'", @"C:\path with spaces\",
                "$HOME; & `code`", "line one\nline two", "\u754c \ud83d\udc1f"];
            arguments = string.Join(" ", expected.Select(value => ShellCommand.Quote(value, powerShell: true)));
        }
        string receiver = Path.Combine(AppContext.BaseDirectory, "TestData", "Explorer", "NativeArgv.ps1");
        string script = "$ErrorActionPreference='Stop'; " +
            "if ($PSVersionTable.PSVersion -lt [Version]'7.3') { throw 'PowerShell 7.3 or newer is required.' }; " +
            $"$PSNativeCommandArgumentPassing='{mode}'; $native=(Get-Process -Id $PID).Path; " +
            $"& $native -NoLogo -NoProfile -NonInteractive -File {ShellCommand.Quote(receiver, powerShell: true)} {arguments}; " +
            "exit $LASTEXITCODE";
        ProcessStartInfo start = new("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            start.ArgumentList.Add(argument);
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using Process process = Process.Start(start)!;
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await errors);
            Assert.Equal(expected, JsonSerializer.Deserialize<string[]>(await output));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
