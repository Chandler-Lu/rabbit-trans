using System.Text.Json;
using RabTrans.Core.Storage;

namespace RabTrans.Core.Settings;

/// <summary>Serializes edits, applies runtime effects, and commits only successful changes.</summary>
public sealed class SettingsAutoSaveSession
{
    private readonly StorageService _storage;
    private readonly Func<IReadOnlyDictionary<string, JsonElement>, Task> _apply;
    private readonly Dictionary<string, JsonElement> _saved;
    private readonly Dictionary<string, (long Revision, JsonElement Value)> _pending = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _revision;
    private Dictionary<string, JsonElement>? _restorePending;
    public event Action? Changed;
    public bool IsSaving { get; private set; }
    public bool HasPending => _pending.Count > 0 || _restorePending != null;
    public string? Error { get; private set; }
    public IReadOnlyDictionary<string, JsonElement> Saved => _saved;

    public SettingsAutoSaveSession(StorageService storage, IReadOnlyDictionary<string, JsonElement> initial,
        Func<IReadOnlyDictionary<string, JsonElement>, Task> apply)
    {
        _storage = storage;
        _saved = initial.ToDictionary(p => p.Key, p => p.Value.Clone());
        _apply = apply;
    }

    public void Queue(IReadOnlyDictionary<string, JsonElement> changes)
    {
        foreach (var (key, value) in changes)
        {
            // While a write is in progress, a reversal is still a new edit.
            if (!IsSaving && _saved.TryGetValue(key, out var old) && JsonElement.DeepEquals(old, value))
                _pending.Remove(key);
            else
                _pending[key] = (++_revision, value.Clone());
        }
        Error = null;
        Changed?.Invoke();
    }

    public void RemovePending(params string[] keys)
    {
        foreach (var key in keys) _pending.Remove(key);
        if (!HasPending) Error = null;
        Changed?.Invoke();
    }

    public async Task<bool> FlushAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!await RestoreRuntimeAsync()) return false;
            while (_pending.Count > 0)
            {
                var batch = _pending.ToDictionary();
                var changes = batch.ToDictionary(p => p.Key, p => p.Value.Value);
                // Migrate the tray preference on the first successful edit, never just by opening settings.
                if (_saved.ContainsKey("minimize_to_tray") && !changes.ContainsKey("close_to_tray"))
                    changes["close_to_tray"] = _saved.GetValueOrDefault("close_to_tray", _saved["minimize_to_tray"]);
                var previous = changes.Keys.Where(_saved.ContainsKey).ToDictionary(k => k, k => _saved[k]);
                IsSaving = true;
                Error = null;
                Changed?.Invoke();
                try
                {
                    await _apply(changes);
                    await _storage.UpdateAsync(changes, changes.ContainsKey("close_to_tray") ? new[] { "minimize_to_tray" } : null);
                    foreach (var (key, value) in changes) _saved[key] = value.Clone();
                    if (changes.ContainsKey("close_to_tray")) _saved.Remove("minimize_to_tray");
                    foreach (var (key, item) in batch)
                        if (_pending.TryGetValue(key, out var latest) && latest.Revision == item.Revision) _pending.Remove(key);
                }
                catch (Exception error)
                {
                    _restorePending = previous;
                    await RestoreRuntimeAsync();
                    Error ??= error is InvalidOperationException ? error.Message : "Could not save settings. Check file access and retry.";
                    return false;
                }
                finally
                {
                    IsSaving = false;
                    Changed?.Invoke();
                }
            }
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> RestoreRuntimeAsync()
    {
        if (_restorePending == null) return true;
        try { await _apply(_restorePending); _restorePending = null; return true; }
        catch { Error = "Could not restore the previous runtime settings. Retry before closing."; return false; }
    }

    public async Task<bool> DiscardPendingAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!await RestoreRuntimeAsync()) { Changed?.Invoke(); return false; }
            _pending.Clear(); Error = null; Changed?.Invoke(); return true;
        }
        finally { _gate.Release(); }
    }
}
