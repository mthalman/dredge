﻿using Newtonsoft.Json;
using Valleysoft.DockerRegistryClient.Models.Manifests;

namespace Valleysoft.Dredge.Commands.Image;

public class InspectCommand : RegistryCommandBase<InspectOptions>
{
    public InspectCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("inspect", "Return low-level information on a container image", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync()
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            IImageManifest manifest = (await ManifestHelper.GetResolvedManifestAsync(client, imageName, Options)).Manifest;
            string? digest = (manifest.Config?.Digest) ??
                throw new NotSupportedException($"Could not resolve the image config digest of '{Options.Image}'.");
            Stream blob = await client.Blobs.GetAsync(imageName.Repo, digest);
            using StreamReader reader = new(blob);
            string content = await reader.ReadToEndAsync();
            object? json = JsonConvert.DeserializeObject(content) ??
                throw new Exception($"Unable to deserialize content into JSON:\n{content}");
            string output = JsonConvert.SerializeObject(json, JsonHelper.Settings);
            Console.Out.WriteLine(output);
        });
    }
}
