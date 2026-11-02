using Mindtalk.Core.Meetings;

/// <summary>
/// A long made-up meeting for testing summaries of meetings that don't fit the model in one go:
/// four people, six topics, a few decisions and promises, and a lot of ordinary talk around them.
/// </summary>
internal static class SyntheticMeeting
{
    private static readonly string[] People = ["Maria", "Jonas", "Leila", "Peter"];

    // Each topic: the lines that matter, in order, with who says them.
    private static readonly (string Topic, (int Who, string Text)[] Key)[] Topics =
    [
        ("the warehouse move", [
            (0, "Let's start with the warehouse move to Västerås."),
            (1, "The lease on the old warehouse ends on the thirty-first of March."),
            (2, "The movers quoted 420,000 kronor for the whole move."),
            (3, "That's more than we budgeted, we had 350,000."),
            (0, "Then we go with the movers anyway, but we split the move over two weekends. Agreed?"),
            (1, "Agreed, two weekends works for the staff."),
            (2, "I'll book the movers for the first two weekends in March."),
        ]),
        ("the new booking system", [
            (1, "The new booking system went live on Monday."),
            (3, "Customers complain that the confirmation emails end up in spam."),
            (2, "About one in five confirmation emails bounced last week."),
            (3, "I'll talk to the email provider about the spam problem this week."),
            (0, "We decided to keep the old phone line open until the emails work."),
        ]),
        ("hiring", [
            (0, "We have two open positions in the warehouse and one in customer service."),
            (2, "Forty-three people applied for the customer service job."),
            (1, "I think we should interview the five best ones next week."),
            (2, "I can set up the interviews for Tuesday and Wednesday."),
            (0, "Let's hire through the agency for the warehouse jobs this time."),
            (3, "Fine by me."),
        ]),
        ("the spring campaign", [
            (3, "The spring campaign starts on the fifteenth of April."),
            (1, "Last year's campaign brought in about 1,200 new customers."),
            (3, "This year we want to focus on families with small children."),
            (0, "Peter, can you send the campaign plan to everyone by Friday?"),
            (3, "Yes, I'll send it by Friday."),
        ]),
        ("the budget", [
            (1, "We are 6 percent over budget for the first quarter."),
            (1, "Most of it is the extra staff we hired in January."),
            (0, "We stop all new consultant contracts until June. That's decided."),
            (2, "What about the IT consultant we already have?"),
            (0, "He stays until the booking system is stable."),
        ]),
        ("the staff party", [
            (2, "The staff party is on the twenty-second of May."),
            (3, "Last year people wanted it outdoors."),
            (2, "I'll look for a place outdoors near the office."),
            (1, "Remember that three people are vegetarian."),
        ]),
    ];

    private static readonly string[] Filler =
    [
        "Yes, I think that makes sense.", "Can we come back to that in a minute?", "I'm not sure everyone has seen the numbers yet.",
        "Right, and that's partly why it took longer than we thought.", "I talked to a few people about {0} yesterday.",
        "It's the same thing we saw last autumn, more or less.", "Do we know how long that would take?",
        "I'd like to hear what the others think about {0}.", "That's a good point, actually.", "Mm, I agree with that.",
        "We should probably write that down somewhere.", "Let me just check my notes on {0}.", "Sorry, could you say that again?",
        "Honestly I don't have a strong opinion on {0}.", "We've been going back and forth on {0} for a while now.",
        "I think the main thing is that people know what's happening.", "It depends a bit on what the others say.",
        "Okay, so where were we with {0}?", "There's also the question of who has time for it.", "Good, let's keep going.",
        "I had a call with them on Thursday, it was fine.", "We don't need to decide everything about {0} today.",
        "My feeling is that most people are positive.", "I'll be honest, it's been a busy month.",
    ];

    public static Meeting Create(int minutes, bool swedish = false)
    {
        var meeting = new Meeting
        {
            Title = "",
            Started = DateTime.Today.AddHours(13),
            Source = MeetingSource.Room,
            Language = "multilingual",
            State = MeetingState.Done,
        };
        for (var i = 0; i < People.Length; i++) meeting.Speakers.Add(new Speaker { Id = $"S{i + 1}", Name = People[i] });

        var random = new Random(7);
        var total = minutes * 60.0;
        var perTopic = total / Topics.Length;
        var t = 2.0;
        for (var topic = 0; topic < Topics.Length; topic++)
        {
            var (name, key) = Topics[topic];
            var end = perTopic * (topic + 1);
            // The lines that matter spread over the topic's time, with ordinary talk between them.
            var slots = key.Length;
            var k = 0;
            while (t < end - 5)
            {
                var due = perTopic * topic + (k + 0.3) * perTopic / (slots + 0.5);
                (int Who, string Text) line;
                if (k < key.Length && t >= due) line = key[k++];
                else line = (random.Next(People.Length), string.Format(Filler[random.Next(Filler.Length)], name));
                var length = 2 + line.Text.Length / 14.0;
                meeting.Segments.Add(new Segment(t, t + length, $"S{line.Who + 1}", line.Text));
                t += length + 0.5 + random.NextDouble() * 1.5;
            }
            while (k < key.Length)
            {
                var line = key[k++];
                var length = 2 + line.Text.Length / 14.0;
                meeting.Segments.Add(new Segment(t, t + length, $"S{line.Who + 1}", line.Text));
                t += length + 1;
            }
        }
        meeting.Duration = t;
        meeting.Marks.Add(new MeetingMark(meeting.Segments.First(s => s.Text.StartsWith("We stop all new consultant", StringComparison.Ordinal)).Start + 2, MarkKind.Decision));
        meeting.Notes.Add(new MeetingNote(meeting.Segments.First(s => s.Text.Contains("1,200", StringComparison.Ordinal)).Start, "Compare with last year's campaign"));
        return meeting;
    }
}
