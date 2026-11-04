using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Valleysoft.Dredge.Explorer;

namespace Valleysoft.Dredge.Tests;

public sealed class ExplorerNativeArgvTests
{
    [Fact]
    public async Task CopiedVersionlessCommandPassesEmptyVersionThroughShellToCliParser()
    {
        ExplorerImage image = new("docker://local", "linux/amd64", "sha256:" + new string('a', 64), [], [], null)
        {
            PlatformArguments = ExplorerImage.PlatformArgumentsFor(new("linux", "amd64", null, null)),
            ConnectionArguments = "--docker-host ssh://unsupported"
        };
        string arguments = new ExplorerPresenter(image, 150, 42).CopyCommandText("file", false)["dredge ".Length..];
        string command = $"dotnet {ShellCommand.Quote(CliProcessTests.GetDredgeAssemblyPath())} {arguments}";
        bool windows = OperatingSystem.IsWindows();
        ProcessStartInfo start = new(windows ? "pwsh" : "/bin/sh")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true
        };
        string state = Path.Combine(Directory.GetCurrentDirectory(), ".test-platform-shell-" + Guid.NewGuid().ToString("N"));
        start.Environment["DREDGE_FIRST_RUN_STATE_DIRECTORY"] = state;
        foreach (string variable in new[] { "DOCKER_TLS_VERIFY", "DOCKER_TLS", "DOCKER_CERT_PATH" })
        {
            start.Environment.Remove(variable);
        }
        if (windows)
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
                "$PSNativeCommandArgumentPassing='Standard'; " + command + "; exit $LASTEXITCODE")));
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string text = await output + await errors;
            Assert.Equal(1, process.ExitCode);
            Assert.Contains("Unsupported Docker endpoint", text);
            Assert.DoesNotContain("Required argument missing", text);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            if (Directory.Exists(state))
            {
                Directory.Delete(state, recursive: true);
            }
        }
    }

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
            ExplorerImage image = new(ExplorerSamples.Reference, "linux/amd64", "sha256:manifest", [], [], null)
            {
                PlatformArguments = ExplorerImage.PlatformArgumentsFor(new("linux", "amd64", null, null))
            };
            ExplorerPresenter presenter = new(image, 150, 42);
            arguments = presenter.CopyCommandText(path, false)["dredge ".Length..];
            expected = ["image", "cat", presenter.Image.ResolvedReference, "/" + path,
                "--os", "linux", "--arch", "amd64", "--os-version="];
        }
        else
        {
            expected = ["", "plain", "\"double quotes\"", "'single quotes'", @"C:\path with spaces\",
                "$HOME; & `code`", "line one\nline two", "\u754c \ud83d\udc1f"];
            arguments = string.Join(" ", expected.Select(static value => ShellCommand.Quote(value, powerShell: true)));
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
