namespace Mindtalk.Core.Meetings.Engine;

/// <summary>
/// When the whole recording is listened through again, the speakers are found afresh — and
/// the names (and background marks, and remembered voices) carry over from the live speakers
/// by when they spoke: each new speaker gets the live speaker it overlaps the most, one to one.
/// </summary>
public static class SpeakerMapping
{
    /// <summary>New speaker (0…) → the live speaker's id it overlaps the most.</summary>
    /// <param name="minOverlap">Seconds of overlap below which nothing carries over.</param>
    public static Dictionary<int, string> Map(IReadOnlyList<SpeakerTurn> turns, IReadOnlyList<Segment> live, double minOverlap = 0.5)
    {
        var overlap = new Dictionary<(int, string), double>();
        foreach (var t in turns)
        foreach (var s in live)
        {
            var o = Math.Min(t.End, s.End) - Math.Max(t.Start, s.Start);
            if (o <= 0) continue;
            var key = (t.Speaker, s.SpeakerId);
            overlap[key] = overlap.GetValueOrDefault(key) + o;
        }
        // Greedy, largest overlap first: good enough for a handful of speakers.
        var map = new Dictionary<int, string>();
        var taken = new HashSet<string>();
        foreach (var ((speaker, id), seconds) in overlap.OrderByDescending(p => p.Value))
        {
            if (seconds < minOverlap || map.ContainsKey(speaker) || taken.Contains(id)) continue;
            map[speaker] = id;
            taken.Add(id);
        }
        return map;
    }

    /// <summary>
    /// The final speaker list: those that map to a live speaker keep its id and everything you
    /// set on it; the others get the next free numbers, in order of first appearance.
    /// </summary>
    public static (List<Speaker> Speakers, Dictionary<int, string> Ids) Carry(
        IReadOnlyList<SpeakerTurn> turns, IReadOnlyList<Segment> live, IReadOnlyList<Speaker> liveSpeakers)
    {
        var map = Map(turns, live);
        var ids = new Dictionary<int, string>();
        var speakers = new List<Speaker>();
        var used = new HashSet<string>(map.Values);
        var next = 1;
        foreach (var speaker in turns.OrderBy(t => t.Start).Select(t => t.Speaker).Distinct())
        {
            if (map.TryGetValue(speaker, out var id) && liveSpeakers.FirstOrDefault(s => s.Id == id) is { } from)
            {
                ids[speaker] = id;
                speakers.Add(new Speaker { Id = id, Name = from.Name, IsBackground = from.IsBackground, VoiceId = from.VoiceId, IsMe = from.IsMe });
                continue;
            }
            while (used.Contains($"S{next}")) next++;
            var fresh = $"S{next}";
            used.Add(fresh);
            ids[speaker] = fresh;
            speakers.Add(new Speaker { Id = fresh });
        }
        return (speakers.OrderBy(s => s.Number).ToList(), ids);
    }
}
