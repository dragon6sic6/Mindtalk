using System.Runtime.CompilerServices;
using Microsoft.Windows.AI.Text;
using Mindtalk.Core;
using Mindtalk.Core.AI;
using Mindtalk.Core.Dictation;
using Mindtalk.Writers;

namespace Mindtalk.App.AI;

/// <summary>
/// Windows' own on-device model (Phi Silica) as a writer — the Mac's Apple Intelligence. Windows
/// lets only packaged apps on Copilot+ PCs use it, so in this build it's shown greyed out, with why.
/// </summary>
public sealed class WindowsAiWriter : ILanguageModel
{
    private LanguageModel? _model;

    public string Name => "Windows AI";

    /// Available here or not, and why — the same words as "Polish with AI" in Settings.
    public static WindowsAiState State(IPolisher polisher) => polisher.Status switch
    {
        PolishStatus.Available => new(true, true, Loc.T("Inbyggd i Windows, på den här datorn", "Built into Windows, on this PC")),
        PolishStatus.NotReady => new(true, false, Loc.T("Windows AI-modell laddas ned. Den går att välja när det är klart.",
                                                       "Windows’ AI model is downloading. You can choose it when it’s done.")),
        PolishStatus.NotEnabled => new(false, false, Loc.T("Kräver Windows AI-modell, som är avstängd på den här datorn.",
                                                          "Requires Windows’ AI model, which is turned off on this PC.")),
        _ => new(false, false, Loc.T("Kräver Windows AI-modell (Copilot+ PC), som inte finns på den här datorn.",
                                     "Requires Windows’ AI model (Copilot+ PC), which isn’t available on this PC.")),
    };

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerateOptions options,
                                                      [EnumeratorCancellation] CancellationToken ct = default)
    {
        _model ??= await LanguageModel.CreateAsync();
        var system = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
        var prompt = string.Join("\n\n", messages.Where(m => m.Role != "system").Select(m => m.Content));
        var context = _model.CreateContext(system);
        var result = await _model.GenerateResponseAsync(context, prompt, new LanguageModelOptions { Temperature = (float)options.Temperature }).AsTask(ct);
        if (result.Status == LanguageModelResponseStatus.Complete) yield return result.Text;
        else throw new InvalidOperationException(Loc.T("Windows AI kunde inte svara.", "Windows AI couldn't answer."));
    }
}
