#if DEBUG
using System.Text.Json;
using Mindtalk.Core;
using Mindtalk.Core.Meetings;
using Mindtalk.Core.Thoughts;

namespace Mindtalk.App;

/// <summary>Demo meetings and thoughts — the ones in the Mac's screenshots.</summary>
internal static partial class Demo
{
    private static string L(string sv, string en) => Loc.T(sv, en);

    private static void SeedThoughts(string folder, DateTime now)
    {
        var today = now.Date;
        Thought[] thoughts =
        [
            new(Guid.NewGuid(), L("Skicka materialet till styrelsen före fredag.", "Send the material to the board before Friday."), today.AddHours(11).AddMinutes(5), Pinned: true),
            new(Guid.NewGuid(), L("Ring tillbaka om offerten och kolla Q4-budgeten.", "Call back about the quote and check the Q4 budget."), today.AddHours(10).AddMinutes(12)),
            new(Guid.NewGuid(), L("Boka ett rum för kickoffen i november.", "Book a room for the kickoff in November."), today.AddHours(8).AddMinutes(40)),
            new(Guid.NewGuid(), L("Idé: visa vem som pratar mest i ett möte.", "Idea: show who talks the most in a meeting."), today.AddDays(-1).AddHours(16).AddMinutes(25)),
        ];
        System.IO.File.WriteAllText(Path.Combine(folder, "thoughts.json"),
            JsonSerializer.Serialize(thoughts, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
    }

    private static void SeedMeetings(string folder, DateTime now)
    {
        var root = Path.Combine(folder, "Meetings");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var store = new MeetingStore(root);
        var today = now.Date;

        // ---- Weekly meeting about the launch ------------------------------------------
        var launch = Meet(L("Veckomöte om lanseringen", "Weekly meeting about the launch"), today.AddHours(9).AddMinutes(30), 25 * 60,
            ["Anna", "Erik", "Sara"],
            [
                (0, 8, 0, L("Hej allihop, ska vi börja med lanseringen?", "Hi everyone, shall we start with the launch?")),
                (153, 160, 1, L("Vi fick fler nedladdningar första dagen än under hela förra månaden.", "We had more downloads on the first day than in all of last month.")),
                (317, 324, 2, L("De flesta användarna finns i Sverige, men Norge och Danmark växer.", "Most users are in Sweden, but Norway and Denmark are growing.")),
                (318, 326, 1, L("Supporten får redan frågor på engelska.", "Support is already getting questions in English.")),
                (634, 642, 1, L("Mötesfunktionen är det folk frågar mest om.", "The meetings feature is what people ask about most.")),
                (700, 708, 0, L("Då bygger vi mötesfunktionen härnäst. Är alla med på det?", "Then we build the meetings feature next. Is everyone on board?")),
                (709, 711, 2, L("Ja, absolut.", "Yes, absolutely.")),
                (980, 990, 2, L("Höstbudgeten behöver justeras före styrelsemötet.", "The autumn budget needs adjusting before the board meeting.")),
                (1100, 1108, 1, L("Jag tar fram siffrorna och skickar dem till Sara på torsdag.", "I'll pull the numbers together and send them to Sara on Thursday.")),
            ]);
        launch.Summary = new MeetingSummary
        {
            Overview = L("Lanseringen gick bättre än väntat, med flest användare i Sverige och växande intresse i Norge och Danmark. Teamet kom överens om att bygga mötesfunktionen härnäst, och höstbudgeten justeras före styrelsemötet.",
                         "The launch went better than expected, with most users in Sweden and growing interest in Norway and Denmark. The team agreed to build the meetings feature next, and the autumn budget is adjusted before the board meeting."),
            Topics =
            [
                new(L("Lanseringen", "The launch"),
                [
                    new(L("Erik: fler nedladdningar första dagen än under hela förra månaden.", "Erik: more downloads on the first day than in all of last month."), 153, "S2"),
                    new(L("De flesta användarna finns i Sverige, men Norge och Danmark växer.", "Most users are in Sweden, but Norway and Denmark are growing."), 317, "S3"),
                    new(L("Supporten får redan frågor på engelska.", "Support is already getting questions in English."), 318, "S2"),
                ]),
                new(L("Vad som kommer härnäst", "What comes next"),
                [
                    new(L("Erik: mötesfunktionen är det folk frågar mest om.", "Erik: the meetings feature is what people ask about most."), 634, "S2"),
                ]),
            ],
            Decisions = [new(L("Mötesfunktionen byggs härnäst.", "The meetings feature is built next."), 700, "S1")],
            Todos = [new(L("Ta fram siffrorna till höstbudgeten och skicka till Sara.", "Pull together the numbers for the autumn budget and send them to Sara."), "Erik", 1100)],
            Remember = [new(L("Höstbudgeten justeras före styrelsemötet.", "The autumn budget is adjusted before the board meeting."), 980, "S3")],
            Writer = "Gemma 4 E4B",
            Written = launch.Ended,
        };
        launch.Marks.Add(new MeetingMark(700, MarkKind.Decision));
        launch.Notes.Add(new MeetingNote(320, L("Kolla siffrorna för Norge", "Check the numbers for Norway")));
        store.Save(launch);

        // ---- Customer meeting, kick-off -------------------------------------------------
        var customer = Meet(L("Kundmöte, uppstart", "Customer meeting, kick-off"), today.AddDays(-1).AddHours(14), 52 * 60, [null, null],
            [
                (12, 20, 0, L("Vi vill gärna köra en pilot i januari.", "We'd like to run a pilot in January.")),
                (40, 48, 1, L("Det går bra. Jag skickar ett förslag nästa vecka.", "That works. I'll send a proposal next week.")),
            ]);
        customer.Folder = L("Kunder", "Customers");
        customer.Summary = new MeetingSummary
        {
            Overview = L("Kunden vill ha en pilot i januari. Ett förslag skickas nästa vecka.", "The customer wants a pilot in January. A proposal goes out next week."),
            Todos = [new(L("Skicka förslaget", "Send the proposal"), null, 40)],
            Writer = "Gemma 4 E4B",
            Written = customer.Ended,
        };
        store.Save(customer);

        // ---- Autumn budget --------------------------------------------------------------
        var budget = Meet(L("Höstbudget", "Autumn budget"), today.AddDays(-7).AddHours(10), 41 * 60, [null, null],
            [
                (30, 38, 0, L("Budgeten håller, förutom marknadsföringen.", "The budget holds, apart from marketing.")),
                (60, 66, 1, L("Den tar vi på veckomötet.", "We'll decide that at the weekly meeting.")),
            ]);
        budget.Summary = new MeetingSummary
        {
            Overview = L("Höstbudgeten håller, förutom marknadsföringen, som avgörs på veckomötet.", "The autumn budget holds, apart from marketing, which is decided at the weekly meeting."),
            Writer = "Gemma 4 E4B",
            Written = budget.Ended,
        };
        store.Save(budget);

        var oneOnOne = Meet(L("Avstämning med Linnéa", "Check-in with Linnéa"), today.AddDays(-9).AddHours(15), 18 * 60, ["Linnéa", null],
            [(5, 12, 0, L("Hur går det med rekryteringen?", "How is the hiring going?"))]);
        oneOnOne.Summary = new MeetingSummary { Overview = L("Rekryteringen går enligt plan.", "Hiring is on track."), Writer = "Gemma 4 E4B", Written = oneOnOne.Ended };
        store.Save(oneOnOne);
    }

    private static Meeting Meet(string title, DateTime started, double seconds, string?[] names, (double Start, double End, int Speaker, string Text)[] lines)
    {
        var meeting = new Meeting
        {
            Title = title,
            TitleIsAuto = true,
            Started = started,
            Duration = seconds,
            Source = MeetingSource.Room,
            Language = "swedish",
            State = MeetingState.Done,
        };
        for (var i = 0; i < names.Length; i++) meeting.Speakers.Add(new Speaker { Id = $"S{i + 1}", Name = names[i] });
        foreach (var (start, end, speaker, text) in lines) meeting.Segments.Add(new Segment(start, end, $"S{speaker + 1}", text));
        return meeting;
    }
}
#endif
