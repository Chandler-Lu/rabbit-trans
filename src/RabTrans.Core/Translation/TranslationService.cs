using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RabTrans.Core.Networking;
using RabTrans.Core.Plugins;

namespace RabTrans.Core.Translation;

/// <summary>
/// Translation service backed by external process plugins.
/// </summary>
public class TranslationService : IDisposable
{
    private readonly Dictionary<string, ITranslationProvider> _providers;
    private readonly Dictionary<string, string> _providerDisplayNames = new();
    private string _currentProvider = "google";
    private bool _disposed = false;

    public TranslationService()
    {
        _providers = new Dictionary<string, ITranslationProvider>();

        LoadPluginProviders();
    }

    /// <summary>
    /// Sets the current translation provider.
    /// </summary>
    public void SetProvider(string providerName)
    {
        if (_providers.ContainsKey(providerName))
        {
            _currentProvider = providerName;
        }
    }

    /// <summary>
    /// Gets available translation providers.
    /// </summary>
    public IReadOnlyList<string> GetProviders() => _providers.Keys.ToList();

    public string GetProviderDisplayName(string providerName)
    {
        return _providerDisplayNames.TryGetValue(providerName, out var displayName)
            ? displayName
            : providerName;
    }

    /// <summary>
    /// Registers a translation provider. Plugin providers can be attached through this entry point.
    /// </summary>
    public void RegisterProvider(string providerName, ITranslationProvider provider)
    {
        _providers[providerName] = provider;
    }

    public void RegisterProvider(string providerName, string displayName, ITranslationProvider provider)
    {
        RegisterProvider(providerName, provider);
        _providerDisplayNames[providerName] = string.IsNullOrWhiteSpace(displayName) ? providerName : displayName;
    }

    public void ReloadProviders()
    {
        _providers.Clear();
        _providerDisplayNames.Clear();
        LoadPluginProviders();

        if (!_providers.ContainsKey(_currentProvider))
        {
            _currentProvider = _providers.Keys.FirstOrDefault() ?? "google";
        }
    }

