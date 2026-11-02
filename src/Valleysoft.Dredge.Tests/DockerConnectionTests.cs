using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Explorer;

namespace Valleysoft.Dredge.Tests;

public class DockerConnectionTests : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(".test-docker-contexts", Guid.NewGuid().ToString("N")));

    [Fact]
    public void ExplicitHost_BypassesAmbientContext()
    {
        DockerConnection connection = DockerConnection.Resolve(null, "tcp://localhost:1234",
            Environment(("DOCKER_CONTEXT", "missing"), ("DOCKER_HOST", "tcp://localhost:9999")));
        Assert.Equal("http://localhost:1234/", connection.Endpoint.AbsoluteUri);
        Assert.Null(connection.Context);
    }

    [Fact]
    public void ExplicitContext_BypassesAmbientHost()
    {
        WriteContext("chosen", "unix:///chosen.sock");
        DockerConnection connection = DockerConnection.Resolve("chosen", null,
            Environment(("DOCKER_HOST", "ssh://wrong")));
        Assert.Equal("unix:///chosen.sock", connection.Endpoint.AbsoluteUri);
        Assert.Equal("chosen", connection.Context);
    }

    [Fact]
    public void AmbientContext_WinsOverHost()
    {
        WriteContext("chosen", "unix:///chosen.sock");
        DockerConnection connection = DockerConnection.Resolve(null, null,
            Environment(("DOCKER_CONTEXT", "chosen"), ("DOCKER_HOST", "ssh://wrong")));
        Assert.Equal("chosen", connection.Context);
    }

    [Fact]
    public void AmbientHost_WinsOverActiveContext()
    {
        WriteActiveContext("missing");
        DockerConnection connection = DockerConnection.Resolve(null, null,
            Environment(("DOCKER_HOST", "http://localhost:2375")));
        Assert.Equal("http://localhost:2375/", connection.Endpoint.AbsoluteUri);
    }

    [Fact]
    public void ActiveContext_UsesDockerConfig()
    {
        WriteContext("active", "unix:///active.sock");
        WriteActiveContext("active");
        DockerConnection connection = DockerConnection.Resolve(null, null, Environment());
        Assert.Equal("unix:///active.sock", connection.Endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("active-context")]
    [InlineData("ambient-context")]
    [InlineData("ambient-host")]
    [InlineData("explicit-context")]
    public async Task CopiedDaemonCommandRetainsEndpointAfterConnectionSelectionChanges(string selection)
    {
        await using LayerCacheTestContext cache = new();
        DockerArchiveFixture fixture = DockerArchiveFixture.Create();
        const string endpoint = "unix:///tmp/original.sock";
        WriteContext("before", endpoint);
        WriteContext("after", "unix:///tmp/other.sock");
        WriteActiveContext("before");
        Func<string, string?> environment = selection switch
        {
            "ambient-context" => Environment(("DOCKER_CONTEXT", "before")),
            "ambient-host" => Environment(("DOCKER_HOST", endpoint)),
            _ => Environment()
        };
        IDockerRegistryClientFactory registry = Mock.Of<IDockerRegistryClientFactory>();
        TestAppSettingsStore settings = new(new()
        {
            Platform = new() { Os = "windows", Architecture = "arm64", OsVersion = "10.0.20348.1" }
        });
        List<DockerConnection> connected = [];
        ImageSourceFactory CreateFactory() => new(registry, settings, connection =>
        {
            connected.Add(connection);
            Assert.Equal(endpoint, connection.Endpoint.AbsoluteUri);
            return new ImageSourceFactoryTests.FakeEngine(fixture);
        }, Path.Combine(cache.Root, "snapshots"),
            options => DockerConnection.Resolve(options.DockerContext, options.DockerHost, environment,
                windows: false));
        PlatformOptionsBase options = ExplorerSource.ForPlatform(new("linux", "amd64", null, null), new()
        {
            DockerContext = selection == "explicit-context" ? "before" : null
        });
        await using ExplorerSource source = await ExplorerSource.OpenAsync(
            CreateFactory(), registry, "docker://local:dev", options, null,
            TestContext.Current.CancellationToken, store: cache.Store);
        string copied = new ExplorerPresenter(ExplorerImage.FromSource(source), 180, 50)
            .CopyCommandText("etc/example", false);
        Assert.Contains("--docker-host " + ShellCommand.Quote(endpoint), copied);
        Assert.Contains("--os-version=\"\"", copied);
        Assert.DoesNotContain("--docker-context", copied);
        WriteActiveContext("after");
        WriteContext("before", "unix:///tmp/changed.sock");
        environment = Environment(("DOCKER_CONTEXT", "after"), ("DOCKER_HOST", "unix:///tmp/other.sock"));
        using MemoryStream output = new();
        CatCommand command = new(registry, output, cache.Paths) { ImageSourceFactory = CreateFactory() };
        ((IProcessTerminationAware)command).ProcessTerminator = new TestProcessTerminator();

        var parsed = command.Parse(copied["dredge image cat ".Length..]);
        Assert.Empty(parsed.Errors);
        int exitCode = await parsed.InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("", command.Options.OsVersion);
        Assert.Equal("fixture"u8.ToArray(), output.ToArray());
        Assert.Equal(2, connected.Count);
    }

    [Fact]
    public void DefaultContext_UsesPlatformDefaultNotAmbientHost()
    {
        DockerConnection connection = DockerConnection.Resolve("default", null,
            Environment(("DOCKER_HOST", "ssh://wrong")), windows: false);
        Assert.Equal("unix:///var/run/docker.sock", connection.Endpoint.AbsoluteUri);
    }

    [Fact]
    public void Defaults_ArePlatformSpecific()
    {
        Assert.Equal("npipe", DockerConnection.Resolve(null, null, Environment(), windows: true).Endpoint.Scheme);
        Assert.Equal("unix", DockerConnection.Resolve(null, null, Environment(), windows: false).Endpoint.Scheme);
    }

    [Fact]
    public void RejectsConflictingFlags() =>
        Assert.Throws<ArgumentException>(() => DockerConnection.Resolve("a", "http://localhost", Environment()));

    [Theory]
    [InlineData("DOCKER_TLS_VERIFY", "1")]
    [InlineData("DOCKER_TLS_VERIFY", "0")]
    [InlineData("DOCKER_TLS", "1")]
    [InlineData("DOCKER_CERT_PATH", "certificates")]
    public void RejectsUnsupportedTlsWithoutFallback(string key, string value) =>
        Assert.Contains("TLS", Assert.Throws<NotSupportedException>(() =>
            DockerConnection.Resolve(null, "tcp://localhost:2376", Environment((key, value)))).Message);

    [Fact]
    public void ExplicitContext_DoesNotSilentlyIgnoreTlsEnvironment()
    {
        WriteContext("chosen", "unix:///chosen.sock");
        Assert.Throws<NotSupportedException>(() =>
            DockerConnection.Resolve("chosen", null, Environment(("DOCKER_TLS_VERIFY", "1"))));
    }

    [Theory]
    [InlineData("ssh://host")]
    [InlineData("ftp://host")]
    public void RejectsUnsupportedTransport(string host) =>
        Assert.Throws<NotSupportedException>(() => DockerConnection.Resolve(null, host, Environment()));

    [Theory]
    [InlineData("unix:docker.sock")]
    [InlineData("unix:./docker.sock")]
    [InlineData("unix:///")]
    [InlineData("unix://host/docker.sock")]
    public void RejectsUnixEndpointsWithoutAbsoluteSocketPath(string host)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            DockerConnection.Resolve(null, host, Environment(), windows: false));
        Assert.Contains("absolute socket path", error.Message);
    }

    [Theory]
    [InlineData("unix:///var/run/docker.sock", "/var/run/docker.sock")]
    [InlineData("unix:///tmp/docker%20socket.sock", "/tmp/docker socket.sock")]
    public void AcceptsAbsoluteUnixSocketPaths(string host, string path)
    {
        DockerConnection connection = DockerConnection.Resolve(null, host, Environment(), windows: false);
        Assert.Equal(path, connection.Endpoint.LocalPath);
    }

    [Fact]
    public void RejectsEmbeddedCredentials() =>
        Assert.Throws<ArgumentException>(() => DockerConnection.Resolve(null, "https://user:password@host", Environment()));

    [Fact]
    public void RejectsHttpsRatherThanAllowingLibraryToDowngradeToHttp() =>
        Assert.Throws<NotSupportedException>(() => DockerConnection.Resolve(null, "https://host:2376", Environment()));

    [Fact]
    public void DockerDesktopNamedPipe_NormalizesBeforeUriCanonicalization()
    {
        DockerConnection connection = DockerConnection.Resolve(null,
            "npipe:////./pipe/dockerDesktopLinuxEngine", Environment(), windows: true);
        Assert.Equal(".", connection.Endpoint.Host);
        Assert.Equal(["/", "pipe/", "dockerDesktopLinuxEngine"], connection.Endpoint.Segments);
    }

    [Fact]
    public void RejectsContextSkipTlsVerify()
    {
        WriteContext("unsafe", "https://host:2376", skipTlsVerify: true);
        Assert.Throws<NotSupportedException>(() => DockerConnection.Resolve("unsafe", null, Environment()));
    }

    [Fact]
    public void RejectsContextCertificateMaterial()
    {
        WriteContext("certificates", "https://host:2376");
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("certificates")));
        Directory.CreateDirectory(Path.Combine(directory, "contexts", "tls", hash, "docker"));
        Assert.Throws<NotSupportedException>(() => DockerConnection.Resolve("certificates", null, Environment()));
    }

    [Fact]
    public void MissingContext_DoesNotFallback() =>
        Assert.Throws<InvalidOperationException>(() => DockerConnection.Resolve("missing", null, Environment()));

    private Func<string, string?> Environment(params (string Name, string Value)[] variables)
    {
        Dictionary<string, string> values = variables.ToDictionary(pair => pair.Name, pair => pair.Value);
        values["DOCKER_CONFIG"] = directory;
        return name => values.GetValueOrDefault(name);
    }

    private void WriteActiveContext(string context)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "config.json"), JsonSerializer.Serialize(new { currentContext = context }));
    }

    private void WriteContext(string context, string host, bool skipTlsVerify = false)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(context)));
        string metadata = Path.Combine(directory, "contexts", "meta", hash);
        Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(metadata, "meta.json"), JsonSerializer.Serialize(new
        {
            Name = context,
            Endpoints = new { docker = new { Host = host, SkipTLSVerify = skipTlsVerify } }
        }));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
        string? parent = Path.GetDirectoryName(directory);
        if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
        }
    }
}
