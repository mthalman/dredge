using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Valleysoft.Dredge;

public class ImageName
{
    private const int MaxRepositoryLength = 255;
    private static readonly Regex RepositoryComponentPattern = new(
        @"\A[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex TagPattern = new(
        @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex DigestAlgorithmPattern = new(
        @"\A[a-z0-9]+(?:[+._-][a-z0-9]+)*\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex DigestEncodedPattern = new(
        @"\A[A-Za-z0-9=_-]+\z",
        RegexOptions.CultureInvariant);

    public ImageName(string? registry, string repo, string? tag, string? digest)
    {
        Registry = registry;
        Repo = repo;
        Tag = tag;
        Digest = digest;
    }

    public string? Registry { get; }
    public string Repo { get; }
    public string? Tag { get; }
    public string? Digest { get; }

    public static ImageName Parse(string imageName)
    {
        if (!TryParse(imageName, out ImageName? result, out string? error))
        {
            throw new ArgumentException(error, nameof(imageName));
        }

        return result;
    }

    public static bool TryParse(
        string? imageName,
        [NotNullWhen(true)]
        out ImageName? result,
        [NotNullWhen(false)]
        out string? error) =>
        TryParse(imageName, allowTagOrDigest: true, out result, out error);

    internal static bool TryParseRepository(
        string? repository,
        [NotNullWhen(true)]
        out ImageName? result,
        [NotNullWhen(false)]
        out string? error) =>
        TryParse(repository, allowTagOrDigest: false, out result, out error);

    private static bool TryParse(
        string? imageName,
        bool allowTagOrDigest,
        [NotNullWhen(true)]
        out ImageName? result,
        [NotNullWhen(false)]
        out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(imageName))
        {
            error = "The image reference cannot be empty or whitespace.";
            return false;
        }

        if (!string.Equals(imageName, imageName.Trim(), StringComparison.Ordinal))
        {
            error = "The image reference cannot contain leading or trailing whitespace.";
            return false;
        }

        string? registry = null;
        int separatorIndex = imageName.IndexOf('/');
        if (separatorIndex >= 0)
        {
            string firstSegment = imageName[..separatorIndex];
            if (firstSegment.Contains('.') ||
                firstSegment.Contains(':') ||
                firstSegment.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                firstSegment.StartsWith('[') ||
                firstSegment.Any(char.IsAsciiLetterUpper))
            {
                registry = firstSegment;
                imageName = imageName[(separatorIndex + 1)..];
            }
        }

        if (registry is not null && !IsValidRegistry(registry, out string? registryError))
        {
            error = $"Invalid registry '{registry}': {registryError}";
            return false;
        }

        string? tag = null;
        string? digest = null;

        separatorIndex = imageName.IndexOf('@');
        if (separatorIndex >= 0)
        {
            if (imageName.IndexOf('@', separatorIndex + 1) >= 0)
            {
                error = "Invalid digest: an image reference can contain only one '@' separator.";
                return false;
            }

            digest = imageName[(separatorIndex + 1)..];
            if (imageName[..separatorIndex].Contains(':'))
            {
                error = "An image reference cannot contain both a tag and a digest.";
                return false;
            }
        }
        else
        {
            separatorIndex = imageName.IndexOf(':');
            if (separatorIndex >= 0)
            {
                tag = imageName[(separatorIndex + 1)..];
            }
        }

        if (!allowTagOrDigest && separatorIndex >= 0)
        {
            error = "Invalid repository: tags and digests are not accepted for this argument.";
            return false;
        }

        if (tag is null && digest is null)
        {
            tag = "latest";
        }

        string repo;
        if (separatorIndex >= 0)
        {
            repo = imageName[..separatorIndex];
        }
        else
        {
            repo = imageName;
        }

        if (!IsValidRepository(repo, out string? repositoryError))
        {
            error = $"Invalid repository '{repo}': {repositoryError}";
            return false;
        }

        if (tag is not null && !TagPattern.IsMatch(tag))
        {
            error = $"Invalid tag '{tag}': tags must start with an ASCII letter, digit, or underscore, contain only ASCII letters, digits, underscores, periods, and hyphens, and be at most 128 characters.";
            return false;
        }

        if (digest is not null && !IsValidDigest(digest, out string? digestError))
        {
            error = $"Invalid digest '{digest}': {digestError}";
            return false;
        }

        repo = DockerHubHelper.ResolveRepoName(registry, repo);
        if (repo.Length > MaxRepositoryLength)
        {
            error = $"Invalid repository '{repo}': the repository cannot exceed {MaxRepositoryLength} characters.";
            return false;
        }

        result = new ImageName(registry, repo, tag, digest);
        return true;
    }