    private void LoadPluginProviders()
    {
        var pluginDirectories = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Plugins", "translation"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RabTrans",
                "plugins",
                "translation")
        };

        Directory.CreateDirectory(pluginDirectories[1]);

        foreach (var manifestPath in pluginDirectories
            .Where(Directory.Exists)
            .SelectMany(EnumeratePluginManifestFiles))
        {
            try
            {
                var manifest = TranslationPluginManifest.Load(manifestPath);

                if (manifest == null ||
                    string.IsNullOrWhiteSpace(manifest.Id) ||
                    !manifest.Type.Equals("translation", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (manifest.Runtime.Equals("process", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(manifest.Entry))
                {
                    RegisterProvider(manifest.Id, manifest.Name, new ProcessTranslationProvider(manifest));
                }
            }
            catch
            {
                // Ignore malformed plugin manifests. They should not block app startup.
            }
        }
    }

    private static IEnumerable<string> EnumeratePluginManifestFiles(string directory)
    {
        foreach (var manifestPath in Directory.EnumerateFiles(directory, "plugin.json", SearchOption.AllDirectories))
        {
            yield return manifestPath;
        }
    }

    /// <summary>
    /// Translates text from source language to target language.
    /// </summary>
    public async Task<TranslationResult> TranslateAsync(string text, string from, string to)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TranslationResult { Success = false, ErrorMessage = "Empty text" };
        }

        if (!_providers.TryGetValue(_currentProvider, out var provider))
        {
            return new TranslationResult { Success = false, ErrorMessage = "Unknown provider" };
        }

        try
        {
            var result = await provider.TranslateAsync(text, from, to);
            result.ProviderId = _currentProvider;
            result.ProviderName = GetProviderDisplayName(_currentProvider);
            return result;
        }
        catch (Exception ex)
        {
            return new TranslationResult 
            { 
                Success = false, 
                ErrorMessage = ex.Message 
            };
        }
    }

    /// <summary>
    /// Translates with multiple providers in parallel.
    /// </summary>
    public async Task<IReadOnlyList<TranslationResult>> TranslateWithProvidersAsync(
        string text,
        string from,
        string to,
        IEnumerable<string>? providerNames = null)
    {
        var tasks = GetSelectedProviders(providerNames)
            .Select(providerName => TranslateWithProviderAsync(providerName, text, from, to));
        return await Task.WhenAll(tasks);
    }

    public async IAsyncEnumerable<TranslationResult> TranslateWithProvidersAsCompletedAsync(
        string text,
        string from,
        string to,
        IEnumerable<string>? providerNames = null)
    {
        var selectedProviders = GetSelectedProviders(providerNames);
        var tasks = selectedProviders
            .Select(providerName => TranslateWithProviderAsync(providerName, text, from, to))
            .ToList();

        while (tasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(tasks);
            tasks.Remove(completedTask);
            yield return await completedTask;
        }
    }

    private List<string> GetSelectedProviders(IEnumerable<string>? providerNames)
    {
        var selectedProviders = (providerNames ?? _providers.Keys)
            .Where(_providers.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedProviders.Count == 0)
        {
            selectedProviders.Add(_currentProvider);
        }

        return selectedProviders;
    }

    private async Task<TranslationResult> TranslateWithProviderAsync(string providerName, string text, string from, string to)
    {
        var displayName = GetProviderDisplayName(providerName);
        try
        {
            var result = await _providers[providerName].TranslateAsync(text, from, to);
            result.ProviderId = providerName;
            result.ProviderName = displayName;
            return result;
        }
        catch (Exception ex)
        {
            return new TranslationResult
            {
                ProviderName = displayName,
                ProviderId = providerName,
                Success = false,
                SourceLanguage = from,
                TargetLanguage = to,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Detects the language of the given text.
    /// </summary>
    public async Task<string> DetectLanguageAsync(string text)
    {
        // Simple language detection based on character ranges
        if (string.IsNullOrWhiteSpace(text))
            return "en";

        // Japanese text often contains CJK ideographs too, so detect kana before Chinese.
        if (text.Any(c => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF)))
            return "ja";

        // Check for Chinese characters
        if (text.Any(c => c >= 0x4E00 && c <= 0x9FFF))
            return "zh-CN";

        // Check for Korean characters
        if (text.Any(c => c >= 0xAC00 && c <= 0xD7AF))
            return "ko";

        // Check for Cyrillic
        if (text.Any(c => c >= 0x0400 && c <= 0x04FF))
            return "ru";

        // Check for Arabic
        if (text.Any(c => c >= 0x0600 && c <= 0x06FF))
            return "ar";

        // Default to English
        return "en";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// Translation result.
/// </summary>
public class TranslationResult
{
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string TranslatedText { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Interface for translation providers.
/// </summary>
public interface ITranslationProvider
{
    Task<TranslationResult> TranslateAsync(string text, string from, string to);
}

public class TranslationPluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "translation";
    public string Runtime { get; set; } = "process";
    public string[] Capabilities { get; set; } = Array.Empty<string>();
    public string? Entry { get; set; }
    public string Command { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    [JsonIgnore]
    public string PluginDirectory { get; set; } = string.Empty;
    public Dictionary<string, PluginConfigField> ConfigSchema { get; set; } = new();

    public static TranslationPluginManifest? Load(string manifestPath)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var manifest = JsonSerializer.Deserialize<TranslationPluginManifest>(File.ReadAllText(manifestPath), options);
        if (manifest != null)
        {
            manifest.PluginDirectory = Path.GetDirectoryName(manifestPath) ?? string.Empty;
        }

        return manifest;
    }
}

public class PluginConfigField
{
    public string Type { get; set; } = "string";
    public string Label { get; set; } = string.Empty;
    public string Default { get; set; } = string.Empty;
    public bool Secret { get; set; }
    public bool Required { get; set; }
}

public class ProcessTranslationProvider : ITranslationProvider
{
    private readonly TranslationPluginManifest _manifest;

    public ProcessTranslationProvider(TranslationPluginManifest manifest)
    {
        _manifest = manifest;
    }

    public async Task<TranslationResult> TranslateAsync(string text, string from, string to)
    {
        var input = JsonSerializer.Serialize(new
        {
            kind = "translation.text",
            text,
            from,
            to,
            requestId = Guid.NewGuid().ToString("N")
        });

        var output = await RunProcessAsync(input);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var translatedText = ReadString(root, "text") ?? ReadString(root, "translatedText") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(translatedText))
        {
            return new TranslationResult
            {
                Success = false,
                SourceLanguage = from,
                TargetLanguage = to,
                ErrorMessage = "Process plugin returned empty translation"
            };
        }

        return new TranslationResult
        {
            Success = true,
            TranslatedText = translatedText,
            SourceLanguage = from,
            TargetLanguage = to
        };
    }

    private async Task<string> RunProcessAsync(string input)
    {
        var entryPath = Path.Combine(_manifest.PluginDirectory, _manifest.Entry ?? string.Empty);
        var command = PluginRuntimeOptions.ResolveCommand(_manifest.Command, entryPath);
        var arguments = string.IsNullOrWhiteSpace(_manifest.Arguments)
            ? Quote(entryPath)
            : _manifest.Arguments.Replace("{entry}", Quote(entryPath), StringComparison.Ordinal);

        var startInfo = new ProcessStartInfo(command, arguments)
        {
            WorkingDirectory = _manifest.PluginDirectory,
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

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value)
            ? value.GetString()
            : null;
    }
}
