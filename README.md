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
- Translation history window with copy support.
- Settings for startup, hotkeys, Node path, interface selection, configuration reload, and sync.
- Import/export for user settings and user plugin interfaces.
- Local configuration and plugin files under `%LOCALAPPDATA%\RabTrans`.

## Build

Requirements:

- Windows 10/11
- .NET 10 SDK
- Optional: Node.js for JavaScript process plugins

Build from the repository root:

```powershell
dotnet build src\RabTrans.App\RabTrans.App.csproj
```

The debug executable is emitted under:

```text
src\RabTrans.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\RabTrans.exe
```

## Runtime Configuration

Runtime data is stored in:

```text
%LOCALAPPDATA%\RabTrans
```

Important files and folders:

```text
settings.json       User settings and enabled interface order
history.jsonl       Translation history
plugins\           User translation plugins
ocr-plugins\       User OCR plugins
logs\              Application logs
```

Built-in plugins are copied from:

```text
src\RabTrans.App\Plugins
```

User plugins are loaded from:

```text
%LOCALAPPDATA%\RabTrans\plugins
%LOCALAPPDATA%\RabTrans\ocr-plugins
```

Plugin execution uses the configured Node executable path when provided, otherwise it falls back to `node` from `PATH`.

## Backup And Sync

Settings -> Sync provides local import/export.

The sync package is a zip file containing:

```text
settings.json
plugins\
ocr-plugins\
history.jsonl       optional
```

User translation plugins under `plugins\` and user OCR plugins under `ocr-plugins\` are always included because they define the user-configured interfaces. Translation history is optional and controlled by the `Include translation history` checkbox.

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
- Built-in translation plugins live under `src\RabTrans.App\Plugins\<id>`.
- Built-in OCR plugins live under `src\RabTrans.App\Plugins\OCR\<id>`.
- User translation plugins should be copied to `%LOCALAPPDATA%\RabTrans\plugins\<id>`.
- User OCR plugins should be copied to `%LOCALAPPDATA%\RabTrans\ocr-plugins\<id>`.
- Use Settings -> Hot Reload after changing plugin files while the app is running.
- Translation and OCR plugin enablement is managed in Settings -> Interfaces, not in `plugin.json`.

### Error Handling

- Write machine-readable result JSON to `stdout` only.
- Write diagnostics to `stderr`.
- Exit with code `0` on success.
- Exit with a non-zero code when the plugin cannot produce a valid result.
- The app reads plugin I/O as UTF-8, so scripts should also use UTF-8.
