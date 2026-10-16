#if DEBUG
using Mindtalk.Core.Dictation;

namespace Mindtalk.App;

/// <summary>
/// Debug only: the app mid-dictation for screenshots, as the Mac's --demo flags.
/// `--demo-hud` shows the pill speaking; add `--demo-locked`, `--demo-transcribing`,
/// `--demo-language`, `--demo-failed` or `--demo-copied`.
/// </summary>
internal static class Demo
{
    private static readonly string[] Args = Environment.GetCommandLineArgs();

    /// A speaking waveform (the Mac's demo levels).
    private static readonly float[] Voice =
    [
        0.05f, 0.12f, 0.30f, 0.52f, 0.41f, 0.66f, 0.35f, 0.22f, 0.48f, 0.71f, 0.58f, 0.33f,
        0.18f, 0.40f, 0.62f, 0.50f, 0.28f, 0.45f, 0.69f, 0.54f, 0.31f, 0.47f, 0.60f, 0.38f,
    ];

    public static bool Has(string flag) => Args.Contains(flag);

    public static bool Hud(HudWindow hud)
    {
        if (!Has("--demo-hud")) return false;
        var phase = Has("--demo-transcribing") ? Phase.Transcribing
                  : Has("--demo-failed") ? Phase.Failed
                  : Has("--demo-copied") ? Phase.Copied
                  : Phase.Recording;
        var state = new DictationState(phase, Has("--demo-locked"), true,
            Has("--demo-failed") ? "Du bytte app – tryck Ctrl+Alt+V för att klistra in texten." : null,
            Has("--demo-language"), Has("--demo-multilingual") ? "multilingual" : "swedish");
        hud.Demo(state, n => Voice[^n..]);
        return true;
    }
}
#endif
