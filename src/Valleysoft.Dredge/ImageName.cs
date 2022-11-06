namespace Valleysoft.Dredge;

public class ImageName
{
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

    public static ImageName Parse(string imageName, bool requireTagOrDigest = false)
    {
        string? registry = null;
        int separatorIndex = imageName.IndexOf('/');
        if (separatorIndex >= 0)
        {
            string firstSegment = imageName[..separatorIndex];
            if (firstSegment.Contains('.') || firstSegment.Contains(':'))
            {
                registry = firstSegment;
                imageName = imageName[(separatorIndex + 1)..];
            }
        }

        string? tag = null;
        string? digest = null;

        separatorIndex = imageName.IndexOf('@');
        if (separatorIndex < 0)
        {
            separatorIndex = imageName.IndexOf(':');
            if (separatorIndex >= 0)
            {
                tag = imageName[(separatorIndex + 1)..];
            }
        }
        else
        {
            digest = imageName[(separatorIndex + 1)..];
        }

        if (requireTagOrDigest && tag is null && digest is null)
        {
            throw new ArgumentException($"Image name '{imageName}' is missing a tag or digest", nameof(imageName));
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

        repo = DockerHubHelper.ResolveRepoName(registry, repo);

        return new ImageName(registry, repo, tag, digest);
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
