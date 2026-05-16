namespace Deckle.Vision;

/// <summary>
/// One desktop frame surface obtained from the capture service. The
/// underlying ID3D11Texture2D is owned by the capture service for the
/// duration of the <see cref="ScreenCaptureService.FrameArrived"/>
/// handler invocation — consumers must NOT retain the pointer past the
/// handler return. Mirrors the disposal contract that the previous
/// WGC-based pipeline carried via <c>Direct3D11CaptureFrame.Dispose</c>.
///
/// Held as a readonly struct because every field is value-type and the
/// type lives one event invocation only — no GC pressure, no boxing on
/// the FrameArrived hot path.
/// </summary>
public readonly struct CapturedFrame
{
    /// <summary>AddRef'd ID3D11Texture2D pointer for the desktop image
    /// surface. Borrowed for the FrameArrived handler scope only ; do
    /// NOT call <c>Marshal.Release</c> from the consumer side — the
    /// capture service handles that after the handler returns.</summary>
    public nint TexturePtr { get; }

    /// <summary>Width of the captured surface in pixels — matches the
    /// captured monitor's current resolution.</summary>
    public int Width { get; }

    /// <summary>Height of the captured surface in pixels.</summary>
    public int Height { get; }

    /// <summary><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>
    /// recorded by the capture service at the moment of acquisition.
    /// Used for resize-detection logging and downstream cadence
    /// metrics ; not the wall clock.</summary>
    public long TimestampTicks { get; }

    public CapturedFrame(nint texturePtr, int width, int height, long timestampTicks)
    {
        TexturePtr     = texturePtr;
        Width          = width;
        Height         = height;
        TimestampTicks = timestampTicks;
    }
}
