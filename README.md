# Scripter (PowerToys Command Palette Extension)

Scripter is a Command Palette extension for running scripts conveniently with Microsoft PowerToys Command Palette.

## Script storage

User scripts are stored in:

`%LOCALAPPDATA%\Microsoft\PowerToys\CommandPalette\Scripter\Scripts`

You can open this folder from the extension menu via **Open scripts folder**.

## Metadata format

Each script can have a sidecar metadata file:

`<script>.js.meta.json`

Example:

```json
{
  "type": "clearscript",
  "name": "My Script",
  "description": "Runs a command and shows output",
  "nativeTypes": [
    {
      "name": "Convert",
      "typeName": "System.Convert"
    }
  ],
  "dynamicImport": false,
  "commandExecution": true
}
```

Fields:

- `type` - script engine type. Current supported value: `clearscript`
- `name` - display name in Command Palette
- `description` - short description
- `nativeTypes` - list of pre-exposed .NET types
- `dynamicImport` - enables `importType(...)` interop function, allowing the script import native types from code
- `commandExecution` - enables built-in `$` command execution

## Permissions

On first run for a script requiring elevated capabilities, Scripter shows an **Approve and run** dialog.

Permission approval is bound to:

- script path
- script content
- permission-relevant metadata

If content/permissions change, approval is automatically invalidated.

## Icons

Script list entries can use custom logos:

- `my-script.js`
- `my-script.png`

If no logo is found, the extension default icon is used.

## Debug settings

From the script command context menu, open **Settings** and configure:

- Enable remote debugging
- Pause on script start
- Debug port

Visual Studio Code can be used to attach a debugger to the script. See `.vscode/launch.json`.
