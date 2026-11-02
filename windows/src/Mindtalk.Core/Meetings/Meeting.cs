using System.Text.Json.Serialization;

namespace Mindtalk.Core.Meetings;

/// <summary>Where a meeting's sound came from.</summary>
public enum MeetingSource
{
    /// The microphone, in the room.
    Room,
    /// A call: the microphone plus the call app's sound.
    Call,
    /// A recording you dropped on Meetings.
    Import,
}

/// <summary>The marks you can put on a moment: ⌥⌘1, ⌥⌘2, ⌥⌘3 on the Mac.</summary>
public enum MarkKind { Decision, Todo, Important }

/// <summary>Where a meeting is in its life.</summary>
public enum MeetingState
{
    /// Being recorded now.
    Recording,
    /// Recording, paused.
    Paused,
    /// Ended: the whole recording is listened through again.
    Finishing,
    /// The text is final; the summary is being written.
    Summarizing,
    /// Done.
    Done,
    /// Something went wrong (see <see cref="Meeting.Error"/>); the text that was written is kept.
    Failed,
}

/// <summary>Someone who speaks in a meeting: "Speaker 1" until named.</summary>
public sealed class Speaker
{
    /// Stable within the meeting: "S1", "S2"…
    public string Id { get; set; } = "";
    /// The name you gave (or the voice that was recognized). Null: "Speaker 1".
    public string? Name { get; set; }
    /// Marked as background sound (a TV, a radio): left out of the summary, never named.
    public bool IsBackground { get; set; }
    /// The remembered voice this speaker was matched to, if any.
    public Guid? VoiceId { get; set; }
    /// You (your own name in Settings).
    public bool IsMe { get; set; }

    /// "Anna", or "Speaker 2" / "Talare 2".
    [JsonIgnore]
    public string DisplayName => Name ?? Loc.T($"Talare {Number}", $"Speaker {Number}");

    [JsonIgnore]
    public int Number => int.TryParse(Id.TrimStart('S'), out var n) ? n : 0;
}

/// <summary>A stretch of speech by one speaker, in seconds from the start.</summary>
public sealed record Segment(double Start, double End, string SpeakerId, string Text);

/// <summary>A note you typed during the meeting, pinned to that moment.</summary>
public sealed record MeetingNote(double Time, string Text);

/// <summary>Decision / To do / Important, at a moment.</summary>
public sealed record MeetingMark(double Time, MarkKind Kind);

/// <summary>
/// One line of the summary. <see cref="Time"/> is the moment it comes from (clickable to hear
/// it); <see cref="Quote"/> is the line in the transcript it was checked against.
/// </summary>
public sealed record SummaryItem(string Text, double? Time = null, string? SpeakerId = null, string? Quote = null);

/// <summary>A heading in "What you talked about", with its points.</summary>
public sealed record SummaryTopic(string Title, List<SummaryItem> Items);

/// <summary>Something to do, with who promised it.</summary>
public sealed record TaskItem(string Text, string? Owner = null, double? Time = null, bool Done = false, string? Quote = null);

/// <summary>The summary: an overview, the topics, decisions, to do, and what's worth remembering.</summary>
public sealed class MeetingSummary
{
    public string Overview { get; set; } = "";
    public List<SummaryTopic> Topics { get; set; } = [];
    public List<SummaryItem> Decisions { get; set; } = [];
    public List<TaskItem> Todos { get; set; } = [];
    public List<SummaryItem> Remember { get; set; } = [];
    /// Which model wrote it ("Gemma 4 E4B", "Ollama · llama3.2").
    public string Writer { get; set; } = "";
    public DateTime Written { get; set; }
}

/// <summary>A question asked about meetings, and the answer, with where it comes from.</summary>
public sealed record AskCitation(Guid MeetingId, double Time, string Quote);
public sealed record AskExchange(string Question, string Answer, List<AskCitation> Citations, DateTime Asked);

/// <summary>
/// A meeting: what was said and by whom, your notes and marks, the summary. Kept as
/// meeting.json in its own folder, with the audio beside it.
/// </summary>
public sealed class Meeting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    /// The title was written by Mindtalk (and may be replaced by a better one); false once you rename it.
    public bool TitleIsAuto { get; set; } = true;
    public DateTime Started { get; set; } = DateTime.Now;
    /// Seconds of audio (pauses not counted).
    public double Duration { get; set; }
    public MeetingSource Source { get; set; }
    /// The speech model it was written with ("swedish", "multilingual").
    public string Language { get; set; } = "";
    /// The folder it's sorted into, if any.
    public string? Folder { get; set; }
    public List<Speaker> Speakers { get; set; } = [];
    public List<Segment> Segments { get; set; } = [];
    public List<MeetingNote> Notes { get; set; } = [];
    public List<MeetingMark> Marks { get; set; } = [];
    public MeetingSummary? Summary { get; set; }
    public List<AskExchange> Questions { get; set; } = [];
    /// The audio's file name in the meeting's folder ("audio.wav"); null once it's deleted.
    public string? AudioFile { get; set; }
    /// For an import: the file's original name.
    public string? ImportedFrom { get; set; }
    public MeetingState State { get; set; } = MeetingState.Recording;
    public string? Error { get; set; }
    /// When it was last changed.
    public DateTime Modified { get; set; } = DateTime.Now;

    [JsonIgnore] public DateTime Ended => Started.AddSeconds(Duration);

    public Speaker? SpeakerById(string id) => Speakers.FirstOrDefault(s => s.Id == id);

    /// Speakers who actually say something and aren't background sound, in order of appearance.
    [JsonIgnore]
    public IEnumerable<Speaker> Talkers => Segments.Select(s => s.SpeakerId).Distinct()
        .Select(SpeakerById).OfType<Speaker>().Where(s => !s.IsBackground);

    /// "Anna, Erik and Sara" — or "3 speakers" while nobody is named.
    [JsonIgnore]
    public string PeopleLine
    {
        get
        {
            var talkers = Talkers.ToList();
            var named = talkers.Where(s => s.Name is not null).Select(s => s.Name!).ToList();
            if (named.Count == 0 || named.Count < talkers.Count)
                return talkers.Count == 1 ? Loc.T("1 talare", "1 speaker") : Loc.T($"{talkers.Count} talare", $"{talkers.Count} speakers");
            if (named.Count == 1) return named[0];
            var and = Loc.T(" och ", " and ");
            return string.Join(", ", named.Take(named.Count - 1)) + and + named[^1];
        }
    }

    /// The whole transcript as plain text: "[0:12] Anna: …".
    public string TranscriptText()
    {
        var lines = Segments.Select(s => $"[{Clock(s.Start)}] {SpeakerById(s.SpeakerId)?.DisplayName ?? s.SpeakerId}: {s.Text}");
        return string.Join("\n", lines);
    }

    /// 0:08, 12:34, 1:02:03.
    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// "25 min", "1 h 5 min", "40 s".
    public static string Length(double seconds)
    {
        if (seconds < 60) return Loc.T($"{(int)seconds} s", $"{(int)seconds} sec");
        var minutes = (int)Math.Round(seconds / 60);
        if (minutes < 60) return $"{minutes} min";
        return $"{minutes / 60} h {minutes % 60} min";
    }
}
