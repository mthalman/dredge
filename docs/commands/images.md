# Image Commands

All image commands support [platform resolution](../platform-resolution.md) via `--os`, `--arch`, and `--os-version` options.

| Sub-command | Description |
|-------------|-------------|
| [`inspect`](#inspect) | Inspect an image configuration |
| [`os`](#os) | Show OS information |
| [`compare layers`](#compare-layers) | Compare the layers of two images |
| [`compare files`](#compare-files) | Compare the file contents of two images |
| [`save-layers`](#save-layers) | Save image layers to disk |
| [`dockerfile`](#dockerfile) | Generate a Dockerfile from an image |

## Inspect

Returns the image configuration of the specified image.

```console
dredge image inspect <image> [--os <os>] [--arch <arch>] [--os-version <version>]
```

Example:

```console
dredge image inspect amd64/ubuntu:22.04
{
  "architecture": "amd64",
  "config": {
    "Hostname": "",
    "Domainname": "",
    "User": "",
    "AttachStdin": false,
    "AttachStdout": false,
--- <cut> ---
  "os": "linux",
  "rootfs": {
    "type": "layers",
    "diff_ids": [
      "sha256:f4a670ac65b68f8757aea863ac0de19e627c0ea57165abad8094eae512ca7dad"
    ]
  }
}
```

## OS

Returns information about the OS of the specified image. Supports both Linux and Windows images.

```console
dredge image os <image> [--os <os>] [--arch <arch>] [--os-version <version>]
```

### Linux example

```console
dredge image os amd64/ubuntu:22.04
{
  "PRETTY_NAME": "Ubuntu 22.04.1 LTS",
  "NAME": "Ubuntu",
  "ID": "ubuntu",
  "ID_LIKE": [
    "debian"
  ],
  "VERSION": "22.04.1 LTS (Jammy Jellyfish)",
  "VERSION_ID": "22.04",
  "VERSION_CODENAME": "jammy",
  "HOME_URL": "https://www.ubuntu.com/",
  "SUPPORT_URL": "https://help.ubuntu.com/",
  "BUG_REPORT_URL": "https://bugs.launchpad.net/ubuntu/",
  "PRIVACY_POLICY_URL": "https://www.ubuntu.com/legal/terms-and-policies/privacy-policy"
}
```

### Windows example

```console
dredge image os mcr.microsoft.com/windows/nanoserver:ltsc2022-amd64
{
  "Type": "Nano Server",
  "Version": "10.0.20348.1249"
}
```

## Compare Layers

Compares the layers of two images.

```console
dredge image compare layers <base> <target> [--output <format>] [--history] [--compressed-size] [--no-color] [--os <os>] [--arch <arch>] [--os-version <version>]
```

| Option | Description |
|--------|-------------|
| `--output` | Output format: `SideBySide` (default), `Inline`, or `Json` |
| `--history` | Include the layer history (Dockerfile instructions) |
| `--compressed-size` | Show compressed layer sizes |
| `--no-color` | Disable color output and use text-based diff indicators instead |

### Inline output example

```diff
dredge image compare layers --output inline amd64/node:19.1-alpine amd64/node:19.2-alpine
  sha256:ca7dd9ec2225f2385955c43b2379305acd51543c28cf1d4e94522b3d94cce3ce
- sha256:4487691952c066cb3964b94606825bc96c698377909c7d74c889fd12e24e36a7
+ sha256:bfebca31f7556839677aca8626941ec4be0d5e2a1a59f1bd991807828de37167
- sha256:206c50ffab466a0ed68db742d6d2015abcedd0a0b2500babb1938ce2a272b425
+ sha256:cc0056ab0c4160f34cd7046016f9aa6d1d14c206f61768b34efa69c45c38a0cb
- sha256:f6d4361cf153f2e83958f504356eef6e3d041eb3c4d23da466480ee2dfe656ae
+ sha256:6e25476b6324255c964f6b86e587d867e79046e94933123d0f1312dbddfe87b7
```

### Side-by-side output with history example

```
dredge image compare layers --history --no-color mcr.microsoft.com/dotnet/runtime:6.0.5-jammy-amd64 mcr.microsoft.com/dotnet/runtime:6.0.6-jammy-amd64
┌──────────────────────────────────────────────────────────────────────────┬───────────┬─────────────────────────────────────────────────────────────────────────┐
│ mcr.microsoft.com/dotnet/runtime:6.0.5-jammy-amd64                       │  Compare  │ mcr.microsoft.com/dotnet/runtime:6.0.6-jammy-amd64                      │
├──────────────────────────────────────────────────────────────────────────┼───────────┼─────────────────────────────────────────────────────────────────────────┤
│ sha256:405f018f9d1d0f351c196b841a7c7f226fb8ea448acd6339a9ed8741600275a2  │   Equal   │ sha256:405f018f9d1d0f351c196b841a7c7f226fb8ea448acd6339a9ed8741600275a2 │
│ /bin/sh -c #(nop) ADD                                                    │           │ /bin/sh -c #(nop) ADD                                                   │
│ file:11157b07dde10107f3f6f2b892c869ea83868475d5825167b5f466a7e410eb05 in │           │ file:11157b07dde10107f3f6f2b892c869ea83868475d5825167b5f466a7e410eb05   │
│ /                                                                        │           │ in /                                                                    │
│                                                                          │           │                                                                         │
│ <empty layer>                                                            │   Equal   │ <empty layer>                                                           │
│ /bin/sh -c #(nop)  CMD ["bash"]                                          │           │ /bin/sh -c #(nop)  CMD ["bash"]                                         │
│                                                                          │           │                                                                         │
│ sha256:7f5199084fb2409a567d45cbe1eebb7ad2bb92d2f2eeac1f9d7d1521b6529da5  │ Not Equal │ sha256:1d6b7ed86f8a0efb7b44af3ac71d881ea686c7e26f2bf9b509ffcee50d503a44 │
│ /bin/sh -c apt-get update     && apt-get install -y                      │           │ /bin/sh -c apt-get update     && apt-get install -y                     │
│ --no-install-recommends         ca-certificates                 libc6    │           │ --no-install-recommends         ca-certificates                 libc6   │
│ libgcc1         libgssapi-krb5-2         libicu70         libssl3        │           │ libgcc1         libgssapi-krb5-2         libicu70         libssl3       │
│ libstdc++6         zlib1g     && rm -rf /var/lib/apt/lists/*             │           │ libstdc++6         zlib1g     && rm -rf /var/lib/apt/lists/*            │
│                                                                          │           │                                                                         │
│ <empty layer>                                                            │   Equal   │ <empty layer>                                                           │
│ /bin/sh -c #(nop)  ENV ASPNETCORE_URLS=http://+:80                       │           │ /bin/sh -c #(nop)  ENV ASPNETCORE_URLS=http://+:80                      │
│ DOTNET_RUNNING_IN_CONTAINER=true                                         │           │ DOTNET_RUNNING_IN_CONTAINER=true                                        │
│                                                                          │           │                                                                         │
│ <empty layer>                                                            │ Not Equal │ <empty layer>                                                           │
│ /bin/sh -c #(nop)  ENV DOTNET_VERSION=6.0.5                              │           │ /bin/sh -c #(nop)  ENV DOTNET_VERSION=6.0.6                             │
│                                                                          │           │                                                                         │
│ sha256:ae2c6691208b45534916003bf6e5607998bab42aa923dc5f1e21fc244f0a9832  │ Not Equal │ sha256:18a715d5177a41204dd062b5760565bd282526e22063ab158b4180833f5a5156 │
│ /bin/sh -c #(nop) COPY                                                   │           │ /bin/sh -c #(nop) COPY                                                  │
│ dir:49b45e3ccadd0521a7513b91e6cb00a52ff23f9e8004ce74c832042e93fe7e33 in  │           │ dir:fb7195f4bc42fce62a7104cc5ef211701a1267b4666b445b59f649b0f86ecaa6 in │
│ /usr/share/dotnet                                                        │           │ /usr/share/dotnet                                                       │
│                                                                          │           │                                                                         │
│ sha256:114810c4073fb2a42557832ebfa76ec9a0f3ddcd13edf20b9f6d690f0d0be720  │ Not Equal │ sha256:6adc839fa9c17fc4a0f1965aa58b446f6531b4b926995080404321a223ce82b2 │
│ /bin/sh -c ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet                │           │ /bin/sh -c ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet               │
└──────────────────────────────────────────────────────────────────────────┴───────────┴─────────────────────────────────────────────────────────────────────────┘
```

## Compare Files

Compares the file contents of two images by downloading and squashing their layers into local filesystem representations, then launching an external diff tool.

```console
dredge image compare files <base> <target> [--base-layer-index <n>] [--target-layer-index <n>] [--os <os>] [--arch <arch>] [--os-version <version>]
```

| Option | Description |
|--------|-------------|
| `--base-layer-index` | Only apply layers up to this index for the base image |
| `--target-layer-index` | Only apply layers up to this index for the target image |

The external comparison tool is configured in the [settings file](../settings.md):

```json
{
  "fileCompareTool": {
    "exePath": "C:\\Program Files\\Beyond Compare 4\\BCompare.exe",
    "args": "{0} {1}"
  }
}
```

Example — compare two images:

```console
dredge image compare files amd64/node:19.1-alpine amd64/node:19.2-alpine
```

Example — compare only the first two layers:

```console
dredge image compare files amd64/node:19.1-alpine amd64/node:19.2-alpine --base-layer-index 1 --target-layer-index 1
```

Example — compare layers within a single image (difference between the 2nd and 3rd layer):

```console
dredge image compare files amd64/node:19.1-alpine amd64/node:19.1-alpine --base-layer-index 1 --target-layer-index 2
```

## Save Layers

Saves the extracted layers of an image to disk.

```console
dredge image save-layers <image> <output-path> [--no-squash] [--layer-index <n>] [--os <os>] [--arch <arch>] [--os-version <version>]
```

| Option | Description |
|--------|-------------|
| `--no-squash` | Save layers as individual directories instead of squashing |
| `--layer-index` | Only save the specified layer (includes dependent layers when squashing) |

Example:

```console
dredge image save-layers amd64/node:19.2-alpine out/layers/node
```

## Dockerfile

Generates a Dockerfile that represents an image.

```console
dredge image dockerfile <image> [--no-format] [--no-color] [--os <os>] [--arch <arch>] [--os-version <version>]
```

| Option | Description |
|--------|-------------|
| `--no-format` | Disable heuristic line-break formatting |
| `--no-color` | Disable syntax coloring |
