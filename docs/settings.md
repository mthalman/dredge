# Settings File

Dredge uses a settings file to store configuration information. The settings file is a JSON file named `settings.json` that is located in the following location:

* Windows: `%LOCALAPPDATA%\Valleysoft.Dredge\settings.json`
* Linux: `$HOME/.local/share/Valleysoft.Dredge/settings.json`
* Mac: `$HOME/Library/Application Support/Valleysoft.Dredge/settings.json`

 Dredge will create the settings file automatically when it's needed.

The [`settings`](commands/settings.md) command can be used to manipulate the settings file.

## Schema

```json
{
  "fileCompareTool": {
    "exePath": "<string>",
    "args": "<string>"
  },
  "platform": {
    "os": "<string>",
    "osVersion": "<string>",
    "arch": "<string>"
  }
}
```

See [`image compare files`](commands/images.md#compare-image-files) for more information about the `fileCompareTool` setting.

See [platform resolution](platform-resolution.md) for more information about the `platform` setting.
