using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RabTrans.Core.Plugins;

namespace RabTrans.Core.OCR;

/// <summary>
/// OCR service backed by external process plugins.
/// </summary>
public class OcrService : IDisposable
{
    private readonly List<OcrPluginManifest> _plugins;
    private bool _disposed;

    public OcrService()
    {
        _plugins = LoadPluginManifests();
    }

    public IReadOnlyList<string> GetSupportedLanguages()
    {
        return _plugins.Select(plugin => plugin.Name).ToList();
    }

    public void ReloadPlugins()
    {
        _plugins.Clear();
        _plugins.AddRange(LoadPluginManifests());
    }

    public async Task<string> RecognizeAsync(Stream imageStream)
    {
        if (_plugins.Count == 0)
        {
            throw new InvalidOperationException("No OCR plugin configured. Enable an OCR plugin json in Plugins/OCR.");
        }

        using var memoryStream = new MemoryStream();
        await imageStream.CopyToAsync(memoryStream);
        var imageBytes = memoryStream.ToArray();

        foreach (var plugin in _plugins)
        {
            var result = await RecognizeWithProcessPluginAsync(plugin, imageBytes);
            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        return string.Empty;
    }

    public async Task<string> RecognizeAsync(byte[] imageData)
    {
        using var stream = new MemoryStream(imageData);
        return await RecognizeAsync(stream);
    }

    public async Task<string> RecognizeFromFileAsync(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return await RecognizeAsync(stream);
    }

    private async Task<string> RecognizeWithProcessPluginAsync(OcrPluginManifest plugin, byte[] imageBytes)
    {
        var imageBase64 = Convert.ToBase64String(imageBytes);
        var input = JsonSerializer.Serialize(new
        {
            kind = "ocr.image",
            imageBase64,
            imageDataUri = $"data:image/png;base64,{imageBase64}",
            mimeType = "image/png",
            requestId = Guid.NewGuid().ToString("N")
        });

        var output = await RunProcessPluginAsync(plugin, input);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        return root.TryGetProperty("text", out var text)
            ? text.GetString() ?? string.Empty
            : string.Empty;
    }

    private static async Task<string> RunProcessPluginAsync(OcrPluginManifest plugin, string input)
    {
        var entryPath = Path.Combine(plugin.PluginDirectory, plugin.Entry ?? string.Empty);
        var command = PluginRuntimeOptions.ResolveCommand(plugin.Command, entryPath);
        var arguments = string.IsNullOrWhiteSpace(plugin.Arguments)
            ? QuoteProcessArgument(entryPath)
            : plugin.Arguments.Replace("{entry}", QuoteProcessArgument(entryPath), StringComparison.Ordinal);

        var startInfo = new ProcessStartInfo(command, arguments)
        {
            WorkingDirectory = plugin.PluginDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start plugin process");
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Plugin process exited with code {process.ExitCode}"
                : error.Trim());
        }

        return output;
    }

    private static string QuoteProcessArgument(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static List<OcrPluginManifest> LoadPluginManifests()
    {
        var pluginDirectories = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Plugins", "OCR"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RabTrans",
                "ocr-plugins")
        };

        Directory.CreateDirectory(pluginDirectories[1]);

        return pluginDirectories
            .Where(Directory.Exists)
            .SelectMany(EnumeratePluginManifestFiles)
            .Select(TryLoadManifest)
            .Where(manifest => manifest is { Enabled: true } &&
                manifest.Type.Equals("ocr", StringComparison.OrdinalIgnoreCase) &&
                manifest.Runtime.Equals("process", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(manifest.Entry))
            .Cast<OcrPluginManifest>()
            .ToList();
    }

    private static IEnumerable<string> EnumeratePluginManifestFiles(string directory)
    {
        foreach (var manifestPath in Directory.EnumerateFiles(directory, "plugin.json", SearchOption.AllDirectories))
        {
            yield return manifestPath;
        }
    }

    private static OcrPluginManifest? TryLoadManifest(string manifestPath)
    {
        try
        {
            return OcrPluginManifest.Load(manifestPath);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

public class OcrPluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "ocr";
    public string Runtime { get; set; } = "process";
    public string[] Capabilities { get; set; } = Array.Empty<string>();
    public string? Entry { get; set; }
    public string Command { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    [JsonIgnore]
    public string PluginDirectory { get; set; } = string.Empty;
    public bool Enabled { get; set; } = false;
    public Dictionary<string, OcrPluginConfigField> ConfigSchema { get; set; } = new();

    public static OcrPluginManifest? Load(string manifestPath)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var manifest = JsonSerializer.Deserialize<OcrPluginManifest>(File.ReadAllText(manifestPath), options);
        if (manifest != null)
        {
            manifest.PluginDirectory = Path.GetDirectoryName(manifestPath) ?? string.Empty;
        }

        return manifest;
    }
}

public class OcrPluginConfigField
{
    public string Type { get; set; } = "string";
    public string Label { get; set; } = string.Empty;
    public string Default { get; set; } = string.Empty;
    public bool Secret { get; set; }
    public bool Required { get; set; }
}

/// <summary>
/// Result from OCR operation.
/// </summary>
public class OcrResult
{
    public string Text { get; set; } = string.Empty;
    public IReadOnlyList<OcrLine> Lines { get; set; } = Array.Empty<OcrLine>();
    public string Language { get; set; } = string.Empty;
}

public class OcrLine
{
    public string Text { get; set; } = string.Empty;
    public System.Drawing.Rectangle BoundingBox { get; set; }
    public IReadOnlyList<OcrWord> Words { get; set; } = Array.Empty<OcrWord>();
}

public class OcrWord
{
    public string Text { get; set; } = string.Empty;
    public System.Drawing.Rectangle BoundingBox { get; set; }
}
