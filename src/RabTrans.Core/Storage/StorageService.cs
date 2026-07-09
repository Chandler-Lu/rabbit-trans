using System.Text;
using System.Text.Json;

namespace RabTrans.Core.Storage;

/// <summary>
/// Storage service using JSON files for settings and translation history.
/// </summary>
public class StorageService : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _historyPath;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly Dictionary<string, JsonElement> _settings;
    private bool _disposed = false;

    public StorageService()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RabTrans");
        
        Directory.CreateDirectory(appDataPath);
        _settingsPath = Path.Combine(appDataPath, "settings.json");
        _historyPath = Path.Combine(appDataPath, "history.jsonl");
        _settings = LoadSettings();
    }

    public string AppDataPath => Path.GetDirectoryName(_settingsPath) ?? string.Empty;

    public string SettingsPath => _settingsPath;

    public string HistoryPath => _historyPath;

    public void Reload()
    {
        _settingsLock.Wait();
        try
        {
            _settings.Clear();
            foreach (var item in LoadSettings())
            {
                _settings[item.Key] = item.Value.Clone();
            }
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private Dictionary<string, JsonElement> LoadSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(_settingsPath, Encoding.UTF8));
            return settings != null
                ? new Dictionary<string, JsonElement>(settings, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Gets a value from storage.
    /// </summary>
    public async Task<T?> GetAsync<T>(string key)
    {
        await _settingsLock.WaitAsync();
        try
        {
            if (!_settings.TryGetValue(key, out var value))
            {
                return default;
            }

            return value.Deserialize<T>();
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    /// <summary>
    /// Sets a value in storage.
    /// </summary>
    public async Task SetAsync<T>(string key, T value, bool encrypt = false)
    {
        await _settingsLock.WaitAsync();
        try
        {
            _settings[key] = JsonSerializer.SerializeToElement(value).Clone();
            await SaveSettingsAsync();
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    /// <summary>
    /// Deletes a value from storage.
    /// </summary>
    public async Task DeleteAsync(string key)
    {
        await _settingsLock.WaitAsync();
        try
        {
            if (_settings.Remove(key))
            {
                await SaveSettingsAsync();
            }
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private async Task SaveSettingsAsync()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        var json = JsonSerializer.Serialize(_settings, options);
        await File.WriteAllTextAsync(_settingsPath, json, Encoding.UTF8);
    }

    /// <summary>
    /// Gets translation history.
    /// </summary>
    public async Task<List<TranslationHistoryItem>> GetHistoryAsync(int limit = 100)
    {
        if (!File.Exists(_historyPath))
        {
            return new List<TranslationHistoryItem>();
        }

        var history = new Queue<TranslationHistoryItem>(Math.Max(1, limit));
        using var stream = new FileStream(_historyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var item = JsonSerializer.Deserialize<TranslationHistoryItem>(line);
                if (item != null)
                {
                    history.Enqueue(item);
                    while (history.Count > limit)
                    {
                        history.Dequeue();
                    }
                }
            }
            catch
            {
                // Ignore malformed history lines so one bad record does not hide all history.
            }
        }

        return history
            .OrderByDescending(item => item.Timestamp)
            .Select((item, index) =>
            {
                item.Id = index + 1;
                return item;
            })
            .ToList();
    }

    /// <summary>
    /// Adds a translation to history.
    /// </summary>
    public async Task AddHistoryAsync(TranslationHistoryItem item)
    {
        var json = JsonSerializer.Serialize(item);
        await File.AppendAllTextAsync(_historyPath, json + Environment.NewLine, Encoding.UTF8);
    }

    /// <summary>
    /// Clears translation history.
    /// </summary>
    public async Task ClearHistoryAsync()
    {
        if (File.Exists(_historyPath))
        {
            File.Delete(_historyPath);
        }
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _settingsLock.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Translation history item.
/// </summary>
public class TranslationHistoryItem
{
    public int Id { get; set; }
    public string SourceText { get; set; } = "";
    public string TranslatedText { get; set; } = "";
    public string SourceLang { get; set; } = "";
    public string TargetLang { get; set; } = "";
    public string Provider { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Credential storage using Windows Credential Manager.
/// </summary>
public class CredentialService
{
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credential);

    private const uint CRED_TYPE_GENERIC = 1;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    private const string CredentialPrefix = "RabTrans_";

    /// <summary>
    /// Stores a credential in Windows Credential Manager.
    /// </summary>
    public void SetCredential(string key, string username, string password)
    {
        var targetName = CredentialPrefix + key;
        var passwordBytes = System.Text.Encoding.Unicode.GetBytes(password);
        var passwordPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(passwordBytes.Length);
        
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(passwordBytes, 0, passwordPtr, passwordBytes.Length);

            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetName,
                CredentialBlobSize = (uint)passwordBytes.Length,
                CredentialBlob = passwordPtr,
                Persist = 2, // CRED_PERSIST_LOCAL_MACHINE
                UserName = username
            };

            if (!CredWriteW(ref credential, 0))
            {
                throw new Exception($"Failed to write credential: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(passwordPtr);
        }
    }

    /// <summary>
    /// Reads a credential from Windows Credential Manager.
    /// </summary>
    public (string Username, string Password)? GetCredential(string key)
    {
        var targetName = CredentialPrefix + key;

        if (!CredReadW(targetName, CRED_TYPE_GENERIC, 0, out var credentialPtr))
        {
            return null;
        }

        try
        {
            var credential = System.Runtime.InteropServices.Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            var passwordBytes = new byte[credential.CredentialBlobSize];
            System.Runtime.InteropServices.Marshal.Copy(credential.CredentialBlob, passwordBytes, 0, (int)credential.CredentialBlobSize);
            var password = System.Text.Encoding.Unicode.GetString(passwordBytes);

            return (credential.UserName, password);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    /// <summary>
    /// Deletes a credential from Windows Credential Manager.
    /// </summary>
    public void DeleteCredential(string key)
    {
        var targetName = CredentialPrefix + key;
        CredDeleteW(targetName, CRED_TYPE_GENERIC, 0);
    }
}
