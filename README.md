# Scripter (PowerToys Command Palette Extension)

Scripter is a Command Palette extension for running scripts conveniently with Microsoft PowerToys Command Palette.

The repository also includes an independent `scripter` CLI. Both the Command Palette extension and the CLI use the same `Scripter.Core` runtime, metadata loader, builtins, exported-function invocation, and permission fingerprint implementation.

> **⚠️This is a vibe-coding project!**
> While the software works in most cases,
> please keep in mind that it hasn't been subjected to serious testing, and bugs are expected.

## Script storage

User scripts are stored in:

`%LOCALAPPDATA%\Microsoft\PowerToys\CommandPalette\Scripter\Scripts`

You can open this folder from the extension menu via **Open scripts folder**.

The CLI is not restricted to this directory and can execute any explicitly selected `.js` path.

## CLI

Build and run the development CLI with:

```powershell
dotnet run --project .\Scripter.Cli\Scripter.Cli.csproj -- run .\test.js
```

Supported commands:

```text
scripter run foo.js
scripter run foo.js --function add -- 1 2
scripter watch foo.js
scripter debug foo.js
scripter debug foo.js --wait
scripter debug foo.js --break
scripter debug foo.js --watch --wait
```

Common options:

- `--function <name>` invokes a global function after loading the script.
- Arguments following `--` are passed to the function as exact string values.
- `--trust` explicitly approves and persists each current permission fingerprint during that CLI session.
- `--port <number>` changes the debug port from its default of `9222`.

CLI approvals are stored separately under `%LOCALAPPDATA%\Scripter\Cli`. Without `--trust`, scripts requesting `nativeTypes`, `dynamicImport`, `commandExecution`, or `nativeFfi` require an interactive confirmation. Non-interactive execution is rejected unless that exact fingerprint was already approved.

`watch` monitors the selected `.js` file and its `.js.meta.json` sidecar with a short debounce. If a save occurs while a script is still running, the old engine is interrupted and the latest content runs in a fresh engine.

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
  "commandExecution": true,
  "nativeFfi": false
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
- `nativeFfi` - enables generic native DLL loading and unmanaged function calls through `ffi`

## Command execution

When `commandExecution` is enabled, scripts can run commands in two ways.

`$(command, options)` keeps the existing raw shell-string behavior:

```javascript
const output = $('echo "Hello World"');
const started = $('start "" cmd.exe /k ping -t 8.8.8.8');
const pwshOutput = $('Write-Output "Hello World"', { shell: 'powershell' });
const utf8Output = $('some-utf8-tool.exe', { encoding: 'utf-8' });
```

The default shell is `cmd`. Supported shell names are `cmd`, `powershell`, and `pwsh`. For `cmd`, the raw command text is passed to `cmd.exe /d /c`, so `cmd` syntax such as `start "" ...` works as it does in a normal Command Prompt.

Use `$.exec(fileName, args, options)` when arguments should be passed as real argv values instead of shell text:

```javascript
$.exec('some.exe', ['', 'Hello World']);
$.exec('cmd.exe', ['/k', 'ping', '-t', '8.8.8.8'], { window: true });
$.exec('some-utf8-tool.exe', [], { encoding: 'utf-8' });
```

`$.exec` converts every argument to a string and preserves empty strings and spaces. By default it waits and captures output. With `{ window: true }`, it opens a visible process window and returns immediately unless `{ wait: true }` is also set. `options.workingDirectory` sets the process working directory.

Captured command output uses the Windows OEM code page by default, matching localized console tools such as `nslookup.exe` on Chinese Windows. Use `encoding` to override this when a tool emits UTF-8 or another encoding. Supported values include `oem`, `utf-8`, an encoding name, or a numeric code page such as `936`.

## Native FFI

When `nativeFfi` is enabled and approved, scripts can load any Windows DLL and bind exports by name or ordinal. Signatures use `<calling-convention> <return-type>(<parameter-types>)`:

```javascript
const getPid = ffi.bind(
    'kernel32.dll',
    'GetCurrentProcessId',
    'winapi u32()'
);

const byOrdinal = ffi.bindOrdinal(
    'some-library.dll',
    103,
    'stdcall u32(u32)'
);

const pid = getPid.Invoke();
```

Supported calling conventions are `winapi`, `cdecl`, and `stdcall`. Supported types are `void`, `i8`, `u8`, `i16`, `u16`, `i32`, `u32`, `i64`, `u64`, `f32`, `f64`, `ptr`, and `uptr`; `void` is valid only as a return type or as the sole marker for an empty parameter list. The first implementation supports primitive values and raw pointers, but not automatic string, array, structure, or out-parameter marshalling.

Native FFI is a separate permission because calls execute unmanaged code inside the Scripter process. A bad address, signature, or native function can corrupt or terminate PowerToys or the CLI process; only enable it for trusted scripts.

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

For development outside PowerToys, use:

```powershell
dotnet run --project .\Scripter.Cli\Scripter.Cli.csproj -- debug .\test.js --watch --wait
```

Then launch **Attach Scripter Debugger** in VS Code. The debugger attaches once to a persistent `V8Runtime`; every save disposes the previous `V8ScriptEngine` and creates a fresh one, so script globals do not leak between runs.

- `--wait` waits for the initial debugger connection, then runs normally to configured breakpoints.
- `--break` waits for the initial debugger connection and pauses in the real user document before its first statement.

The **Debug Current JS with Scripter** launch configuration can start debug-watch for the currently active JavaScript file and attach automatically.
