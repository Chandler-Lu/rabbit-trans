using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RabTrans.Core.Storage;

/// <summary>
/// Storage service using SQLite with DPAPI encryption for sensitive data.
/// </summary>
public class StorageService : IDisposable
{
    private readonly string _databasePath;
    private readonly string _historyPath;
    private SqliteConnection? _connection;
    private bool _disposed = false;

    public StorageService()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RabTrans");
        
        Directory.CreateDirectory(appDataPath);
        _databasePath = Path.Combine(appDataPath, "rabtrans.db");
        _historyPath = Path.Combine(appDataPath, "history.jsonl");
        
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        _connection = new SqliteConnection($"Data Source={_databasePath}");
        _connection.Open();

        // Create tables
        var createTablesCommand = _connection.CreateCommand();
        createTablesCommand.CommandText = @"
            CREATE TABLE IF NOT EXISTS config (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                encrypted INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS plugins (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                enabled INTEGER DEFAULT 1,
                config TEXT
            );
        ";
        createTablesCommand.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets a value from storage.
    /// </summary>
    public async Task<T?> GetAsync<T>(string key)
    {
        var command = _connection!.CreateCommand();
        command.CommandText = "SELECT value, encrypted FROM config WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var value = reader.GetString(0);
            var encrypted = reader.GetInt32(1) == 1;

            if (encrypted)
            {
                var decrypted = DecryptWithDPAPI(Convert.FromBase64String(value));
                return JsonSerializer.Deserialize<T>(decrypted);
            }

            return JsonSerializer.Deserialize<T>(value);
        }

        return default;
    }

    /// <summary>
    /// Sets a value in storage.
    /// </summary>
    public async Task SetAsync<T>(string key, T value, bool encrypt = false)
    {
        var json = JsonSerializer.Serialize(value);
        string storedValue;
        int encrypted = 0;

        if (encrypt)
        {
            var encryptedBytes = EncryptWithDPAPI(Encoding.UTF8.GetBytes(json));
            storedValue = Convert.ToBase64String(encryptedBytes);
            encrypted = 1;
        }
        else
        {
            storedValue = json;
        }

        var command = _connection!.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO config (key, value, encrypted) 
            VALUES (@key, @value, @encrypted)";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", storedValue);
        command.Parameters.AddWithValue("@encrypted", encrypted);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes a value from storage.
    /// </summary>
    public async Task DeleteAsync(string key)
    {
        var command = _connection!.CreateCommand();
        command.CommandText = "DELETE FROM config WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);
        await command.ExecuteNonQueryAsync();
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

        var history = new List<TranslationHistoryItem>();
        var lines = await File.ReadAllLinesAsync(_historyPath, Encoding.UTF8);
        foreach (var line in lines)
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
                    history.Add(item);
                }
            }
            catch
            {
                // Ignore malformed history lines so one bad record does not hide all history.
            }
        }

        return history
            .OrderByDescending(item => item.Timestamp)
            .Take(limit)
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

    /// <summary>
    /// Encrypts data using Windows DPAPI.
    /// </summary>
    private static byte[] EncryptWithDPAPI(byte[] data)
    {
        return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    }

    /// <summary>
    /// Decrypts data using Windows DPAPI.
    /// </summary>
    private static byte[] DecryptWithDPAPI(byte[] encryptedData)
    {
        return ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection?.Close();
            _connection?.Dispose();
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
