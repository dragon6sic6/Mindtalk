using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Text;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Polish;

namespace Mindtalk.App;

/// <summary>
/// "Polish with AI": Windows' own on-device language model (Phi Silica, on Copilot+ PCs) —
/// the counterpart of Apple Intelligence on the Mac. One fresh context per line, warmed
/// while you speak; every answer must pass the same safeguards (LinePolisher, PolishGuard).
/// Nothing leaves the PC. On a PC without the model, the setting says so and stays off.
/// </summary>
public sealed class OnDevicePolisher : IPolisher
{
    private LanguageModel? _model;
    private Task<LanguageModel?>? _loading;

    public PolishStatus Status
    {
        get
        {
            try
            {
                return LanguageModel.GetReadyState() switch
                {
                    AIFeatureReadyState.Ready => PolishStatus.Available,
                    AIFeatureReadyState.NotReady => PolishStatus.NotReady,
                    AIFeatureReadyState.DisabledByUser => PolishStatus.NotEnabled,
                    _ => PolishStatus.Unsupported,
                };
            }
            catch
            {
                // No model on this PC, or Windows won't let this app use it.
                return PolishStatus.Unsupported;
            }
        }
    }

#if DEBUG
    /// Why the model is or isn't available here, for diagnosing (debug builds: --polish-status).
    public static string Diagnose()
    {
        try { return LanguageModel.GetReadyState().ToString(); }
        catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }
#endif

    /// Called when recording starts, so the model is loaded by the time you let go.
    public void Prepare()
    {
        if (_model is not null || _loading is not null || Status != PolishStatus.Available) return;
        _loading = Load();
    }

    private async Task<LanguageModel?> Load()
    {
        try
        {
            _model = await LanguageModel.CreateAsync();
            return _model;
        }
        catch
        {
            return null;
        }
        finally
        {
            _loading = null;
        }
    }

    public async Task<string?> PolishAsync(string text)
    {
        if (Status != PolishStatus.Available) return null;
        var model = _model ?? await (_loading ?? Load());
        if (model is null) return null;
        return await LinePolisher.PolishAsync(text, async (line, ct) =>
        {
            var context = model.CreateContext(LinePolisher.Instructions);
            // Greedy: the same line always gets the same answer.
            var options = new LanguageModelOptions { Temperature = 0, TopK = 1 };
            var result = await model.GenerateResponseAsync(context, line, options).AsTask(ct);
            return result.Status == LanguageModelResponseStatus.Complete ? result.Text : null;
        });
    }
}
