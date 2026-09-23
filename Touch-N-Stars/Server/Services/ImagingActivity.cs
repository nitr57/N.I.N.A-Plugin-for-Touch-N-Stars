using NINA.Core.Utility;
using System;

namespace TouchNStars.Server.Services;

/// <summary>
/// Whether NINA is imaging, for features that would otherwise take the camera and focuser over mid-way
/// (an Aberration Inspector live run, an optimizer live sweep). Neither checks this itself: on the desktop the
/// user starts them deliberately, but from TNS they are one tap away while a sequence runs.
/// </summary>
public static class ImagingActivity
{
    public const string SequenceRunningMessage =
        "A sequence is running. This would move the focuser and take its own exposures in the middle of it: stop the sequence first. Replaying a saved auto-focus run is fine meanwhile.";

    /// <summary>True while the advanced sequencer is running.</summary>
    public static bool IsSequenceRunning()
    {
        try
        {
            return TouchNStars.Mediators?.Sequence?.IsAdvancedSequenceRunning() == true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not read the sequencer state: {ex.Message}");
            return false;
        }
    }
}
