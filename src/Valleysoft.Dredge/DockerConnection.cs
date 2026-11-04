using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docker.DotNet;

namespace Valleysoft.Dredge;

internal sealed record DockerConnection(Uri Endpoint, string? Context)
{
    internal DockerClient CreateClient() => new DockerClientBuilder().WithEndpoint(Endpoint).Build();

    internal static DockerConnection Resolve(
        string? context,
        string? host,
        Func<string, string?>? environment = null,
        string? homeDirectory = null,
        bool? windows = null)
    {
        if (context is not null && host is not null)
        {
            throw new ArgumentException("--docker-context and --docker-host cannot be used together.");
        }
        environment ??= Environment.GetEnvironmentVariable;
        if (Nonempty(environment("DOCKER_TLS_VERIFY")) is not null ||
            Nonempty(environment("DOCKER_TLS")) is not null ||
            Nonempty(environment("DOCKER_CERT_PATH")) is not null)
        {
            throw UnsupportedTls();
        }
        homeDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool isWindows = windows ?? OperatingSystem.IsWindows();
        string configPath = Nonempty(environment("DOCKER_CONFIG")) ?? Path.Combine(homeDirectory, ".docker");
        string? selectedContext = context;
        string? selectedHost = host;
        if (selectedContext is null && selectedHost is null)
        {
            selectedContext = Nonempty(environment("DOCKER_CONTEXT"));
            if (selectedContext is null)
            {
                selectedHost = Nonempty(environment("DOCKER_HOST"));
            }
            if (selectedContext is null && selectedHost is null)
            {
                string configFile = Path.Combine(configPath, "config.json");
                if (File.Exists(configFile))
                {
                    using JsonDocument config = JsonDocument.Parse(File.ReadAllBytes(configFile));
                    selectedContext = ReadString(config.RootElement, "currentContext");
                }
            }
        }

        string defaultEndpoint = isWindows ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock";
        string endpoint;
        if (selectedContext is not null && selectedContext != "default")
        {
            string contextHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selectedContext)));
            string metadata = Path.Combine(configPath, "contexts", "meta", contextHash, "meta.json");
            if (!File.Exists(metadata))
            {
                throw new InvalidOperationException($"Docker context '{selectedContext}' was not found. Check --docker-context and DOCKER_CONFIG.");
            }
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(metadata));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("Endpoints", out JsonElement endpoints) ||
                !endpoints.TryGetProperty("docker", out JsonElement docker))
            {
                throw new InvalidOperationException($"Docker context '{selectedContext}' has no Docker endpoint.");
            }
            if ((docker.TryGetProperty("SkipTLSVerify", out JsonElement skip) && skip.GetBoolean()) ||
                (root.TryGetProperty("TLSMaterial", out JsonElement material) &&
                    material.ValueKind == JsonValueKind.Object && material.EnumerateObject().Any()) ||
                Directory.Exists(Path.Combine(configPath, "contexts", "tls", contextHash)))
            {
                throw UnsupportedTls();
            }
            endpoint = ReadString(docker, "Host") ??
                throw new InvalidOperationException($"Docker context '{selectedContext}' has no endpoint host.");
        }
        else
        {
            endpoint = selectedContext == "default" ? defaultEndpoint : selectedHost ?? defaultEndpoint;
        }

        if (endpoint.StartsWith("npipe:////", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = "npipe://" + endpoint["npipe:////".Length..];
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("unix" or "npipe" or "http" or "https" or "tcp"))
        {
            throw new NotSupportedException("Unsupported Docker endpoint. SSH contexts are not supported; use a Unix socket, named pipe, or a separately secured local HTTP tunnel with --docker-host.");
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Docker endpoints cannot contain embedded credentials, query strings, or fragments.");
        }
        if (uri.Scheme == "https")
        {
            throw UnsupportedTls();
        }
        if (uri.Scheme == "tcp")
        {
            uri = new UriBuilder(uri) { Scheme = "http", Port = uri.Port < 0 ? 2375 : uri.Port }.Uri;
        }
        if (uri.Scheme == "unix" &&
            (uri.Host.Length != 0 || !uri.LocalPath.StartsWith('/') || uri.LocalPath == "/"))
        {
            throw new ArgumentException("Docker Unix endpoints must use an absolute socket path, such as unix:///var/run/docker.sock.");
        }
        if (uri.Scheme == "npipe" && !isWindows)
        {
            throw new NotSupportedException("Docker named pipes require Windows. Select a Unix socket or a separately secured local HTTP tunnel.");
        }
        if (uri.Scheme == "npipe" &&
            (uri.Segments.Length != 3 || !uri.Segments[1].Equals("pipe/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Docker named-pipe endpoints must use npipe://./pipe/<pipe-name>.");
        }
        return new DockerConnection(uri, selectedContext);
    }

    private static NotSupportedException UnsupportedTls() => new(
        "Docker HTTPS/certificate/TLS configuration is not supported by this build (https://, DOCKER_TLS_VERIFY, DOCKER_TLS, DOCKER_CERT_PATH, or context TLS material/SkipTLSVerify). Use a local Unix socket/named pipe, or a separately secured local HTTP tunnel. TLS is never disabled or downgraded and no connection fallback was attempted.");

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) ? Nonempty(value.GetString()) : null;

    private static string? Nonempty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
