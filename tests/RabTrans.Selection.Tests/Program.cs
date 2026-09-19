using RabTrans.Core.Selection;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Alt focus recovery works without a native menu flag", async () =>
    {
        var b = new FakeBackend { RequiresEscape = true, MenuActive = false };
        var result = await new SelectionCaptureService(b).CaptureAsync(0x45, true);
        Check(result.Status == SelectionCaptureStatus.Success && b.Escapes == 1 && b.Copies == 2, "custom browser menu recovered");
        Check(b.EscapeTimes[0] - b.CopyTimes[0] >= 280 && b.CopyTimes[1] - b.EscapeTimes[0] >= 35, "original Alt retry timing");
    }),
    ("Retry has its own complete copy response window", async () =>
    {
        var b = new FakeBackend { PublishOnCopy = false };
        b.OnWait = () => { if (b.Copies == 2 && b.TickCount - b.CopyTimes[1] >= 245 && b.Sequence == 1) b.Publish("browser response"); };
        Check((await new SelectionCaptureService(b).CaptureAsync(0x45, true)).Text == "browser response", "second attempt deadline");
    }),
    ("Temporarily empty clipboard waits for the text format to arrive", async () =>
    {
        var b = new FakeBackend { CopyText = null };
        b.OnWait = () => { if (b.Copies > 0 && b.TickCount - b.CopyTimes[0] >= 70 && b.Text == null) b.Publish("delayed text"); };
        Check((await new SelectionCaptureService(b).CaptureAsync(0x45, true)).Text == "delayed text", "staged clipboard formats");
        Check(b.Escapes == 0 && b.Copies == 1, "do not Escape after clipboard activity");
    }),
    ("Copy read and wake delays match the original compatibility timings", async () =>
    {
        var b = new FakeBackend();
        Check((await new SelectionCaptureService(b).CaptureAsync(0x45, true)).Status == SelectionCaptureStatus.Success, "capture");
        Check(b.ReadTimes[0] - b.CopyTimes[0] >= 35, "do not read in the copy input callback");
        Check(b.TickCount - b.RestoreTimes[^1] >= 80, "settle before activating translation window");
    }),
    ("Copy and activation retain the original settling delays", async () =>
    {
        var b = new FakeBackend { CopyReadyAt = 60 };
        var result = await new SelectionCaptureService(b).CaptureAsync(0, false);
        Check(result.Status == SelectionCaptureStatus.Success && result.Text == "selected", "selection");
        Check(b.CopyTimes[0] == 60 && b.TickCount == 175 && b.Restores == 1 && b.Text == "original", "original timing and restore");
    }),
    ("Quiet interval restarts if shortcut keys are pressed again", async () =>
    {
        var b = new FakeBackend { CopyReadyAt = 105 };
        b.OnWait = () => b.KeysReleased = b.TickCount >= 45;
        Check((await new SelectionCaptureService(b).CaptureAsync(0x54, true)).Status == SelectionCaptureStatus.Success, "key-up queue settled");
        Check(b.Copies == 1 && b.CopyTimes[0] >= 105 && b.CopyTimes[0] <= 130, "continuous 60ms release before copy");
    }),
    ("First ignored Ctrl+C gets one delayed retry", async () =>
    {
        var b = new FakeBackend { IgnoredCopies = 1 };
        Check((await new SelectionCaptureService(b).CaptureAsync(0x54, true)).Status == SelectionCaptureStatus.Success, "retry recovered");
        Check(b.Copies == 2 && b.CopyTimes[1] - b.CopyTimes[0] >= 315, "complete first copy wait and menu settling");
        Check(b.Escapes == 1 && b.Restores == 1, "bounded original Alt recovery");
    }),
    ("No retry while a new modifier is held", async () =>
    {
        var b = new FakeBackend { PublishOnCopy = false };
        b.OnWait = () => { if (b.Copies > 0) b.KeysReleased = false; };
        await new SelectionCaptureService(b).CaptureAsync(0x54, true);
        Check(b.Copies == 1, "do not inject modified Ctrl+C");
    }),
    ("Late response during retry settling is read without a second copy", async () =>
    {
        var b = new FakeBackend { PublishOnCopy = false };
        b.OnWait = () => { if (b.Escapes > 0 && b.Sequence == 1) b.Publish("late response"); };
        var result = await new SelectionCaptureService(b).CaptureAsync(0x45, true);
        Check(result.Text == "late response" && b.Copies == 1, "read late first response");
    }),
    ("Switching windows during retry settling prevents another copy", async () =>
    {
        var b = new FakeBackend { PublishOnCopy = false };
        b.OnWait = () => { if (b.Escapes > 0) b.ForegroundWindow = new IntPtr(2); };
        Check((await new SelectionCaptureService(b).CaptureAsync(0x45, true)).Status == SelectionCaptureStatus.Cancelled, "focus change cancels retry");
        Check(b.Copies == 1 && b.Restores == 0, "no copy to new window");
    }),
    ("Repeated identical text is detected by sequence, not text equality", async () =>
    {
        var b = new FakeBackend { Text = "selected" };
        var service = new SelectionCaptureService(b);
        Check((await service.CaptureAsync(0, false)).Status == SelectionCaptureStatus.Success, "first copy");
        Check((await service.CaptureAsync(0, false)).Status == SelectionCaptureStatus.Success, "repeat copy");
    }),
    ("Clipboard contention after copy retries until readable", async () =>
    {
        var b = new FakeBackend { ReadFailures = 4 };
        var result = await new SelectionCaptureService(b).CaptureAsync(0, false);
        Check(result.Status == SelectionCaptureStatus.Success && b.Reads == 5, "read retries");
        Check(b.Copies == 1 && b.Restores == 1, "one copy/restore");
    }),
    ("Temporarily locked backup retries before injecting input", async () =>
    {
        var b = new FakeBackend { BackupFailures = 3 };
        Check((await new SelectionCaptureService(b).CaptureAsync(0, false)).Status == SelectionCaptureStatus.Success, "capture");
        Check(b.Backups == 4 && b.Copies == 1, "backup retries");
    }),
    ("Unavailable backup never copies or clears clipboard", async () =>
    {
        var b = new FakeBackend { BackupFailures = int.MaxValue };
        Check((await new SelectionCaptureService(b).CaptureAsync(0, false)).Status == SelectionCaptureStatus.ClipboardUnavailable, "status");
        Check(b.Copies == 0 && b.Restores == 0 && b.Text == "original", "untouched clipboard");
    }),
    ("Late first response is still accepted during the Alt retry", async () =>
    {
        var b = new FakeBackend { PublishOnCopy = false };
        b.OnWait = () => { if (b.TickCount >= 405 && b.Sequence == 1) b.Publish("slow selection"); };
        var result = await new SelectionCaptureService(b).CaptureAsync(0x45, true);
        Check(result.Text == "slow selection" && b.TickCount >= 485 && b.TickCount <= 520, "late source response and settling");
    }),
    ("No selection never translates stale clipboard or restores it", async () =>
    {
        var b = new FakeBackend { PublishOnCopy = false };
        Check((await new SelectionCaptureService(b).CaptureAsync(0, false)).Status == SelectionCaptureStatus.Empty, "empty status");
        Check(b.Text == "original" && b.Restores == 0 && b.Escapes == 0 && b.Copies == 1, "non-Alt capture does not dismiss source UI");
    }),
    ("Non-text copy restores prior clipboard without translation", async () =>
    {
        var b = new FakeBackend { CopyText = null };
        Check((await new SelectionCaptureService(b).CaptureAsync(0, false)).Status == SelectionCaptureStatus.Empty, "non-text status");
        Check(b.Restores == 1 && b.Text == "original", "restored non-text copy");
    }),
    ("Long-held shortcut waits for release instead of failing at 500ms", async () =>
    {
        var b = new FakeBackend { KeysReleased = false };
        b.OnWait = () => { if (b.TickCount >= 810) b.KeysReleased = true; };
        Check((await new SelectionCaptureService(b).CaptureAsync(0x54, true)).Status == SelectionCaptureStatus.Success, "held shortcut");
        Check(b.Copies == 1 && b.CopyTimes[0] >= 870 && b.CopyTimes[0] <= 895, "release timing");
    }),
    ("Unreleased shortcut cancels without waking/copying", async () =>
    {
        var b = new FakeBackend { KeysReleased = false };
        Check((await new SelectionCaptureService(b).CaptureAsync(0x54, true)).Status == SelectionCaptureStatus.Cancelled, "timeout status");
        Check(b.TickCount == 1500 && b.Copies == 0, "bounded release wait");
    }),
    ("Changing focus while releasing cancels without input", async () =>
    {
        var b = new FakeBackend { KeysReleased = false };
        b.OnWait = () => b.ForegroundWindow = new IntPtr(2);
        Check((await new SelectionCaptureService(b).CaptureAsync(0, false)).Status == SelectionCaptureStatus.Cancelled, "focus cancel");
        Check(b.Copies == 0 && b.Restores == 0, "no input");
    }),
    ("Changing focus while awaiting copy does not restore stale data", async () =>
    {
        var b = new FakeBackend { PublishOnCopy = false };
        b.OnWait = () => { if (b.Copies > 0) { b.ForegroundWindow = new IntPtr(2); b.Publish("user copy"); } };
        Check((await new SelectionCaptureService(b).CaptureAsync(0, false)).Status == SelectionCaptureStatus.Cancelled, "focus cancel");
        Check(b.Restores == 0 && b.Text == "user copy", "preserve user copy");
    }),
    ("Unrelated clipboard owner is not translated or overwritten", async () =>
    {
        var b = new FakeBackend { OwnsClipboard = false };
        Check((await new SelectionCaptureService(b).CaptureAsync(0, false)).Status == SelectionCaptureStatus.Cancelled, "owner check");
        Check(b.Reads == 0 && b.Restores == 0, "foreign clipboard untouched");
    }),
    ("Rapid repeated hotkeys are coalesced during capture", async () =>
    {
        var b = new FakeBackend { ReadFailures = 1 };
        var service = new SelectionCaptureService(b);
        b.OnWait = () => Check(service.CaptureAsync(0, false).GetAwaiter().GetResult().Status == SelectionCaptureStatus.Busy, "single flight");
        Check((await service.CaptureAsync(0, false)).Status == SelectionCaptureStatus.Success, "first request");
        Check(b.Copies == 1 && b.Restores == 1, "no overlapping copies");
    }),
    ("New user clipboard data during restoration is preserved", async () =>
    {
        var b = new FakeBackend { RestoreFailures = 1 };
        b.OnWait = () => { if (b.Restores > 0) b.Publish("new user copy"); };
        Check((await new SelectionCaptureService(b).CaptureAsync(0, false)).Status == SelectionCaptureStatus.Success, "captured result");
        Check(b.Restores == 1 && b.Text == "new user copy", "new data preserved");
    }),
    ("Restore contention retries without an unconditional clear", async () =>
    {
        var b = new FakeBackend { RestoreFailures = 3 };
        Check((await new SelectionCaptureService(b).CaptureAsync(0, false)).Status == SelectionCaptureStatus.Success, "result");
        Check(b.Restores == 4 && b.Text == "original", "restore retries");
    }),
    ("Alt recovery waits for copy failure; non-Alt never sends Escape", async () =>
    {
        var b = new FakeBackend { MenuActive = true, RequiresEscape = true };
        Check((await new SelectionCaptureService(b).CaptureAsync(0, true)).Status == SelectionCaptureStatus.Success, "menu result");
        Check(b.Escapes == 1 && b.Copies == 2 && b.EscapeTimes[0] >= 340, "dismiss only after failed first copy");
        b = new FakeBackend { PublishOnCopy = false };
        await new SelectionCaptureService(b).CaptureAsync(0, false);
        Check(b.Escapes == 0 && b.Copies == 1, "non-Alt selection");
    }),
    ("Menu appearing after Alt release gets one guarded retry", async () =>
    {
        var b = new FakeBackend { PublishOnCopy = false };
        b.OnWait = () => { if (b.Copies > 0) { b.MenuActive = b.Escapes == 0; b.PublishOnCopy = true; } };
        Check((await new SelectionCaptureService(b).CaptureAsync(0, true)).Status == SelectionCaptureStatus.Success, "late menu");
        Check(b.Escapes == 1 && b.Copies == 2, "bounded retry");
    }),
    ("Blocked input fails immediately and leaves original clipboard", async () =>
    {
        var b = new FakeBackend { InputSucceeds = false };
        Check((await new SelectionCaptureService(b).CaptureAsync(0, false)).Status == SelectionCaptureStatus.InputFailed, "input failed");
        Check(b.TickCount == 60 && b.Restores == 0 && b.Text == "original", "fast failure");
    }),
    ("Foreign copy during Alt recovery cancels before injecting another copy", async () =>
    {
        var b = new FakeBackend { RequiresEscape = true };
        b.OnWait = () => { if (b.Escapes > 0) { b.OwnsClipboard = false; b.Publish("new user copy"); } };
        Check((await new SelectionCaptureService(b).CaptureAsync(0, true)).Status == SelectionCaptureStatus.Cancelled, "clipboard changed");
        Check(b.Copies == 1 && b.Restores == 0 && b.Text == "new user copy", "preserve latest copy");
    }),
    ("Unexpected restore failure also releases capture gate", async () =>
    {
        var b = new FakeBackend { ThrowOnRestore = true };
        var service = new SelectionCaptureService(b);
        try { await service.CaptureAsync(0, false); throw new Exception("expected restore failure"); }
        catch (InvalidOperationException) { }
        b.ThrowOnRestore = false;
        Check((await service.CaptureAsync(0, false)).Status == SelectionCaptureStatus.Success, "gate recovered after restore");
    }),
    ("Unexpected backend failure releases capture gate", async () =>
    {
        var b = new FakeBackend { ThrowOnRead = true };
        var service = new SelectionCaptureService(b);
        try { await service.CaptureAsync(0, false); throw new Exception("expected read failure"); }
        catch (InvalidOperationException) { }
        b.ThrowOnRead = false;
        Check((await service.CaptureAsync(0, false)).Status == SelectionCaptureStatus.Success, "gate recovered");
    })
};

