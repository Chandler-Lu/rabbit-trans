namespace RabTrans.Core.Selection;

public enum SelectionCaptureStatus { Success, Empty, Cancelled, Busy, ClipboardUnavailable, InputFailed }

public readonly record struct SelectionCaptureResult(SelectionCaptureStatus Status, string? Text = null);

/// <summary>Serializes selection copying without holding the gate during translation/network I/O.</summary>
public sealed class SelectionCaptureService(ISelectionCaptureBackend backend)
{
    private const int KeyReleasePollMs = 25;
    private const int KeyReleaseSettleMs = 60;
    private const int CopyPollMs = 35;
    private const int CopyPollCount = 8;
    private const int MenuSettleMs = 35;
    private const int BeforeActivationSettleMs = 80;
    private int _capturing;

    public async Task<SelectionCaptureResult> CaptureAsync(uint triggerKey, bool usesAlt)
    {
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
            return new(SelectionCaptureStatus.Busy);

        object? snapshot = null;
        uint? copiedSequence = null;
        var copySent = false;
        try
        {
            var target = backend.ForegroundWindow;
            if (target == IntPtr.Zero ||
                !await WaitForSettledKeyReleaseAsync(target, triggerKey, backend.TickCount + 1500))
                return new(SelectionCaptureStatus.Cancelled);

            // Materialize the old clipboard before copying: an OLE proxy alone is not a backup.
            var deadline = backend.TickCount + 120;
            while (!backend.TryBackupClipboard(out snapshot))
            {
                if (backend.ForegroundWindow != target)
                    return new(SelectionCaptureStatus.Cancelled);
                if (backend.TickCount >= deadline)
                    return new(SelectionCaptureStatus.ClipboardUnavailable);
                await backend.WaitAsync(15);
            }

            var previousSequence = backend.ClipboardSequence;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (backend.ForegroundWindow != target || !backend.AreKeysReleased(triggerKey))
                    return new(SelectionCaptureStatus.Cancelled);

                if (attempt > 0 && backend.ClipboardSequence == previousSequence)
                {
                    // Restore the original Alt recovery. Browser menu/toolbar focus is not
                    // necessarily exposed by GetGUIThreadInfo's native menu flags.
                    // Only run after a full unsuccessful copy attempt, with no clipboard activity.
                    if (!backend.SendEscape())
                        return new(SelectionCaptureStatus.InputFailed);
                    await backend.WaitAsync(MenuSettleMs);
                }

                if (backend.ForegroundWindow != target || !backend.AreKeysReleased(triggerKey))
                    return new(SelectionCaptureStatus.Cancelled);

                // A late reply to the first copy may arrive while Escape is settling.
                // Read it instead of overwriting it with another Ctrl+C.
                if (backend.ClipboardSequence == previousSequence)
                {
                    if (!backend.SendCopy())
                        return new(SelectionCaptureStatus.InputFailed);
                    copySent = true;
                }

                // Each attempt receives the original full 8 x 35 ms response window.
                // Do not wake on an early clipboard notification while formats are being published.
                var read = await ReadCopiedTextAsync(target, previousSequence);
                copiedSequence = read.Sequence;
                if (read.Status != SelectionCaptureStatus.Empty || read.ClipboardChanged || !usesAlt)
                    return new(read.Status, read.Text);
            }
            return new(SelectionCaptureStatus.Empty);
        }
        finally
        {
            try
            {
                // Never restore on a failed backup/no copy, or overwrite a later user copy.
                if (snapshot != null && copiedSequence is uint sequence)
                {
                    var deadline = backend.TickCount + 120;
                    while (backend.ClipboardSequence == sequence &&
                           !backend.TryRestoreClipboard(snapshot, sequence) && backend.TickCount < deadline)
                        await backend.WaitAsync(15);
                }
            }
            finally
            {
                try
                {
                    // Preserve the original pause before the translation window takes focus.
                    if (copySent)
                        await backend.WaitAsync(BeforeActivationSettleMs);
                }
                finally
                {
                    Volatile.Write(ref _capturing, 0);
                }
            }
        }
    }

    private async Task<CopyReadResult> ReadCopiedTextAsync(IntPtr target, uint previousSequence)
    {
        var changed = false;
        uint? lastReadSequence = null;
        for (var poll = 0; poll < CopyPollCount; poll++)
        {
            await backend.WaitAsync(CopyPollMs);
            if (backend.ForegroundWindow != target)
                return new(SelectionCaptureStatus.Cancelled);
            if (backend.ClipboardSequence == previousSequence)
                continue;
            changed = true;
            if (!backend.ClipboardBelongsTo(target))
                return new(SelectionCaptureStatus.Cancelled);
            if (backend.TryReadClipboard(target, out var text, out var sequence))
            {
                lastReadSequence = sequence;
                if (!string.IsNullOrWhiteSpace(text))
                    return new(SelectionCaptureStatus.Success, text, sequence, true);
                // Empty/non-text clipboard updates can precede a browser's text format.
                // Wait out this attempt before deciding there is no selected text.
            }
        }
        return new(SelectionCaptureStatus.Empty, Sequence: lastReadSequence, ClipboardChanged: changed);
    }

    private async Task<bool> WaitForSettledKeyReleaseAsync(IntPtr target, uint triggerKey, long deadline)
    {
        long? releasedAt = null;
        while (backend.TickCount < deadline)
        {
            if (backend.ForegroundWindow != target)
                return false;
            var delay = KeyReleasePollMs;
            if (backend.AreKeysReleased(triggerKey))
            {
                releasedAt ??= backend.TickCount;
                var remaining = KeyReleaseSettleMs - (backend.TickCount - releasedAt.Value);
                if (remaining <= 0)
                    return true;
                delay = (int)Math.Min(delay, remaining);
            }
            else
            {
                releasedAt = null;
            }
            await backend.WaitAsync(delay);
        }
        return false;
    }

    private readonly record struct CopyReadResult(SelectionCaptureStatus Status, string? Text = null,
        uint? Sequence = null, bool ClipboardChanged = false);
}

/// <summary>STA clipboard/input operations; separated so timing and focus races can be tested.</summary>
public interface ISelectionCaptureBackend
{
    long TickCount { get; }
    IntPtr ForegroundWindow { get; }
    uint ClipboardSequence { get; }
    bool AreKeysReleased(uint triggerKey);
    bool ClipboardBelongsTo(IntPtr window);
    bool SendCopy();
    bool SendEscape();
    bool TryBackupClipboard(out object? snapshot);
    bool TryReadClipboard(IntPtr target, out string? text, out uint sequence);
    bool TryRestoreClipboard(object snapshot, uint expectedSequence);
    Task WaitAsync(int milliseconds);
}
