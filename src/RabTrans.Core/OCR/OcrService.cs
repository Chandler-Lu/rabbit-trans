using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RabTrans.Core.Networking;
using RabTrans.Core.Plugins;

namespace RabTrans.Core.OCR;

/// <summary>
/// OCR service backed by external process plugins.
/// </summary>
public class OcrService : IDisposable
{
    private readonly List<OcrPluginManifest> _plugins;
    private bool _disposed;
    private readonly string _builtInPluginDirectory;
    private readonly string _userPluginDirectory;
    private readonly Dictionary<string, string?> _providerConfigPaths = new(StringComparer.OrdinalIgnoreCase);

    public OcrService(string? builtInPluginDirectory = null, string? userPluginDirectory = null)
    {
        _builtInPluginDirectory = builtInPluginDirectory ?? Path.Combine(AppContext.BaseDirectory, "Plugins", "ocr");
        _userPluginDirectory = userPluginDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RabTrans", "plugins", "ocr");
        _plugins = LoadPluginManifests();
    }

    public IReadOnlyList<string> GetSupportedLanguages()
    {
        return _plugins.Select(plugin => plugin.Name).ToList();
    }

    public IReadOnlyList<string> GetProviders() => _plugins.Select(plugin => plugin.Id).ToList();

    public string GetProviderDisplayName(string providerName)
    {
        return _plugins.FirstOrDefault(plugin => plugin.Id.Equals(providerName, StringComparison.OrdinalIgnoreCase))?.Name
            ?? providerName;
    }

    public string? GetProviderConfigPath(string providerName) => _providerConfigPaths.GetValueOrDefault(providerName);

    public string OpenProviderConfiguration(string providerName)
    {
        var path = GetProviderConfigPath(providerName)
            ?? throw new InvalidOperationException("This service does not need configuration.");
        PluginConfiguration.CreateIfMissing(path);
        return path;
    }

    public void ReloadPlugins()
    {
        _plugins.Clear();
        _plugins.AddRange(LoadPluginManifests());
    }

    public async Task<string> RecognizeAsync(Stream imageStream, IEnumerable<string>? providerNames = null)
    {
        if (_plugins.Count == 0)
        {
            throw new InvalidOperationException("No OCR plugin configured.");
        }

        using var memoryStream = new MemoryStream();
        await imageStream.CopyToAsync(memoryStream);
        var imageBytes = memoryStream.ToArray();
        var selectedPlugins = GetSelectedPlugins(providerNames);
        if (selectedPlugins.Count == 0)
        {
            throw new InvalidOperationException("No OCR plugin enabled.");
        }

        Exception? lastError = null;
        foreach (var plugin in selectedPlugins)
        {
            try
            {
                var result = await RecognizeWithProcessPluginAsync(plugin, imageBytes);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError != null)
        {
            throw new InvalidOperationException(lastError.Message, lastError);
        }

        return string.Empty;
    }

    public async Task<string> RecognizeAsync(byte[] imageData, IEnumerable<string>? providerNames = null)
    {
        using var stream = new MemoryStream(imageData);
        return await RecognizeAsync(stream, providerNames);
    }

    public async Task<string> RecognizeFromFileAsync(string filePath, IEnumerable<string>? providerNames = null)
    {
        using var stream = File.OpenRead(filePath);
        return await RecognizeAsync(stream, providerNames);
    }

    private List<OcrPluginManifest> GetSelectedPlugins(IEnumerable<string>? providerNames)
    {
        var selectedProviderNames = (providerNames ?? _plugins.Select(plugin => plugin.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return selectedProviderNames
            .Select(providerName => _plugins.FirstOrDefault(plugin => plugin.Id.Equals(providerName, StringComparison.OrdinalIgnoreCase)))
            .Where(plugin => plugin != null)
            .Cast<OcrPluginManifest>()
            .ToList();
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
            requestId = Guid.NewGuid().ToString("N"),
            config = PluginConfiguration.Read(plugin.ConfigPath)
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
        NetworkProxyOptions.ApplyToProcessEnvironment(startInfo);
        PluginRuntimeOptions.ApplyCommonEnvironment(startInfo);

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
            error = StripAnsiEscapeSequences(error).Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Plugin process exited with code {process.ExitCode}"
                : error);
        }

        return output;
    }

    private static string StripAnsiEscapeSequences(string value)
    {
        return string.IsNullOrEmpty(value)
            ? value
            : Regex.Replace(value, @"\x1B\[[0-?]*[ -/]*[@-~]", string.Empty);
    }

    private static string QuoteProcessArgument(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private List<OcrPluginManifest> LoadPluginManifests()
    {
        var plugins = new Dictionary<string, OcrPluginManifest>(StringComparer.OrdinalIgnoreCase);
        _providerConfigPaths.Clear();
        Directory.CreateDirectory(_userPluginDirectory);
        foreach (var directory in new[] { _builtInPluginDirectory, _userPluginDirectory }.Where(Directory.Exists))
        foreach (var manifestPath in EnumeratePluginManifestFiles(directory))
        {
            var manifest = TryLoadManifest(manifestPath);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id) ||
                !manifest.Type.Equals("ocr", StringComparison.OrdinalIgnoreCase) ||
                !manifest.Runtime.Equals("process", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(manifest.Entry)) continue;
            try
            {
                var builtIn = directory == _builtInPluginDirectory;
                if (builtIn && !Regex.IsMatch(manifest.Id, @"\A[a-zA-Z0-9_-]+\z")) continue;
                var templatePath = Path.Combine(manifest.PluginDirectory, "config.json");
                manifest.ConfigPath = builtIn
                    ? Path.Combine(_userPluginDirectory, manifest.Id, "config.json")
                    : templatePath;
                if (builtIn && File.Exists(templatePath))
                    PluginConfiguration.CreateIfMissing(manifest.ConfigPath, templatePath);
                // User plugins override a built-in with the same id, as translation plugins do.
                plugins[manifest.Id] = manifest;
                _providerConfigPaths[manifest.Id] = !builtIn || File.Exists(manifest.ConfigPath) ? manifest.ConfigPath : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One inaccessible plugin must not prevent other services from loading.
            }
        }
        return plugins.Values.ToList();
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
    [JsonIgnore]
    public string ConfigPath { get; set; } = string.Empty;

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
