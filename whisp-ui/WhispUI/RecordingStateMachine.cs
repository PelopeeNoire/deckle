using System.Diagnostics;

namespace WhispUI;

internal enum RecordingState { Idle, Recording }

internal sealed class RecordingStateMachine
{
    public RecordingState State { get; private set; } = RecordingState.Idle;
    public Stopwatch Stopwatch { get; } = new();

    // Déclenché à chaque transition, avec le nouvel état.
    public event Action<RecordingState>? StateChanged;

    public void StartRecording()
    {
        if (State == RecordingState.Recording) return;
        Stopwatch.Restart();
        State = RecordingState.Recording;
        StateChanged?.Invoke(State);
    }

    public void StopRecording()
    {
        if (State == RecordingState.Idle) return;
        Stopwatch.Stop();
        State = RecordingState.Idle;
        StateChanged?.Invoke(State);
    }
}
