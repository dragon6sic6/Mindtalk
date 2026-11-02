using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using Mindtalk.Core;
using Mindtalk.Core.Meetings;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Mindtalk.App.Meetings;

/// <summary>
/// Share → PDF, Word, Markdown, plain text, the audio (.m4a) and subtitles (.srt), from the
/// meeting's own data. PDF and Word come from the engine's writers (Core), the rest from here.
/// </summary>
internal static class MeetingExport
{
    public enum Kind { Pdf, Word, Markdown, Text, Audio, Subtitles }

    public static async Task SaveAsync(App app, MainWindow main, Meeting meeting, Kind kind)
    {
        var audio = app.Meetings.AudioPath(meeting);
        if (kind == Kind.Audio && audio is null) return;
        var (extension, label) = kind switch
        {
            Kind.Pdf => (".pdf", "PDF"),
            Kind.Word => (".docx", "Word"),
            Kind.Markdown => (".md", "Markdown"),
            Kind.Text => (".txt", Loc.T("Text", "Text")),
            Kind.Subtitles => (".srt", Loc.T("Undertexter", "Subtitles")),
            _ => (".m4a", Loc.T("Ljud", "Audio")),
        };
        var picker = new FileSavePicker { SuggestedFileName = FileName(meeting), SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add(label, [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(main));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            switch (kind)
            {
                case Kind.Pdf: await Task.Run(() => Core.Meetings.MeetingExport.Write(meeting, ExportFormat.Pdf, file.Path)); break;
                case Kind.Word: await Task.Run(() => Core.Meetings.MeetingExport.Write(meeting, ExportFormat.Word, file.Path)); break;
                case Kind.Markdown: await File.WriteAllTextAsync(file.Path, Markdown(meeting, withTranscript: true), new UTF8Encoding(false)); break;
                case Kind.Text: await File.WriteAllTextAsync(file.Path, PlainText(meeting), new UTF8Encoding(false)); break;
                case Kind.Subtitles: await File.WriteAllTextAsync(file.Path, Srt(meeting), new UTF8Encoding(false)); break;
                case Kind.Audio: await SaveAudioAsync(audio!, file); break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            await main.Ask(Loc.T("Det gick inte att spara", "Couldn't save"), ex.Message, "OK", null);
        }
    }

    /// "Weekly meeting about the launch 2026-11-02".
    public static string FileName(Meeting meeting)
    {
        var name = $"{Format.Title(meeting)} {meeting.Started:yyyy-MM-dd}";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        return name.Trim();
    }

    /// The audio as .m4a (AAC) — converted by Windows; if it can't be, the file as it is.
    private static async Task SaveAudioAsync(string source, StorageFile target)
    {
        if (Path.GetExtension(source).Equals(".m4a", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, target.Path, overwrite: true);
            return;
        }
        var input = await StorageFile.GetFileFromPathAsync(source);
        var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateM4a(Windows.Media.MediaProperties.AudioEncodingQuality.Medium);
        var transcoder = new Windows.Media.Transcoding.MediaTranscoder();
        var prepared = await transcoder.PrepareFileTranscodeAsync(input, target, profile);
        if (prepared.CanTranscode) await prepared.TranscodeAsync();
        else File.Copy(source, target.Path, overwrite: true);
    }

    // ---- The document, as lines -------------------------------------------------------

    private enum Style { Title, Meta, Heading, Subheading, Body, Bullet, Speaker }

    /// The meeting as styled lines, shared by every format.
    private static IEnumerable<(Style Style, string Text)> Lines(Meeting meeting, bool withTranscript)
    {
        yield return (Style.Title, Format.Title(meeting));
        var when = meeting.Started.ToString(Loc.IsSwedish ? "dddd d MMMM yyyy, H:mm" : "dddd, MMMM d, yyyy, h:mm tt", Loc.Culture);
        var meta = $"{char.ToUpper(when[0], Loc.Culture)}{when[1..]} · {Meeting.Length(meeting.Duration)}";
        if (meeting.Talkers.Any()) meta += $" · {meeting.PeopleLine}";
        yield return (Style.Meta, meta);
        if (meeting.Summary is { } s)
        {
            if (s.Overview.Length > 0) yield return (Style.Body, s.Overview);
            if (s.Topics.Count > 0)
            {
                yield return (Style.Heading, Loc.T("Det här pratade ni om", "What you talked about"));
                foreach (var topic in s.Topics)
                {
                    yield return (Style.Subheading, topic.Title);
                    foreach (var item in topic.Items) yield return (Style.Bullet, WithTime(item.Text, item.Time));
                }
            }
            if (s.Decisions.Count > 0)
            {
                yield return (Style.Heading, Loc.T("Beslut", "Decisions"));
                foreach (var item in s.Decisions) yield return (Style.Bullet, WithTime(item.Text, item.Time));
            }
            if (s.Todos.Count > 0)
            {
                yield return (Style.Heading, Loc.T("Att göra", "To do"));
                foreach (var task in s.Todos)
                    yield return (Style.Bullet, (task.Done ? "✓ " : "") + WithTime(task.Text + (task.Owner is { } o ? $" — {o}" : ""), task.Time));
            }
            if (s.Remember.Count > 0)
            {
                yield return (Style.Heading, Loc.T("Värt att komma ihåg", "Worth remembering"));
                foreach (var item in s.Remember) yield return (Style.Bullet, WithTime(item.Text, item.Time));
            }
        }
        if (meeting.Notes.Count > 0 || meeting.Marks.Count > 0)
        {
            yield return (Style.Heading, Loc.T("Dina anteckningar och markeringar", "Your notes and marks"));
            foreach (var (time, text) in meeting.Notes.Select(n => (n.Time, n.Text))
                         .Concat(meeting.Marks.Select(m => (m.Time, Parts.MarkTitle(m.Kind)))).OrderBy(x => x.Item1))
                yield return (Style.Bullet, WithTime(text, time));
        }
        if (meeting.Summary is { Writer.Length: > 0 } summary)
            yield return (Style.Meta, Loc.T($"Sammanfattad av {summary.Writer}", $"Summarized by {summary.Writer}"));
        if (!withTranscript || meeting.Segments.Count == 0) yield break;
        yield return (Style.Heading, "Text");
        foreach (var turn in Format.Turns(meeting.Segments))
        {
            var speaker = meeting.SpeakerById(turn.SpeakerId);
            if (speaker?.IsBackground == true) continue;
            yield return (Style.Speaker, $"{speaker?.DisplayName ?? turn.SpeakerId}  {Meeting.Clock(turn.Start)}");
            yield return (Style.Body, turn.Text);
        }
    }

    private static string WithTime(string text, double? time) => time is { } t ? $"{text} ({Meeting.Clock(t)})" : text;

    public static string Markdown(Meeting meeting, bool withTranscript)
    {
        var sb = new StringBuilder();
        foreach (var (style, text) in Lines(meeting, withTranscript))
        {
            switch (style)
            {
                case Style.Title: sb.Append("# ").AppendLine(text).AppendLine(); break;
                case Style.Meta: sb.Append('*').Append(text).AppendLine("*").AppendLine(); break;
                case Style.Heading: sb.AppendLine().Append("## ").AppendLine(text).AppendLine(); break;
                case Style.Subheading: sb.Append("### ").AppendLine(text).AppendLine(); break;
                case Style.Bullet: sb.Append("- ").AppendLine(text); break;
                case Style.Speaker: sb.AppendLine().Append("**").Append(text).AppendLine("**  "); break;
                default: sb.AppendLine(text).AppendLine(); break;
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    public static string PlainText(Meeting meeting)
    {
        var sb = new StringBuilder();
        foreach (var (style, text) in Lines(meeting, withTranscript: true))
        {
            switch (style)
            {
                case Style.Title: sb.AppendLine(text).AppendLine(new string('=', Math.Min(60, text.Length))); break;
                case Style.Heading: sb.AppendLine().AppendLine(text.ToUpper(Loc.Culture)); break;
                case Style.Subheading: sb.AppendLine().AppendLine(text); break;
                case Style.Bullet: sb.Append("  • ").AppendLine(text); break;
                case Style.Speaker: sb.AppendLine().AppendLine(text); break;
                default: sb.AppendLine(text).AppendLine(); break;
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    /// Subtitles: one per segment, with who's speaking.
    public static string Srt(Meeting meeting)
    {
        var sb = new StringBuilder();
        var n = 1;
        foreach (var segment in meeting.Segments.OrderBy(s => s.Start))
        {
            var speaker = meeting.SpeakerById(segment.SpeakerId);
            if (speaker?.IsBackground == true) continue;
            var end = Math.Max(segment.End, segment.Start + 1);
            sb.Append(n++).Append('\n')
              .Append(SrtTime(segment.Start)).Append(" --> ").Append(SrtTime(end)).Append('\n')
              .Append(speaker?.Name is { } name ? $"{name}: " : "").Append(segment.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string SrtTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
    }
}