var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class FakeBackend : ISelectionCaptureBackend
{
    public long TickCount { get; private set; }
    public IntPtr ForegroundWindow { get; set; } = new(1);
    public uint Sequence = 1;
    public uint ClipboardSequence => Sequence;
    public string? Text = "original", CopyText = "selected";
    public bool KeysReleased = true, OwnsClipboard = true, PublishOnCopy = true, InputSucceeds = true;
    public bool MenuActive, ThrowOnRead, ThrowOnRestore, RequiresEscape;
    public int BackupFailures, ReadFailures, RestoreFailures, Copies, Escapes, Reads, Restores, Backups;
    public int IgnoredCopies;
    public long CopyReadyAt;
    public List<long> CopyTimes = new();
    public List<long> EscapeTimes = new(), ReadTimes = new(), RestoreTimes = new();
    public Action? OnWait;
    public bool AreKeysReleased(uint triggerKey) => KeysReleased;
    public bool ClipboardBelongsTo(IntPtr window) => OwnsClipboard;
    public void Publish(string? text) { Text = text; Sequence++; }
    public bool SendCopy()
    {
        Copies++;
        CopyTimes.Add(TickCount);
        if (InputSucceeds && PublishOnCopy && TickCount >= CopyReadyAt && Copies > IgnoredCopies && (!RequiresEscape || Escapes > 0)) Publish(CopyText);
        return InputSucceeds;
    }
    public bool SendEscape() { Escapes++; EscapeTimes.Add(TickCount); MenuActive = false; return InputSucceeds; }
    public bool TryBackupClipboard(out object? snapshot)
    {
        Backups++;
        snapshot = null;
        if (BackupFailures-- > 0) return false;
        snapshot = new Backup(Text);
        return true;
    }
    public bool TryReadClipboard(IntPtr target, out string? text, out uint sequence)
    {
        Reads++;
        ReadTimes.Add(TickCount);
        if (ThrowOnRead) throw new InvalidOperationException("Simulated read failure");
        text = Text; sequence = Sequence;
        return ReadFailures-- <= 0 && OwnsClipboard;
    }
    public bool TryRestoreClipboard(object snapshot, uint expectedSequence)
    {
        Restores++;
        RestoreTimes.Add(TickCount);
        if (ThrowOnRestore) throw new InvalidOperationException("Simulated restore failure");
        if (RestoreFailures-- > 0) return false;
        if (Sequence == expectedSequence) Publish(((Backup)snapshot).Text);
        return true;
    }
    public Task WaitAsync(int milliseconds)
    {
        TickCount += milliseconds;
        OnWait?.Invoke();
        return Task.CompletedTask;
    }
    private sealed record Backup(string? Text);
}
