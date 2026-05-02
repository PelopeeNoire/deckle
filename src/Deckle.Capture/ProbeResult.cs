namespace Deckle.Capture;

// Categorical decoding of MMSYSERR codes returned by waveInOpen during the
// pre-flight probe. Capture stays free of any localization dependency:
// the caller (WhispEngine, App) maps MicErrorKind to user-visible strings
// via Loc.Get on its side. The raw MMSYSERR value is also returned so the
// caller can include it verbatim in error logs / format strings (the
// "Unavailable_Body_Format" path uses {0} = err).
//
// Mapping (canonical, mirrors the original DescribeMicError table):
//   0 (MMSYSERR_NOERROR)     → None         (probe succeeded)
//   2 (MMSYSERR_BADDEVICEID) → NotDetected
//   6 (MMSYSERR_NODRIVER)    → NotDetected
//   4 (MMSYSERR_ALLOCATED)   → InUse
//   anything else            → Unavailable
public enum MicErrorKind
{
    None,
    NotDetected,
    InUse,
    Unavailable,
}

// Result of MicrophoneCapture.Probe. Two-step Probe + Record so the caller
// can refuse to transition Idle → Starting when the mic is unavailable —
// mirrors the original TryProbeMicrophone semantics. MmsysErr is forwarded
// verbatim from waveInOpen.
public readonly record struct ProbeResult(bool Ok, MicErrorKind Kind, uint MmsysErr);
