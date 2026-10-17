# RabTrans

RabTrans is a Windows desktop translation and OCR tool built with C#/.NET and WPF. It focuses on quick selected-text translation, screenshot OCR, plugin-based service integration, and a small tray-first workflow.

![](screenshots/1.jpg)

## Project Structure

```text
RabTrans.sln
src/
  RabTrans.App/
    App.xaml
    MainWindow.xaml
    SettingsWindow.xaml
    HistoryWindow.xaml
    ScreenshotSelectionWindow.xaml
    Assets/
    Plugins/
  RabTrans.Core/
    Clipboard/
    Hotkey/
    Networking/
    OCR/
    Plugins/
    Screenshot/
    Storage/
    Translation/
```

## Features

- Global hotkeys for screenshot OCR and selected-text translation.
- Translation window with multiple plugin-backed translation providers.
- OCR plugins loaded separately from translation plugins.
- Built-in local PaddleOCR plugin through the user's Python environment.
- Translation history window with copy support.
- Settings for startup, hotkeys, Node path, proxy mode, interface selection, configuration reload, and sync.
- Import/export for user settings and user plugin interfaces.
- Local configuration and plugin files under `%LOCALAPPDATA%\RabTrans`.

## Build

Requirements:

- Windows 10/11
- .NET 10 SDK
- Optional: Node.js for JavaScript process plugins
- Optional: Python with `paddleocr` for local PaddleOCR recognition

Build from the repository root:

```powershell
dotnet build src\RabTrans.App\RabTrans.App.csproj
```

The debug executable is emitted under:

```text
src\RabTrans.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\RabTrans.exe
```

## Selection Shortcut (0.6.3)

Selection capture restores the original compatibility timings: poll key release every
25 ms, allow 60 ms for released keys to settle, read the copied text every 35 ms for
up to eight polls per attempt, and wait 80 ms after capture/restoration before showing
the translation window. The longer 1.5-second allowance for held shortcut keys remains.

