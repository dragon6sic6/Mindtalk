using System.Runtime.CompilerServices;
using Mindtalk.Core.AI;
using Mindtalk.Core.Meetings;

namespace Mindtalk.Core.Tests;

/// <summary>A language model that answers from a script, and remembers what it was asked.</summary>
internal sealed class ScriptedModel(params string[] answers) : ILanguageModel, IContextWindow
{
    private int _next;
    public List<IReadOnlyList<ChatMessage>> Asked { get; } = [];
    public string Name => "Scripted";
    public int ContextTokens { get; init; } = 8192;
    public Func<IReadOnlyList<ChatMessage>, string>? Answer { get; init; }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerateOptions options,
                                                      [EnumeratorCancellation] CancellationToken ct = default)
    {
        Asked.Add(messages);
        var text = Answer?.Invoke(messages) ?? answers[Math.Min(_next++, answers.Length - 1)];
        // In pieces, as a real model streams.
        for (var i = 0; i < text.Length; i += 7)
        {
            await Task.Yield();
            yield return text.Substring(i, Math.Min(7, text.Length - i));
        }
    }
}

internal static class TestMeetings
{
    /// The demo's "Weekly meeting about the launch", in English.
    public static Meeting Launch(DateTime? started = null)
    {
        var m = new Meeting
        {
            Title = "Weekly meeting about the launch",
            Started = started ?? new DateTime(2026, 11, 2, 9, 30, 0),
            Duration = 25 * 60,
            State = MeetingState.Done,
            Speakers = [new Speaker { Id = "S1", Name = "Anna" }, new Speaker { Id = "S2", Name = "Erik" }, new Speaker { Id = "S3", Name = "Sara" }],
        };
        (double, double, string, string)[] lines =
        [
            (0, 8, "S1", "Hi everyone, shall we start with the launch?"),
            (153, 160, "S2", "We had more downloads on the first day than in all of last month."),
            (317, 324, "S3", "Most users are in Sweden, but Norway and Denmark are growing."),
            (318, 326, "S2", "Support is already getting questions in English."),
            (634, 642, "S2", "The meetings feature is what people ask about most."),
            (700, 708, "S1", "Then we build the meetings feature next. Is everyone on board?"),
            (709, 711, "S3", "Yes, absolutely."),
            (980, 990, "S3", "The autumn budget needs adjusting before the board meeting."),
            (1100, 1108, "S2", "I'll pull the numbers together and send them to Sara on Thursday."),
        ];
        foreach (var (s, e, who, text) in lines) m.Segments.Add(new Segment(s, e, who, text));
        m.Marks.Add(new MeetingMark(700, MarkKind.Decision));
        m.Notes.Add(new MeetingNote(320, "Check the numbers for Norway"));
        return m;
    }

    public static Meeting Swedish(string title, DateTime started, params (string Who, string Text)[] lines)
    {
        var m = new Meeting { Title = title, Started = started, Duration = 600, State = MeetingState.Done };
        foreach (var who in lines.Select(l => l.Who).Distinct()) m.Speakers.Add(new Speaker { Id = who, Name = who == "S1" ? "Linnéa" : null });
        var t = 0.0;
        foreach (var (who, text) in lines) { m.Segments.Add(new Segment(t, t + 5, who, text)); t += 30; }
        return m;
    }
}
