using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge.Commands.Referrer;

public class CheckResult
{
    public CheckResult(IEnumerable<ArtifactTypeCheckResult> results)
    {
        Results = results.ToArray();
    }

    public bool Succeeded => Results.All(result => result.Found);
    public IReadOnlyList<ArtifactTypeCheckResult> Results { get; }
}

public class ArtifactTypeCheckResult
{
    public ArtifactTypeCheckResult(string artifactType, IEnumerable<ManifestReference> referrers)
    {
        ArtifactType = artifactType;
        Referrers = referrers.ToArray();
    }

    public string ArtifactType { get; }
    public bool Found => Referrers.Count > 0;
    public IReadOnlyList<ManifestReference> Referrers { get; }
}
