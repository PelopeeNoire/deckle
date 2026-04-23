using System;
using System.Collections.ObjectModel;

namespace HudPlayground;

// Minimalist in-process log surface for the playground. One
// ObservableCollection bound to MainWindow's ListView, one `Write`
// entry point for callers, a hard cap so long sessions don't leak.
//
// Design deltas from LogWindow (src/WhispUI/LogWindow.xaml.cs):
//   - No search, no filter, no selection, no copy, no file sink,
//     no theme dictionary, no custom selector.
//   - Single flat category string (TUNE, REBUILD, STATE, RMS, …) —
//     the full vocabulary lives in the callers, no enum here.
//   - No marshalling. All callers in the playground run on the UI
//     thread (slider ValueChanged, DispatcherTimer Tick, Window
//     Closed). If a future caller ever logs from a background
//     thread, it'll surface as an ObservableCollection thread
//     exception and we'll revisit.
public sealed record DevLogEntry(DateTime Timestamp, string Category, string Message)
{
    // Precomputed display string for the ListView timestamp column. We
    // format once at construction rather than on every render pass —
    // `x:Bind` reads this property directly and never re-queries as
    // long as the entry is immutable (records compare by value, new
    // instance on each Write). Format `HH:mm:ss.fff` mirrors what the
    // shipping `LogWindow` uses so the visual cadence is familiar.
    public string TimestampText => Timestamp.ToString("HH:mm:ss.fff");
}

public static class DevLog
{
    // Cap the ring so a long tuning session doesn't grow the collection
    // without bound. 2000 entries at an average ~80 chars ≈ 160 KB —
    // well under any concern. LogWindow caps at 5000 for the same
    // reason; we're half of that because the playground's event rate
    // (slider tweaks, rebuilds) is lower than the engine's.
    public const int MaxEntries = 2000;

    // Exposed as an `ObservableCollection` so MainWindow can bind it
    // directly to a ListView without an intermediate ViewModel. The
    // collection is mutated on the UI thread only.
    public static ObservableCollection<DevLogEntry> Entries { get; } = new();

    // Fires after each new entry is appended. MainWindow subscribes to
    // auto-scroll the ListView to the tail — without this, the
    // ListView stays pinned wherever Louis last scrolled. Kept
    // separate from the collection's CollectionChanged so we can
    // invoke it only on Add (skipping the cap-driven RemoveAt in the
    // loop below).
    public static event Action? EntryAppended;

    public static void Write(string category, string message)
    {
        Entries.Add(new DevLogEntry(DateTime.Now, category, message));
        while (Entries.Count > MaxEntries)
            Entries.RemoveAt(0);
        EntryAppended?.Invoke();
    }
}
