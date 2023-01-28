# Settings

Sub-commands:

* [`open`](#open-settings) - Opens the Dredge settings file
* [`get`](#get-setting) - Gets the value of a setting
* [`set`](#set-setting) - Sets the value of a setting
* [`clear-cache`](#clear-cache) - Deletes the cached files used by Dredge

## Open Settings

The `settings open` command opens the Dredge settings file in the default associated program if it can.
Otherwise, it outputs the path to the settings file.

## Get Setting

The `settings get` command gets the value of a setting from the Dredge settings file.
It takes a single argument as the name of the setting to get.
Because the settings in the settings file are hierarchical and represented as JSON, setting names use a dot notation to separate the names to access the desired setting.
For example: `dredge settings get fileCompareTool.exePath` gets the executable path of the file compare tool from the settings file.

## Set Setting

The `settings set` command sets the value of a setting from the Dredge settings file.
It takes the name of the setting to set followed by the value to set it to.
Because the settings in the settings file are hierarchical and represented as JSON, setting names use a dot notation to separate the names to access the desired setting.
For example: `dredge settings set platform.os linux` sets the default platform OS to "linux".

## Clear Cache

The `settings clear-cache` command deletes the local cache of layer data stored in the temporary directory.
