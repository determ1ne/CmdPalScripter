# Scripter (PowerToys Command Palette Extension)

Scripter is a Command Palette extension for running scripts conveniently with Microsoft PowerToys Command Palette.

> **⚠️This is a vibe-coding project!**
> While the software works in most cases,
> please keep in mind that it hasn't been subjected to serious testing, and bugs are expected.

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
  "export": ["run_command"],
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
- `export` - optional list of global JavaScript functions to register as commands
- `nativeTypes` - list of pre-exposed .NET types
- `dynamicImport` - enables `importType(...)` interop function, allowing the script import native types from code
- `commandExecution` - enables built-in `$` command execution

## Command execution

When `commandExecution` is enabled, scripts can run commands in two ways.

`$(command, options)` keeps the existing raw shell-string behavior:

```javascript
const output = $('echo "Hello World"');
const started = $('start "" cmd.exe /k ping -t 8.8.8.8');
const pwshOutput = $('Write-Output "Hello World"', { shell: 'powershell' });
```

The default shell is `cmd`. Supported shell names are `cmd`, `powershell`, and `pwsh`. For `cmd`, the raw command text is passed to `cmd.exe /d /c`, so `cmd` syntax such as `start "" ...` works as it does in a normal Command Prompt.

Use `$.exec(fileName, args, options)` when arguments should be passed as real argv values instead of shell text:

```javascript
$.exec('some.exe', ['', 'Hello World']);
$.exec('cmd.exe', ['/k', 'ping', '-t', '8.8.8.8'], { window: true });
```

`$.exec` converts every argument to a string and preserves empty strings and spaces. By default it waits and captures output. With `{ window: true }`, it opens a visible process window and returns immediately unless `{ wait: true }` is also set. `options.workingDirectory` sets the process working directory.

## Exported function commands

When `export` contains function names, each function is shown as a separate command. Everything typed after the complete function name is passed as string arguments:

```text
add_many_numbers 1 2 3
add_just_two "10" "20"
```

```javascript
function add_many_numbers(...args) {
    return args.reduce((sum, value) => sum + Number(value), 0);
}

function add_just_two(a, b) {
    return Number(a) + Number(b);
}
```

Export names must be JavaScript identifiers. Quotes preserve spaces within an argument. Missing, `null`, or empty `export` metadata keeps the original behavior of executing the entire script. In export mode, top-level code still runs before the selected function is called, so top-level code should normally contain only declarations and initialization.

Exported functions are also registered as top-level Command Palette commands, so their argument pages can be opened without first opening the Scripts page. Script metadata is refreshed when the extension starts or **Reload scripts** is selected. Script source is read from disk each time a command runs.

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
