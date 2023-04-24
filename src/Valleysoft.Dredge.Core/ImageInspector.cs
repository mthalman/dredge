using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge.Core;

public static class ImageInspector
{
    public async static Task<string> GetImageConfigJson(string imageName, IDockerRegistryClientFactory dockerRegistryClientFactory, AppSettings appSettings, PlatformOptions platformOptions)
    {
        ImageName parsedImageName = ImageName.Parse(imageName);
        using IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(parsedImageName.Registry);
        DockerManifestV2 manifest = (await ManifestHelper.GetResolvedManifestAsync(client, parsedImageName, appSettings, platformOptions)).Manifest;
        string? digest = manifest.Config?.Digest;
        if (digest is null)
        {
            throw new NotSupportedException($"Could not resolve the image config digest of '{imageName}'.");
        }

        Stream blob = await client.Blobs.GetAsync(parsedImageName.Repo, digest);
        using StreamReader reader = new(blob);
        string content = await reader.ReadToEndAsync();
        object json = JsonConvert.DeserializeObject(content);
        string output = JsonConvert.SerializeObject(json, Formatting.Indented);
        return output;
    }
}
