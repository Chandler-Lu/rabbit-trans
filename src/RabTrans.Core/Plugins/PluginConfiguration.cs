using System.Text.Json;

namespace RabTrans.Core.Plugins;

/// <summary>Opaque plugin-owned JSON. The host never filters or coerces its fields.</summary>
public static class PluginConfiguration
{
    public static JsonElement Read(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.Exists(path) ? File.ReadAllText(path) : "{}");
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("config.json must contain a JSON object.");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Parser exceptions can contain configuration values. Do not retain the inner exception.
            throw new InvalidOperationException("Invalid JSON in config.json. Check its syntax and save again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Cannot read config.json. Check file access and try again.");
        }
    }

    public static void CreateIfMissing(string path, string? templatePath = null)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            if (templatePath != null)
            {
                File.Copy(templatePath, path, overwrite: false);
            }
            else
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.WriteLine("{}");
            }
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another caller created it. Never overwrite an existing user configuration.
        }
    }
}