    private static bool IsValidRegistry(string registry, out string? error)
    {
        error = null;
        string host;
        string? port = null;

        if (registry.StartsWith('['))
        {
            int closingBracket = registry.IndexOf(']');
            if (closingBracket < 0)
            {
                error = "an IPv6 address must end with ']'.";
                return false;
            }

            host = registry[1..closingBracket];
            string suffix = registry[(closingBracket + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':'))
                {
                    error = "unexpected characters follow the IPv6 address.";
                    return false;
                }

                port = suffix[1..];
            }

            if (host.Any(character => !char.IsAsciiHexDigit(character) && character != ':') ||
                !IPAddress.TryParse(host, out IPAddress? address) ||
                address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "the bracketed host must be a valid IPv6 address.";
                return false;
            }
        }
        else
        {
            int colonIndex = registry.LastIndexOf(':');
            if (colonIndex >= 0)
            {
                if (registry.IndexOf(':') != colonIndex)
                {
                    error = "IPv6 addresses must be enclosed in brackets.";
                    return false;
                }

                host = registry[..colonIndex];
                port = registry[(colonIndex + 1)..];
            }
            else
            {
                host = registry;
            }

            if (!IsValidHost(host))
            {
                error = "the host must be a valid DNS name, IPv4 address, or localhost.";
                return false;
            }
        }

        if (port is not null &&
            (port.Length == 0 ||
                !port.All(char.IsAsciiDigit) ||
                !int.TryParse(port, out int portNumber) ||
                portNumber is < 1 or > 65535))
        {
            error = "the port must be a number from 1 through 65535.";
            return false;
        }

        return true;
    }

    private static bool IsValidHost(string host)
    {
        if (host.Length is 0 or > 253)
        {
            return false;
        }

        if (host.All(character => char.IsAsciiDigit(character) || character == '.'))
        {
            if (IPAddress.TryParse(host, out IPAddress? address) &&
                address.AddressFamily == AddressFamily.InterNetwork)
            {
                return true;
            }
        }

        return host.Split('.').All(label =>
            label.Length is > 0 and <= 63 &&
            char.IsAsciiLetterOrDigit(label[0]) &&
            char.IsAsciiLetterOrDigit(label[^1]) &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static bool IsValidRepository(string repository, out string? error)
    {
        if (repository.Length == 0)
        {
            error = "the repository cannot be empty.";
            return false;
        }

        if (repository.Length > MaxRepositoryLength)
        {
            error = $"the repository cannot exceed {MaxRepositoryLength} characters.";
            return false;
        }

        if (!repository.Split('/').All(component => RepositoryComponentPattern.IsMatch(component)))
        {
            error = "each path component must be lowercase, start and end with a letter or digit, and use only periods, underscores, or hyphens as separators.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsValidDigest(string digest, out string? error)
    {
        int separatorIndex = digest.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == digest.Length - 1 ||
            digest.IndexOf(':', separatorIndex + 1) >= 0)
        {
            error = "digests must use the form '<algorithm>:<encoded>'.";
            return false;
        }

        string algorithm = digest[..separatorIndex];
        if (!DigestAlgorithmPattern.IsMatch(algorithm))
        {
            error = "the algorithm is not valid.";
            return false;
        }

        string encoded = digest[(separatorIndex + 1)..];
        if (!DigestEncodedPattern.IsMatch(encoded))
        {
            error = "the encoded value contains invalid characters.";
            return false;
        }

        int? requiredLength = algorithm switch
        {
            "sha256" => 64,
            "sha512" => 128,
            _ => null
        };
        if (requiredLength is not null &&
            (encoded.Length != requiredLength ||
                !encoded.All(character =>
                    char.IsAsciiDigit(character) || character is >= 'a' and <= 'f')))
        {
            error = $"the encoded value for {algorithm} must contain exactly {requiredLength} lowercase hexadecimal characters.";
            return false;
        }

        error = null;
        return true;
    }

    public override string ToString()
    {
        string result = string.Empty;
        if (Registry is not null)
        {
            result += Registry + "/";
        }

        result += Repo;

        if (Tag is not null)
        {
            result += ":" + Tag;
        }
        else if (Digest is not null)
        {
            result += "@" + Digest;
        }

        return result;
    }
}
