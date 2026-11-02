using Mindtalk.Core;
using Mindtalk.Core.Meetings;

namespace Mindtalk.App.Meetings;

/// <summary>How the meeting pages write days and times: "Today 9:30 AM", "I dag 9:30".</summary>
internal static class Format
{
    /// "9:30 AM" / "9:30".
    public static string Clock(DateTime time) => Loc.IsSwedish
        ? time.ToString("H:mm", Loc.Culture)
        : time.ToString("h:mm tt", Loc.Culture);

    /// "Today", "Yesterday", "Monday", "26 October", "26 October 2025".
    public static string Day(DateTime day, DateTime now)
    {
        var days = (now.Date - day.Date).Days;
        if (days == 0) return Loc.T("I dag", "Today");
        if (days == 1) return Loc.T("I går", "Yesterday");
        if (days is > 1 and < 7) return Capital(day.ToString("dddd", Loc.Culture));
        var format = day.Year == now.Year ? (Loc.IsSwedish ? "d MMMM" : "MMMM d") : (Loc.IsSwedish ? "d MMMM yyyy" : "MMMM d, yyyy");
        return Capital(day.ToString(format, Loc.Culture));
    }

    /// "Today 9:30 AM".
    public static string When(DateTime time, DateTime now) => $"{Day(time, now)} {Clock(time)}";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Loc.Culture) + text[1..];

    /// The title, or "Meeting 9:30" before one is written.
    public static string Title(Meeting meeting) => string.IsNullOrWhiteSpace(meeting.Title)
        ? (meeting.Source == MeetingSource.Import && meeting.ImportedFrom is { } file ? Path.GetFileNameWithoutExtension(file)
           : Loc.T($"Möte {Clock(meeting.Started)}", $"Meeting {Clock(meeting.Started)}"))
        : meeting.Title;

    /// The first lines of the summary, for the list.
    public static string Gist(Meeting meeting)
    {
        if (meeting.Summary is { Overview.Length: > 0 } s) return s.Overview;
        if (meeting.Error is { } error) return error;
        return string.Join(" ", meeting.Segments.Take(3).Select(x => x.Text));
    }

    /// "Listens in Swedish" / "Listens in several languages".
    public static string Language(string modelId) => modelId == "swedish"
        ? Loc.T("svenska", "Swedish")
        : Loc.T("flera språk", "several languages");

    /// One speaker turn: consecutive segments by the same person, joined.
    public sealed record Turn(string SpeakerId, double Start, double End, List<Segment> Segments)
    {
        public string Text => string.Join(" ", Segments.Select(s => s.Text));
    }

    public static List<Turn> Turns(IEnumerable<Segment> segments, double gap = 30)
    {
        var turns = new List<Turn>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            if (turns.Count > 0 && turns[^1].SpeakerId == segment.SpeakerId && segment.Start - turns[^1].End < gap)
            {
                var last = turns[^1];
                last.Segments.Add(segment);
                turns[^1] = last with { End = Math.Max(last.End, segment.End) };
            }
            else turns.Add(new Turn(segment.SpeakerId, segment.Start, segment.End, [segment]));
        }
        return turns;
    }
}
