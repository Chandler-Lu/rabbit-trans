using RabTrans.Core.Plugins;
using RabTrans.Core.OCR;
using System.Text.Json;
using RabTrans.Core.Translation;
using RabTrans.Core.Storage;
using RabTrans.Core.Sync;
using RabTrans.Core.Networking;

var root = Path.Combine(Path.GetTempPath(), "RabTrans-config-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
NetworkProxyOptions.Mode = ProxyMode.None;
var checks = 0;
try
{
    var builtIn = Path.Combine(root, "built-in");
    var appData = Path.Combine(root, "appdata");
    var user = Path.Combine(appData, "plugins", "translation");
    const string echo = "let raw=''; process.stdin.setEncoding('utf8'); process.stdin.on('data', c=>raw+=c); process.stdin.on('end',()=>process.stdout.write(JSON.stringify({text:JSON.stringify(JSON.parse(raw.replace(/^\\uFEFF/,'')))})));";
    MakePlugin(builtIn, "echo-template", "echo", echo);
    MakePlugin(builtIn, "no-config", "google", echo);
    MakePlugin(user, "folder-not-id", "custom_id", echo);
    var template = Path.Combine(builtIn, "echo-template", "config.json");
    File.WriteAllText(template, "{\"default\":true}");
    using var service = new TranslationService(builtIn, user);
    var configPath = service.GetProviderConfigPath("echo")!;
    Check(configPath == Path.Combine(user, "echo", "config.json") && File.ReadAllText(configPath) == File.ReadAllText(template), "built-in template copied to user directory");
    Check(service.GetProviderConfigPath("google") == null && !File.Exists(Path.Combine(user, "google", "config.json")), "no unnecessary Google config");
    Check(service.OpenProviderConfiguration("custom_id") == Path.Combine(user, "folder-not-id", "config.json"), "custom plugins use their existing folder, not their id");
    const string arbitrary = """
        {"text":"cannot replace input","apiKey":"fixture-only","vendor":{"items":[0,false,"",null,{"自定义":"值"}],"numeric":1.25},"unknown-key":true,"large":9007199254740993}
        """;
    File.WriteAllText(configPath, arbitrary);
    var read = PluginConfiguration.Read(configPath);
    Check(read.GetProperty("large").GetInt64() == 9007199254740993L, "host retains JSON number precision");
    using (var response = await Echo(service, "echo"))
    {
        var input = response.RootElement;
        Check(input.GetProperty("text").GetString() == "hello" && input.GetProperty("from").GetString() == "en" && input.GetProperty("to").GetString() == "zh-CN", "config cannot overwrite input");
        var config = input.GetProperty("config");
        Check(config.GetProperty("unknown-key").GetBoolean(), "unknown fields preserved");
        var items = config.GetProperty("vendor").GetProperty("items");
        Check(items[0].GetInt32() == 0 && !items[1].GetBoolean() && items[2].GetString() == "" && items[3].ValueKind == JsonValueKind.Null && items[4].GetProperty("自定义").GetString() == "值", "nested arrays, zero, false, empty, null and Unicode preserved");
    }
    service.ReloadProviders();
    Check(File.ReadAllText(configPath) == arbitrary, "reload never overwrites existing config");
    File.WriteAllText(configPath, "{\"newField\":false}");
    using (var response = await Echo(service, "echo"))
        Check(response.RootElement.GetProperty("config").TryGetProperty("newField", out var value) && !value.GetBoolean(), "editing affects next request without reload");
    foreach (var invalid in new[] { "[]", "null", "true", "\"private-value\"", "{\"private-value\":" })
    {
        File.WriteAllText(configPath, invalid);
        var result = (await service.TranslateWithProvidersAsync("hello", "en", "zh-CN", new[] { "echo" }))[0];
        Check(!result.Success && result.ErrorMessage!.Contains("config.json") && !result.ErrorMessage.Contains("private-value"), "invalid config gives value-free error");
    }
    File.Delete(configPath);
    using (var response = await Echo(service, "echo"))
        Check(!response.RootElement.GetProperty("config").EnumerateObject().Any(), "missing config is an empty object");
    File.WriteAllText(configPath, arbitrary);
    using var storage = new StorageService(appData);
    await storage.SetAsync("enabled_translation_providers", new[] { "custom_id", "echo" });
    var sync = new ConfigSyncService(storage);
    var package = Path.Combine(root, "backup.zip");
    sync.CreateConfigPackage(package, false);
    File.WriteAllText(configPath, "{}");
    sync.ImportConfigPackage(package);
    Check(File.ReadAllText(configPath) == arbitrary, "export/import preserves every config field and file bytes");
    Check((await storage.GetAsync<string[]>("enabled_translation_providers"))!.SequenceEqual(new[] { "custom_id", "echo" }), "export/import preserves service order");
    var ocrBuiltIn = Path.Combine(root, "ocr-built-in");
    var ocrUser = Path.Combine(appData, "plugins", "ocr");
    MakePlugin(ocrBuiltIn, "builtin", "ocr_echo", echo, "ocr");
    MakePlugin(ocrUser, "custom-folder", "ocr_custom", echo, "ocr");
    File.WriteAllText(Path.Combine(ocrBuiltIn, "builtin", "config.json"), "{\"lang\":\"ch\"}");
    using var ocr = new OcrService(ocrBuiltIn, ocrUser);
    var ocrConfig = ocr.GetProviderConfigPath("ocr_echo")!;
    Check(ocrConfig == Path.Combine(ocrUser, "ocr_echo", "config.json"), "OCR built-in uses user configuration path");
    Check(ocr.OpenProviderConfiguration("ocr_custom") == Path.Combine(ocrUser, "custom-folder", "config.json"), "OCR custom config follows actual plugin directory");
    File.WriteAllText(ocrConfig, arbitrary);
    using (var response = JsonDocument.Parse(await ocr.RecognizeAsync(new byte[] { 1, 2, 3 }, new[] { "ocr_echo" })))
    {
        var input = response.RootElement;
        Check(input.GetProperty("imageBase64").GetString() == "AQID" && input.GetProperty("kind").GetString() == "ocr.image", "OCR image input stays separate from configuration");
        Check(input.GetProperty("config").GetProperty("vendor").GetProperty("items")[3].ValueKind == JsonValueKind.Null, "OCR passes arbitrary nested configuration");
    }
    File.WriteAllText(ocrConfig, "{\"revision\":2}");
    using (var response = JsonDocument.Parse(await ocr.RecognizeAsync(new byte[] { 1 }, new[] { "ocr_echo" })))
        Check(response.RootElement.GetProperty("config").GetProperty("revision").GetInt32() == 2, "OCR config edits apply on next request");
    File.WriteAllText(ocrConfig, "{\"private-value\":");
    try
    {
        await ocr.RecognizeAsync(new byte[] { 1 }, new[] { "ocr_echo" });
        throw new Exception("invalid OCR config was accepted");
    }
    catch (InvalidOperationException error)
    {
        Check(error.Message.Contains("config.json") && !error.ToString().Contains("private-value"), "OCR config errors do not include values");
    }
    using (var response = JsonDocument.Parse(await ocr.RecognizeAsync(new byte[] { 1 }, new[] { "ocr_echo", "ocr_custom" })))
        Check(!response.RootElement.GetProperty("config").EnumerateObject().Any(), "OCR still falls back to the next enabled service");
    File.WriteAllText(ocrConfig, arbitrary);
    ocr.ReloadPlugins();
    Check(File.ReadAllText(ocrConfig) == arbitrary, "OCR reload preserves custom parameters");
    sync.CreateConfigPackage(package, false);
    File.WriteAllText(ocrConfig, "{}");
    sync.ImportConfigPackage(package);
    Check(File.ReadAllText(ocrConfig) == arbitrary, "OCR configuration survives export/import");
    Console.WriteLine($"PASS: {checks} service configuration checks");
}
finally { Directory.Delete(root, true); }

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
    Console.WriteLine("PASS: " + message);
}

static void MakePlugin(string root, string folder, string id, string script, string type = "translation")
{
    var directory = Path.Combine(root, folder);
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(new { id, name = id, type, runtime = "process", entry = "index.js", command = "node", arguments = "{entry}" }));
    File.WriteAllText(Path.Combine(directory, "index.js"), script);
}

static async Task<JsonDocument> Echo(TranslationService service, string id)
{
    var result = (await service.TranslateWithProvidersAsync("hello", "en", "zh-CN", new[] { id }))[0];
    if (!result.Success) throw new Exception(result.ErrorMessage);
    return JsonDocument.Parse(result.TranslatedText);
}