If an Alt-triggered copy produces no clipboard update, the app sends Escape, waits
35 ms, and retries once with a fresh eight-poll response window. This recovery no longer
depends on native menu flags: browser toolbar/menu focus may not expose those flags.
For example, [Chrome uses Alt+E to open its menu](https://support.google.com/chrome/answer/157179).
Escape is only sent after an unsuccessful Alt copy while the same window is foreground;
non-Alt shortcuts do not send Escape. Late replies and temporarily empty clipboard
formats are allowed to finish before declaring that there is no selection.

Capture still retries temporary clipboard locks, ignores overlapping requests, cancels
if you switch windows, and protects newer clipboard contents. The regression suite
covers Alt focus with no native menu flag, a delayed second response, staged clipboard
formats, the original settling delays, and focus changes during retry.

The old clipboard is materialized before copying and restored only while the copied
selection is still current. Failed backups do not clear the clipboard. Restored data
is kept by the running app and persisted at normal exit. Selection text is not written
to diagnostics; logs include copy/recovery dispatch, whether text became available,
capture status, and time until the window is shown.

Run the deterministic capture regression suite (no external test packages required):

```powershell
dotnet run --project tests\RabTrans.Selection.Tests -c Release
```

For desktop acceptance, test the shortcut from a browser, a text editor, and an Office
document: repeated selections (including the same text), keys held for about a second,
rapid repeated triggers, no selection, a busy clipboard, and switching windows while
copying. Verify plain text, rich text, images, and file lists on the prior clipboard.
Actual wake latency depends on the source application and clipboard rendering. Windows
may block simulated input into an elevated app; in that case the window reports that
the selection could not be copied.

The capture timing/notification implementation follows the Windows documentation for
[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
and [clipboard monitoring](https://learn.microsoft.com/en-us/windows/win32/dataxchg/using-the-clipboard).
The distinction between physical key state and an application's input queue is described
in [GetKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getkeystate).

## Runtime Configuration

Runtime data is stored in:

```text
%LOCALAPPDATA%\RabTrans
```

Important files and folders:

```text
settings.json           User settings and enabled interface order
history.jsonl           Translation history
plugins\translation\   User translation plugins
plugins\ocr\           User OCR plugins
logs\                  Application logs
```

Built-in plugins are copied from:

```text
src\RabTrans.App\Plugins
```

User plugins are loaded from:

```text
%LOCALAPPDATA%\RabTrans\plugins
```

Plugin execution uses the configured Node/Python executable paths when provided, otherwise it falls back to `node` or `python` from `PATH`.

Network proxy behavior is configured in Settings -> Runtime & network:

- `Use system proxy`: process plugins inherit the system proxy environment where supported.
- `No proxy`: RabTrans clears common proxy environment variables for plugin processes.
- `HTTP proxy`: RabTrans sets `HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`, and lowercase variants for plugin processes.

For Node.js process plugins, RabTrans also sets `NODE_USE_ENV_PROXY=1` when proxy environment variables should be used.

## Settings

Settings uses a resizable window with five categories: **General**, **Translation**, **OCR**,
**Runtime & network**, and **Backup & restore**. Navigation and save status stay visible while
long pages scroll. The window fits the current monitor's available space.

- Changes save automatically. Toggles, selections, and service order save immediately; runtime paths
  and proxy addresses save after 600 ms without typing, on Enter, or when focus leaves the field.
- Invalid paths, proxy addresses, and unavailable shortcuts remain unsaved with an explanation.
  Failed saves restore the previous runtime values and can be retried. Closing waits for pending
  saves; if an edit cannot save, choose **Retry**, **Discard edits**, or **Keep editing**.
- **Close to system tray** controls the main window's close button. Minimizing still minimizes the
  window normally; **Exit** in the tray always quits. The old `minimize_to_tray` preference migrates
  to `close_to_tray` on the first successful settings edit.
- Translation and OCR share the same service controls: checkbox, name and ID, small configuration
  gear, and drag handle. Drag by the handle or use **Alt+Up / Alt+Down** on a selected service.
  At least one service must remain enabled when services are available. Disabled services retain
  their positions; refresh preserves choices and appends newly discovered services.
- Shortcut recording suspends global shortcuts until a combination is recorded or recording is
  cancelled. **Esc**, changing page, and moving focus away cancel recording. Duplicate or occupied
  shortcuts do not replace the last working shortcuts.
- Runtime paths have browse and reset controls; blank paths use automatic executable discovery.
  The proxy address is shown only in HTTP proxy mode.

Settings behavior and layout verification (uses temporary configuration, never the startup registry):

```powershell
dotnet run --project tests/RabTrans.Settings.Tests -c Release -p:Platform=x64 -- artifacts/settings-review
```

The harness checks autosave, concurrency, rollback, backup validation, service selection/order,
text validation, Windows shortcut conflicts and restoration, and close/discard behavior. It renders all five pages at 100%, 150%, and 200%
raster scales and at the minimum window size. These renders do not replace checks on physical monitors with different DPI settings.

## Translation And OCR Service Configuration

In **Settings -> Translation / OCR**, click the small gear icon next to
a service (tooltip: **Open config**) and edit its `config.json`. Save the file; the next translation reads it again
without restarting or hot reloading. OCR also reloads configuration for each recognition. Google needs no configuration.

- Built-in templates are copied once to `%LOCALAPPDATA%\RabTrans\plugins\translation\<service-id>\config.json`.
- Built-in OCR templates use `%LOCALAPPDATA%\RabTrans\plugins\ocr\<service-id>\config.json`.
- Custom plugins keep `config.json` beside their own `plugin.json`, even if their folder
  name differs from the service id.
- Existing files are never overwritten by startup or template updates. Configurations
  are included in the normal configuration export/import package.
- `plugin.json` describes how to run a plugin; `config.json` contains its actual settings.
  There are no credentials in the bundled scripts or templates.

The minimal DeepL configuration contains only `apiKey`. DashScope Qwen uses:

```json
{
  "apiKey": "YOUR_API_KEY",
  "model": "qwen3.5-35b-a3b",
  "enableThinking": false
}
```

### Arbitrary Plugin Parameters

`config.json` is a plugin-owned JSON object, **not a fixed list of supported keys**.
The host passes the whole object in the process input's `config` property, preserving
unknown keys, nested objects, arrays, numbers, booleans, strings and nulls. It only
validates JSON syntax and that the root is an object. Missing files produce `{}`.
A malformed file fails that service's request with a value-free error.

Plugins read `input.config` and define their own required fields, defaults and validation.
The host does not flatten values, convert booleans to strings, filter by a schema, or
merge configuration into `text`, `from`, `to`, `imageBase64` or other request fields.
Parameters only take effect when the plugin consumes them or forwards them to its API.
Adding provider-specific configuration never requires changing the host.

DashScope and the migrated Qwen/DeepSeek adapters also support optional `extraBody`:

```json
{
  "apiKey": "YOUR_API_KEY",
  "model": "YOUR_MODEL",
  "enableThinking": false,
  "extraBody": {
    "temperature": 0,
    "vendor_options": { "mode": "custom" }
  }
}
```

The adapter builds its default HTTP body and then applies `extraBody` by top-level key;
nested objects are replaced as a whole. `messages` is reserved for the current translation
and is rejected in `extraBody`. The example vendor field illustrates arbitrary structure;
your chosen API must support each field you send. DeepL and Caiyun currently consume
`apiKey` and `token`, respectively; their adapters do not forward `extraBody`.

Templates intentionally omit optional advanced parameters. Credentials are local plaintext
configuration and are also included in configuration exports. Plugin diagnostics must not
print configuration values or raw API error bodies.

### OCR Parameters

Baidu OCR uses a minimal `config.json` containing `ak` and `sk`. Optional `extraBody`
adds or overrides recognition form fields such as `language_type` or `detect_direction`.
The `image` field is reserved for the current screenshot. String values are sent directly;
other JSON values are JSON-encoded into form values, preserving `false` and `0`. The API
must support the supplied fields and their encodings. OCR authentication uses only `ak`/`sk`.

PaddleOCR defaults to `{ "lang": "ch" }`. Optional `device` defaults to `cpu`; optional
`engine` selects the inference engine. An `options` object passes arbitrary keyword
arguments to the installed `PaddleOCR` constructor, overriding defaults by top-level key.
For example, `options` can contain `use_textline_orientation` or model-specific options.
Nested values retain their JSON types. The effective engine is used for dependency checks.
Unsupported options produce an initialization error instead of being silently removed.

PaddleOCR reads these settings only from `input.config`. The one-time OCR migration copies
existing `RABTRANS_PADDLEOCR_LANG`, `RABTRANS_PADDLEOCR_ENGINE`, and
`RABTRANS_PADDLEOCR_DEVICE` environment values into a new config file when present; it
never overwrites an existing config. Subsequent changes belong in `config.json`.

### One-Time Custom Plugin Migration

The new plugins require the updated host; there is no old-protocol fallback. The migration
utility handles this installation's `caiyun`, `deepseek`, and `qwen` folders; use
`--kind ocr` for the `baidu-basic-ocr` folder and PaddleOCR environment migration. It extracts
literal settings without executing scripts, removes unused `configSchema`
metadata, preserves existing configuration files (including unknown fields), and updates
the scripts to read `input.config`. All originals are backed up outside the plugin tree.
Repeated runs leave migrated services unchanged. Use Python 3.9+ and Node.js:

```powershell
python packaging/Migrate-ServiceConfig.py --root "$env:LOCALAPPDATA\RabTrans\plugins\translation" --backup-root "$env:LOCALAPPDATA\RabTrans\backups\translation-config" --dry-run
python packaging/Migrate-ServiceConfig.py --root "$env:LOCALAPPDATA\RabTrans\plugins\translation" --backup-root "$env:LOCALAPPDATA\RabTrans\backups\translation-config"
```

For the OCR migration, use the same utility with the OCR paths:

```powershell
python packaging/Migrate-ServiceConfig.py --kind ocr --root "$env:LOCALAPPDATA\RabTrans\plugins\ocr" --backup-root "$env:LOCALAPPDATA\RabTrans\backups\ocr-config"
```

Close the old application and use the newly built host when applying the migration.
Do not store backups inside `plugins`, where their manifests would be loaded as services.

### Configuration Checks

```powershell
dotnet run --project tests/RabTrans.Translation.Tests -c Release -p:Platform=x64
node tests/RabTrans.Translation.Tests/plugins.test.cjs
python tests/RabTrans.Translation.Tests/migration_test.py
python tests/RabTrans.Translation.Tests/paddle_options_test.py
```

The tests use isolated folders and simulated API responses; they do not spend API credits
or access real credentials. To exercise already migrated custom adapters with fake
credentials, set `RABTRANS_PLUGIN_TEST_ROOT` to their translation plugin directory before
running the Node tests. Set `RABTRANS_OCR_TEST_ROOT` to test migrated custom OCR adapters.

## Local PaddleOCR

RabTrans includes a built-in OCR plugin named `paddleocr_local`. It calls the Python executable configured in Settings -> Runtime & network -> Plugin runtimes and imports the local `paddleocr` package.

Install PaddleOCR in the Python environment used by RabTrans, then enable `paddleocr_local` in Settings -> OCR -> Recognition services:

```powershell
pip install paddleocr
```

By default the plugin does not pass an explicit PaddleOCR inference engine. If you set `engine` in its `config.json`, install the matching inference dependency, such as `onnxruntime` for `onnxruntime` or `paddlepaddle` for Paddle engines.

The plugin does not bundle PaddleOCR models or Python. PaddleOCR may download models on first use depending on your local PaddleOCR setup.

## Backup And Sync

Settings -> Backup & restore provides local import/export. Export waits for pending edits to save. Import asks for confirmation and creates a recovery ZIP under `%LOCALAPPDATA%\RabTrans\backups` before replacing files; a failed import restores that recovery copy.

The sync package is a zip file containing:

```text
settings.json
plugins\
history.jsonl       optional
```

User translation plugins under `plugins\translation\` and user OCR plugins under `plugins\ocr\` are always included because they define the user-configured interfaces. Translation history is optional and controlled by the `Include translation history` checkbox.

## Plugin Development

RabTrans plugins are small folders containing a `plugin.json` manifest and an executable entry file. New plugins should use the `process` runtime. The app sends one JSON object to the process through `stdin`; the process writes one JSON object to `stdout`.

### Manifest Fields

Common fields:

```json
{
  "id": "my-plugin",
  "name": "My Plugin",
  "type": "translation",
  "runtime": "process",
  "entry": "index.js",
  "capabilities": ["translation.text"],
  "command": "node",
  "arguments": "{entry}"
}
```

Field notes:

- `id`: Stable unique plugin id. Use lowercase letters, numbers, `_`, or `-`. Settings displays this id.
- `name`: Human-readable provider name shown on result cards and history records.
- `type`: Use `translation` for translation plugins and `ocr` for OCR plugins.
- `runtime`: Use `process`.
- `entry`: Path to the executable script relative to the plugin folder.
- `capabilities`: Use `translation.text` or `ocr.image`.
- `command`: Usually `node` for JavaScript plugins. When omitted for `.js`, the app also resolves to Node.
- `arguments`: Use `{entry}` to pass the resolved entry path.
- Translation and OCR settings belong in `config.json` and are delivered as `input.config`; no `configSchema` declaration is required.

### Translation Plugin

Folder layout:

```text
my-translator/
  plugin.json
  config.json       optional, plugin-defined settings
  index.js
```

`plugin.json`:

```json
{
  "id": "my_translator",
  "name": "My Translator",
  "type": "translation",
  "runtime": "process",
  "entry": "index.js",
  "capabilities": ["translation.text"],
  "command": "node",
  "arguments": "{entry}"
}
```

Input sent to `stdin`:

```json
{
  "kind": "translation.text",
  "text": "Hello",
  "from": "en",
  "to": "zh-CN",
  "requestId": "...",
  "config": {}
}
```

Output expected on `stdout`:

```json
{
  "text": "translated text"
}
```

Minimal JavaScript entry:

```js
main().catch((error) => {
  console.error(error?.stack || String(error));
  process.exit(1);
});

async function main() {
  const input = await readInput();
  const translated = await translate(input.text || '', input.from || 'auto', input.to || 'zh-CN');
  process.stdout.write(JSON.stringify({ text: translated }));
}

async function translate(text, from, to) {
  return text;
}

function readInput() {
  return new Promise((resolve, reject) => {
    let raw = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => raw += chunk);
    process.stdin.on('end', () => {
      try { resolve(JSON.parse(raw.replace(/^\uFEFF/, '') || '{}')); }
      catch (error) { reject(error); }
    });
  });
}
```

### OCR Plugin

Folder layout:

```text
my-ocr/
  plugin.json
  config.json       optional, plugin-defined settings
  index.js
```

`plugin.json`:

```json
{
  "id": "my_ocr",
  "name": "My OCR",
  "type": "ocr",
  "runtime": "process",
  "entry": "index.js",
  "capabilities": ["ocr.image"],
  "command": "node",
  "arguments": "{entry}"
}
```

Input sent to `stdin`:

```json
{
  "kind": "ocr.image",
  "imageBase64": "...",
  "imageDataUri": "data:image/png;base64,...",
  "mimeType": "image/png",
  "requestId": "...",
  "config": {}
}
```

Output expected on `stdout`:

```json
{
  "text": "recognized text"
}
```

### Loading Rules

- Translation service selection only loads plugins with `type: "translation"`.
- OCR only loads plugins with `type: "ocr"`.
- Built-in translation plugins live under `src\RabTrans.App\Plugins\translation\<id>`.
- Built-in OCR plugins live under `src\RabTrans.App\Plugins\ocr\<id>`.
- User translation plugins should be copied to `%LOCALAPPDATA%\RabTrans\plugins\translation\<id>`.
- User OCR plugins should be copied to `%LOCALAPPDATA%\RabTrans\plugins\ocr\<id>`.
- Use the refresh icon in Settings -> Translation / OCR after adding or changing plugin manifests while the app is running.
- Translation and OCR plugin enablement is managed in their respective Settings pages, not in `plugin.json`.

### Error Handling

- Write machine-readable result JSON to `stdout` only.
- Write diagnostics to `stderr`.
- Exit with code `0` on success.
- Exit with a non-zero code when the plugin cannot produce a valid result.
- The app reads plugin I/O as UTF-8, so scripts should also use UTF-8.
