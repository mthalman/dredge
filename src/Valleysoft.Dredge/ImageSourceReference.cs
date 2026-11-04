namespace Valleysoft.Dredge;

internal sealed record ImageSourceReference
{
    private readonly bool explicitRegistry;

    private ImageSourceReference(bool isDaemon, string value, string? registry, bool explicitRegistry)
    {
        IsDaemon = isDaemon;
        Value = value;
        Registry = registry;
        this.explicitRegistry = explicitRegistry;
    }

    public bool IsDaemon { get; }
    public bool IsImageId => IsDaemon && DockerImageArchive.IsSha256(Value);
    public string Value { get; }
    public string? Registry { get; }

    public static ImageSourceReference Parse(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        int separator = reference.IndexOf("://", StringComparison.Ordinal);
        bool daemon = false;
        bool registry = false;
        string value = reference;
        ImageName? parsed = null;
        if (separator >= 0)
        {
            string scheme = reference[..separator];
            daemon = scheme == "docker";
            registry = scheme == "registry";
            if (!daemon && !registry)
            {
                throw new ArgumentException($"Unsupported image source '{scheme}://'. Use docker:// or registry://.");
            }
            value = reference[(separator + 3)..];
        }

        if (!daemon || !DockerImageArchive.IsSha256(value))
        {
            parsed = ImageName.Parse(value);
        }

        return new ImageSourceReference(daemon, value, daemon ? null : parsed!.Registry, registry);
    }

    internal ImageName GetImageName()
    {
        if (!IsDaemon)
        {
            return ImageName.Parse(Value);
        }
        if (IsImageId)
        {
            return new ImageName(null, Value, null, null);
        }
        int digestSeparator = Value.IndexOf('@');
        if (digestSeparator >= 0)
        {
            return new ImageName(null, Value[..digestSeparator], null, Value[(digestSeparator + 1)..]);
        }
        int tagSeparator = Value.LastIndexOf(':');
        return tagSeparator > Value.LastIndexOf('/')
            ? new ImageName(null, Value[..tagSeparator], Value[(tagSeparator + 1)..], null)
            : new ImageName(null, Value, "latest", null);
    }

    public override string ToString() =>
        (IsDaemon ? "docker://" : explicitRegistry ? "registry://" : "") + Value;
}
