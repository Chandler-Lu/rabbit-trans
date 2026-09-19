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

Network proxy behavior is configured in Settings -> General:

- `Use system proxy`: process plugins inherit the system proxy environment where supported.
- `No proxy`: RabTrans clears common proxy environment variables for plugin processes.
- `HTTP proxy`: RabTrans sets `HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`, and lowercase variants for plugin processes.

For Node.js process plugins, RabTrans also sets `NODE_USE_ENV_PROXY=1` when proxy environment variables should be used.

## Local PaddleOCR

RabTrans includes a built-in OCR plugin named `paddleocr_local`. It calls the Python executable configured in Settings -> General -> Runtime Paths and imports the local `paddleocr` package.

Install PaddleOCR in the Python environment used by RabTrans, then enable `paddleocr_local` in Settings -> Interfaces -> OCR Services:

```powershell
pip install paddleocr
```

By default the plugin does not pass an explicit PaddleOCR inference engine. If you set `RABTRANS_PADDLEOCR_ENGINE`, install the matching inference dependency, such as `onnxruntime` for `onnxruntime` or `paddlepaddle` for Paddle engines.

The plugin does not bundle PaddleOCR models or Python. PaddleOCR may download models on first use depending on your local PaddleOCR setup.

## Backup And Sync

Settings -> Sync provides local import/export.

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
- `configSchema`: Optional metadata for user-facing config fields. Secrets should set `secret: true`.

### Translation Plugin

Folder layout:

```text
my-translator/
  plugin.json
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
  "requestId": "..."
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
  "requestId": "..."
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
- Use Settings -> Hot Reload after changing plugin files while the app is running.
- Translation and OCR plugin enablement is managed in Settings -> Interfaces, not in `plugin.json`.

### Error Handling

- Write machine-readable result JSON to `stdout` only.
- Write diagnostics to `stderr`.
- Exit with code `0` on success.
- Exit with a non-zero code when the plugin cannot produce a valid result.
- The app reads plugin I/O as UTF-8, so scripts should also use UTF-8.
