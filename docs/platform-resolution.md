# Platform Resolution

For [certain operations](#operations-that-use-platform-resolution), Dredge needs to resolve a platform to a specific image manifest if a multi-arch/multi-platform tag is provided as input.
For example, the `alpine:latest` tag points to different images for different architectures.
For these commands, Dredge needs to know which image to use for the operation.
If it's unable to resolve the platform, it will fail with an error.
You can influence the platform resolution by setting the command's platform options or by setting the global platform settings.

## Operations that use platform resolution

The following operations make use of platform resolution:

* [`manifest resolve`](commands/manifests.md#resolve-manifest)
* All [`image`](commands/images.md) sub-commands

## Platform options

The following [platform options](https://github.com/mthalman/dredge/pull/52) can be used to influence platform resolution:

* `--os`: The operating system of the platform ("linux" or "windows").
* `--os-version`: The operating system version of the platform. This is usually only relevant for Windows images but may be relevant for Linux images in some rare cases.
* `--arch`: The architecture of the platform (e.g. "amd64", "arm", "arm64").

## Global platform settings

[Global platform settings](https://github.com/mthalman/dredge/pull/54) allow you to statically define platform settings in the Dredge settings file that will be used for all operations that use platform resolution.
The same platform options can be used as described in the previous section.

Here's the configuration of these global platform settings:

```json
{
  "platform": {
    "os": "<os-name>",
    "osVersion": "<os-version>",
    "arch": "<architecture>"
  }
}
```

You can set these values by using the [`settings set`](commands/settings.md#set-settings) command:

Example:

```console
dredge settings set platform.os linux
```

## Platform resolution order

Since the platform options and global platform settings can be used to influence platform resolution, it's important to understand the order in which these settings are applied.

Platform options provided in the call to the command take precedence over global platform settings.
If a platform option is not provided in the call to the command, the global platform setting is used.

It's not always necessary to set all of the platform values in order to successfully resolve the platform.
For example, many images are only available for Linux.
In that case, it's not necessary to set the `os` value because it doesn't reduce the amount of available platforms.
For Linux, it's often enough to just set the `arch` value.
As long as the provided platform values reduce the number of available platforms to a single platform, the platform resolution will succeed.
