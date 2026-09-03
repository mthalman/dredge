using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge.Commands.Referrer;

public class CheckCommand : RegistryCommandBase<CheckOptions>
{
    public CheckCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        TextWriter? output = null)
        : base(
            "check",
            "Checks for required OCI referrer artifact types",
            dockerRegistryClientFactory,
            output)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        bool succeeded = true;

        await ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client =
                await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            OciImageIndex index =
                await ReferrerHelper.GetReferrersAsync(client, imageName, artifactType: null, ct);
            CheckResult result = GetResult(index);
            WriteResult(result);
            succeeded = result.Succeeded;
        });

        if (!succeeded)
        {
            Exit(2);
        }
    }

    private CheckResult GetResult(OciImageIndex index) =>
        new(Options.ArtifactTypes.Select(artifactType =>
            new ArtifactTypeCheckResult(
                artifactType,
                index.Manifests.Where(referrer =>
                    string.Equals(referrer.ArtifactType, artifactType, StringComparison.Ordinal)))));

    private void WriteResult(CheckResult result)
    {
        if (Options.OutputFormat == CheckOutput.Json)
        {
            Output.WriteLine(JsonConvert.SerializeObject(result, JsonHelper.Settings));
            return;
        }

        foreach (ArtifactTypeCheckResult artifactTypeResult in result.Results)
        {
            Output.WriteLine(
                $"{(artifactTypeResult.Found ? "PASS" : "FAIL")} {artifactTypeResult.ArtifactType}");
            foreach (ManifestReference referrer in artifactTypeResult.Referrers)
            {
                Output.WriteLine($"  {referrer.Digest}");
            }
        }
    }
}
