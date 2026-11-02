using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Mindtalk.Core.Meetings.Engine;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Mindtalk.Core.Meetings;

/// <summary>The formats a meeting can be shared as (the Mac's Share and export).</summary>
public enum ExportFormat { Pdf, Word, Markdown, Text, Subtitles, Audio }

/// <summary>
/// A meeting as PDF, Word (.docx), Markdown, plain text, subtitles (.srt) or its audio (.m4a):
/// the title; date, time, length and people; the summary (overview, what you talked about,
/// decisions, to do with owners, worth remembering); your notes; then what was said, with
/// speakers and times. Headings in the app's language.
/// </summary>
public static class MeetingExport
{
    /// ".pdf", ".docx"…
    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Pdf => ".pdf",
        ExportFormat.Word => ".docx",
        ExportFormat.Markdown => ".md",
        ExportFormat.Text => ".txt",
        ExportFormat.Subtitles => ".srt",
        _ => ".m4a",
    };

    /// "PDF", "Word (.docx)"… for a menu.
    public static string Title(ExportFormat format) => format switch
    {
        ExportFormat.Pdf => "PDF",
        ExportFormat.Word => "Word (.docx)",
        ExportFormat.Markdown => "Markdown",
        ExportFormat.Text => Loc.T("Ren text", "Plain text"),
        ExportFormat.Subtitles => Loc.T("Undertexter (.srt)", "Subtitles (.srt)"),
        _ => Loc.T("Ljud (.m4a)", "Audio (.m4a)"),
    };

    /// A file name from the title, without characters Windows doesn't allow.
    public static string FileName(Meeting meeting, ExportFormat format)
    {
        var name = string.Concat((meeting.Title.Length > 0 ? meeting.Title : Loc.T("Möte", "Meeting"))
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim().TrimEnd('.');
        return (name.Length > 0 ? name : "Meeting") + Extension(format);
    }

    /// <summary>
    /// Writes the meeting to <paramref name="path"/>. For the audio, <paramref name="audioPath"/> is
    /// the meeting's audio file (MeetingStore.AudioPath) and <paramref name="media"/> encodes a WAV.
    /// </summary>
    public static void Write(Meeting meeting, ExportFormat format, string path, string? audioPath = null, IMeetingMedia? media = null)
    {
        switch (format)
        {
            case ExportFormat.Pdf: File.WriteAllBytes(path, Pdf(meeting)); break;
            case ExportFormat.Word: File.WriteAllBytes(path, Word(meeting)); break;
            case ExportFormat.Markdown: File.WriteAllText(path, Markdown(meeting), new UTF8Encoding(false)); break;
            case ExportFormat.Text: File.WriteAllText(path, Text(meeting), new UTF8Encoding(false)); break;
            case ExportFormat.Subtitles: File.WriteAllText(path, Srt(meeting), new UTF8Encoding(false)); break;
            default:
                if (audioPath is null || !File.Exists(audioPath))
                    throw new FileNotFoundException(Loc.T("Ljudet för mötet finns inte kvar.", "The meeting’s audio is no longer kept."));
                if (audioPath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)) File.Copy(audioPath, path, overwrite: true);
                else if (media is null || !media.EncodeM4a(audioPath, path))
                    throw new IOException(Loc.T("Ljudet kunde inte sparas som .m4a.", "The audio couldn’t be saved as .m4a."));
                break;
        }
    }

    // ---- The content, once -----------------------------------------------------------

    private enum Kind { Title, Meta, Heading, Subheading, Paragraph, Bullet, Line }

    /// One block of the document. For a Line: <see cref="Who"/> and <see cref="Time"/> head the text.
    private sealed record Block(Kind Kind, string Text, string? Time = null, string? Who = null);

    public static string Heading(string key) => key switch
    {
        "overview" => Loc.T("Översikt", "Overview"),
        "topics" => Loc.T("Det här pratade ni om", "What you talked about"),
        "decisions" => Loc.T("Beslut", "Decisions"),
        "todo" => Loc.T("Att göra", "To do"),
        "remember" => Loc.T("Värt att komma ihåg", "Worth remembering"),
        "notes" => Loc.T("Anteckningar", "Notes"),
        "transcript" => Loc.T("Utskrift", "Transcript"),
        _ => key,
    };

    public static string MarkName(MarkKind kind) => kind switch
    {
        MarkKind.Decision => Loc.T("Beslut", "Decision"),
        MarkKind.Todo => Loc.T("Att göra", "To do"),
        _ => Loc.T("Viktigt", "Important"),
    };

    /// "Tuesday 4 November 2026 · 9:30–10:05 · 35 min · Anna and Erik".
    public static string MetaLine(Meeting m)
    {
        var culture = Loc.Culture;
        var date = m.Started.ToString(Loc.IsSwedish ? "dddd d MMMM yyyy" : "dddd, MMMM d, yyyy", culture);
        date = char.ToUpper(date[0], culture) + date[1..];
        var time = "H:mm";   // 9:30 in both languages, as in the app
        var parts = new List<string> { date, $"{m.Started.ToString(time, culture)}–{m.Ended.ToString(time, culture)}", Meeting.Length(m.Duration) };
        if (m.Talkers.Any()) parts.Add(m.PeopleLine);
        return string.Join(" · ", parts);
    }

    private static string? At(double? time) => time is { } t ? Meeting.Clock(t) : null;

    private static List<Block> Blocks(Meeting m, bool transcript = true)
    {
        var blocks = new List<Block> { new(Kind.Title, m.Title.Length > 0 ? m.Title : Loc.T("Möte", "Meeting")), new(Kind.Meta, MetaLine(m)) };
        string Name(string? speakerId) => speakerId is null ? "" : m.SpeakerById(speakerId)?.DisplayName ?? speakerId;

        if (m.Summary is { } s)
        {
            if (s.Overview.Length > 0) { blocks.Add(new(Kind.Heading, Heading("overview"))); blocks.Add(new(Kind.Paragraph, s.Overview)); }
            if (s.Topics.Count > 0)
            {
                blocks.Add(new(Kind.Heading, Heading("topics")));
                foreach (var topic in s.Topics)
                {
                    blocks.Add(new(Kind.Subheading, topic.Title, At(topic.Items.FirstOrDefault(i => i.Time is not null)?.Time)));
                    blocks.AddRange(topic.Items.Select(i => new Block(Kind.Bullet, i.Text, At(i.Time))));
                }
            }
            if (s.Decisions.Count > 0)
            {
                blocks.Add(new(Kind.Heading, Heading("decisions")));
                blocks.AddRange(s.Decisions.Select(d => new Block(Kind.Bullet, d.Text, At(d.Time))));
            }
            if (s.Todos.Count > 0)
            {
                blocks.Add(new(Kind.Heading, Heading("todo")));
                blocks.AddRange(s.Todos.Select(t => new Block(Kind.Bullet, (t.Done ? "✓ " : "") + t.Text, At(t.Time), t.Owner)));
            }
            if (s.Remember.Count > 0)
            {
                blocks.Add(new(Kind.Heading, Heading("remember")));
                blocks.AddRange(s.Remember.Select(r => new Block(Kind.Bullet, r.Text, At(r.Time))));
            }
        }

        var notes = m.Notes.Select(n => (n.Time, Text: n.Text))
            .Concat(m.Marks.Select(k => (k.Time, Text: MarkName(k.Kind))))
            .OrderBy(n => n.Time).ToList();
        if (notes.Count > 0)
        {
            blocks.Add(new(Kind.Heading, Heading("notes")));
            blocks.AddRange(notes.Select(n => new Block(Kind.Bullet, n.Text, Meeting.Clock(n.Time))));
        }

        if (transcript && m.Segments.Count > 0)
        {
            blocks.Add(new(Kind.Heading, Heading("transcript")));
            // A speaker's lines in a row are one paragraph, headed once.
            foreach (var run in Runs(m))
                blocks.Add(new(Kind.Line, string.Join(" ", run.Select(r => r.Text)), Meeting.Clock(run[0].Start), Name(run[0].SpeakerId)));
        }
        return blocks;
    }

    /// Consecutive segments by the same speaker, background sound left out.
    private static List<List<Segment>> Runs(Meeting m)
    {
        var runs = new List<List<Segment>>();
        foreach (var seg in m.Segments.OrderBy(s => s.Start))
        {
            if (m.SpeakerById(seg.SpeakerId)?.IsBackground == true) continue;
            if (runs.Count > 0 && runs[^1][0].SpeakerId == seg.SpeakerId && seg.Start - runs[^1][^1].End < 3) runs[^1].Add(seg);
            else runs.Add([seg]);
        }
        return runs;
    }

    // ---- Markdown and plain text -------------------------------------------------------

    public static string Markdown(Meeting meeting)
    {
        var sb = new StringBuilder();
        foreach (var b in Blocks(meeting))
        {
            switch (b.Kind)
            {
                case Kind.Title: sb.Append("# ").AppendLine(Escape(b.Text)).AppendLine(); break;
                case Kind.Meta: sb.Append('*').Append(Escape(b.Text)).AppendLine("*").AppendLine(); break;
                case Kind.Heading: sb.Append("## ").AppendLine(Escape(b.Text)).AppendLine(); break;
                case Kind.Subheading: sb.Append("### ").Append(Escape(b.Text)).AppendLine(b.Time is null ? "" : $" ({b.Time})").AppendLine(); break;
                case Kind.Paragraph: sb.AppendLine(Escape(b.Text)).AppendLine(); break;
                case Kind.Bullet:
                    sb.Append("- ");
                    if (b.Who is { Length: > 0 }) sb.Append("**").Append(Escape(b.Who)).Append(":** ");
                    sb.Append(Escape(b.Text));
                    if (b.Time is not null) sb.Append(" (").Append(b.Time).Append(')');
                    sb.AppendLine();
                    break;
                case Kind.Line: sb.Append("**").Append(Escape(b.Who ?? "")).Append("** · ").AppendLine(b.Time).AppendLine(Escape(b.Text)).AppendLine(); break;
            }
        }
        return Tidy(sb.ToString(), markdown: true);
    }

    public static string Text(Meeting meeting)
    {
        var sb = new StringBuilder();
        foreach (var b in Blocks(meeting))
        {
            switch (b.Kind)
            {
                case Kind.Title: sb.AppendLine(b.Text); break;
                case Kind.Meta: sb.AppendLine(b.Text).AppendLine(); break;
                case Kind.Heading: sb.AppendLine().AppendLine(b.Text.ToUpper(Loc.Culture)).AppendLine(); break;
                case Kind.Subheading: sb.AppendLine().Append(b.Text).AppendLine(b.Time is null ? "" : $" ({b.Time})"); break;
                case Kind.Paragraph: sb.AppendLine(b.Text); break;
                case Kind.Bullet:
                    sb.Append("• ");
                    if (b.Who is { Length: > 0 }) sb.Append(b.Who).Append(": ");
                    sb.Append(b.Text);
                    if (b.Time is not null) sb.Append(" (").Append(b.Time).Append(')');
                    sb.AppendLine();
                    break;
                case Kind.Line: sb.Append('[').Append(b.Time).Append("] ").Append(b.Who).Append(": ").AppendLine(b.Text); break;
            }
        }
        return Tidy(sb.ToString(), markdown: false);
    }

    /// Bullets are followed by a blank line where a new section starts; at most one blank line anywhere.
    private static string Tidy(string text, bool markdown)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var result = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0 && result.Count > 0 && result[^1].Length == 0) continue;
            var bullet = markdown ? "- " : "• ";
            if (line.Length > 0 && !line.StartsWith(bullet) && result.Count > 0 && result[^1].StartsWith(bullet)) result.Add("");
            result.Add(line);
        }
        return string.Join("\n", result).Trim() + "\n";
    }

    /// Characters Markdown would take as formatting.
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '*' or '_' or '`' or '[' or ']' or '<' or '>' or '#' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- Subtitles ---------------------------------------------------------------------

    /// SubRip: one cue per segment (long ones split), with the speaker's name.
    public static string Srt(Meeting meeting)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var seg in meeting.Segments.OrderBy(s => s.Start))
        {
            var speaker = meeting.SpeakerById(seg.SpeakerId);
            if (speaker?.IsBackground == true) continue;
            foreach (var (start, end, text) in SplitCue(seg))
            {
                sb.Append(++n).Append('\n');
                sb.Append(SrtTime(start)).Append(" --> ").Append(SrtTime(end)).Append('\n');
                sb.Append(speaker?.DisplayName ?? seg.SpeakerId).Append(": ").Append(text).Append("\n\n");
            }
        }
        return sb.ToString();
    }

    /// About 7 seconds a cue at most, split between words, time shared by length.
    private static IEnumerable<(double, double, string)> SplitCue(Segment seg)
    {
        var length = seg.End - seg.Start;
        var parts = Math.Max(1, (int)Math.Ceiling(length / 7));
        var words = seg.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts == 1 || words.Length < parts * 2) { yield return (seg.Start, seg.End, seg.Text); yield break; }
        var total = words.Sum(w => w.Length + 1);
        var at = 0;
        var start = seg.Start;
        for (var p = 0; p < parts; p++)
        {
            var take = p == parts - 1 ? words.Length - at : (int)Math.Round(words.Length / (double)parts);
            var chunk = words.Skip(at).Take(take).ToArray();
            at += take;
            var end = p == parts - 1 ? seg.End : start + length * chunk.Sum(w => w.Length + 1) / total;
            yield return (start, end, string.Join(' ', chunk));
            start = end;
        }
    }

    public static string SrtTime(double seconds)
    {
        var t = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
    }

    // ---- Word ---------------------------------------------------------------------------

    public static byte[] Word(Meeting meeting)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            AddStyles(main);
            var body = new Body();
            foreach (var b in Blocks(meeting))
            {
                switch (b.Kind)
                {
                    case Kind.Title: body.Append(Para("Title", Run(b.Text))); break;
                    case Kind.Meta: body.Append(Para("Subtitle", Run(b.Text))); break;
                    case Kind.Heading: body.Append(Para("Heading1", Run(b.Text))); break;
                    case Kind.Subheading: body.Append(Para("Heading2", Run(b.Text), b.Time is null ? null : Run($"  {b.Time}", grey: true))); break;
                    case Kind.Paragraph: body.Append(Para(null, Run(b.Text))); break;
                    case Kind.Bullet:
                        body.Append(Para("ListBullet",
                            b.Who is { Length: > 0 } ? Run(b.Who + ": ", bold: true) : null,
                            Run(b.Text),
                            b.Time is null ? null : Run($"  {b.Time}", grey: true)));
                        break;
                    case Kind.Line:
                        body.Append(Para("Speaker", Run(b.Who ?? "", bold: true), Run($"  {b.Time}", grey: true)));
                        body.Append(Para(null, Run(b.Text)));
                        break;
                }
            }
            body.Append(new SectionProperties(new PageMargin { Top = 1300, Bottom = 1300, Left = 1300, Right = 1300 }));
            main.Document = new Document(body);
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static Run? Run(string text, bool bold = false, bool grey = false)
    {
        var props = new RunProperties();
        if (bold) props.Append(new Bold());
        if (grey) props.Append(new Color { Val = "8A8A8A" });
        return new Run(props, new DocumentFormat.OpenXml.Wordprocessing.Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Paragraph Para(string? style, params Run?[] runs)
    {
        var p = new Paragraph();
        if (style is not null) p.Append(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        foreach (var r in runs) if (r is not null) p.Append(r);
        return p;
    }

    /// A few styles, so Word's navigation shows the headings: serif titles, as in the app.
    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        Style Make(string id, string name, string font, int halfPoints, bool bold, int before, int after, string? color = null, int? outline = null, bool bullet = false)
        {
            var run = new StyleRunProperties(new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font }, new FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) });
            if (bold) run.Append(new Bold());
            if (color is not null) run.Append(new Color { Val = color });
            var para = new StyleParagraphProperties(new SpacingBetweenLines { Before = before.ToString(CultureInfo.InvariantCulture), After = after.ToString(CultureInfo.InvariantCulture) });
            if (outline is { } level) para.Append(new OutlineLevel { Val = level });
            if (bullet) para.Append(new Indentation { Left = "360" });
            return new Style(new StyleName { Val = name }, new BasedOn { Val = "Normal" }, new PrimaryStyle(), para, run) { Type = StyleValues.Paragraph, StyleId = id };
        }
        var normal = new Style(new StyleName { Val = "Normal" }, new PrimaryStyle(),
            new StyleParagraphProperties(new SpacingBetweenLines { After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto }),
            new StyleRunProperties(new RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI", ComplexScript = "Segoe UI" }, new FontSize { Val = "21" }))
        { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true };
        part.Styles = new Styles(normal,
            Make("Title", "Title", "Georgia", 44, false, 0, 80),
            Make("Subtitle", "Subtitle", "Segoe UI", 20, false, 0, 240, "6E6E6E"),
            Make("Heading1", "heading 1", "Georgia", 30, false, 360, 120, null, 0),
            Make("Heading2", "heading 2", "Segoe UI", 22, true, 200, 80, null, 1),
            Make("ListBullet", "List Bullet", "Segoe UI", 21, false, 0, 60, null, null, bullet: true),
            Make("Speaker", "Speaker", "Segoe UI", 19, false, 160, 20, "3A3A3A"));
        part.Styles.Save();
    }

    // ---- PDF ----------------------------------------------------------------------------

    /// A4, with a serif title and headings (Georgia) and Segoe UI for the text.
    public static byte[] Pdf(Meeting meeting)
    {
        lock (FontGate) GlobalFontSettings.FontResolver ??= new WindowsFonts();
        using var doc = new PdfDocument();
        doc.Info.Title = meeting.Title;
        doc.Info.Creator = "Mindtalk";
        var writer = new PdfWriter(doc);
        foreach (var b in Blocks(meeting))
        {
            switch (b.Kind)
            {
                case Kind.Title: writer.Paragraph(b.Text, writer.TitleFont, XColors.Black, after: 6); break;
                case Kind.Meta: writer.Paragraph(b.Text, writer.SmallFont, XColor.FromArgb(110, 110, 110), after: 14); break;
                case Kind.Heading: writer.Space(10); writer.Paragraph(b.Text, writer.HeadingFont, XColors.Black, after: 6, keepWithNext: 40); break;
                case Kind.Subheading: writer.Space(4); writer.Paragraph(b.Text + (b.Time is null ? "" : $"   {b.Time}"), writer.BoldFont, XColors.Black, after: 3, keepWithNext: 30); break;
                case Kind.Paragraph: writer.Paragraph(b.Text, writer.BodyFont, XColors.Black, after: 6); break;
                case Kind.Bullet:
                    var text = (b.Who is { Length: > 0 } ? b.Who + ": " : "") + b.Text + (b.Time is null ? "" : $"   {b.Time}");
                    writer.Paragraph(text, writer.BodyFont, XColors.Black, after: 3, bullet: true);
                    break;
                case Kind.Line:
                    writer.Paragraph($"{b.Who}   {b.Time}", writer.SmallBoldFont, XColor.FromArgb(70, 70, 70), after: 1, keepWithNext: 28);
                    writer.Paragraph(b.Text, writer.BodyFont, XColors.Black, after: 7);
                    break;
            }
        }
        writer.Finish();
        using var stream = new MemoryStream();
        doc.Save(stream, false);
        return stream.ToArray();
    }

    private static readonly object FontGate = new();

    /// <summary>
    /// Windows' own fonts for PDFsharp (embedded as subsets): Georgia for titles, Segoe UI for
    /// text — falling back to Arial, which every Windows has.
    /// </summary>
    private sealed class WindowsFonts : IFontResolver
    {
        private static readonly string Folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        private static readonly Dictionary<string, string[]> Files = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Georgia"] = ["georgia.ttf", "georgiab.ttf"],
            ["Segoe UI"] = ["segoeui.ttf", "segoeuib.ttf"],
            ["Arial"] = ["arial.ttf", "arialbd.ttf"],
        };

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            foreach (var family in new[] { familyName, "Arial" })
            {
                if (!Files.TryGetValue(family, out var files)) continue;
                var file = files[bold ? 1 : 0];
                if (File.Exists(Path.Combine(Folder, file))) return new FontResolverInfo(file);
                if (File.Exists(Path.Combine(Folder, files[0]))) return new FontResolverInfo(files[0], bold, false);
            }
            return null;
        }

        public byte[]? GetFont(string faceName)
        {
            var path = Path.Combine(Folder, faceName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }

    /// Lays text out top to bottom, wrapping by words and starting new pages as needed.
    private sealed class PdfWriter
    {
        private const double Margin = 56, BulletIndent = 14;
        private readonly PdfDocument _doc;
        private PdfPage _page = null!;
        private XGraphics _g = null!;
        private double _y;

        public readonly XFont TitleFont = new("Georgia", 22);
        public readonly XFont HeadingFont = new("Georgia", 15);
        public readonly XFont BodyFont = new("Segoe UI", 10.5);
        public readonly XFont BoldFont = new("Segoe UI", 10.5, XFontStyleEx.Bold);
        public readonly XFont SmallFont = new("Segoe UI", 9.5);
        public readonly XFont SmallBoldFont = new("Segoe UI", 9, XFontStyleEx.Bold);

        public PdfWriter(PdfDocument doc)
        {
            _doc = doc;
            NewPage();
        }

        private double Width => _page.Width.Point - 2 * Margin;
        private double Bottom => _page.Height.Point - Margin;

        private void NewPage()
        {
            _g?.Dispose();
            _page = _doc.AddPage();
            _page.Size = PdfSharp.PageSize.A4;
            _g = XGraphics.FromPdfPage(_page);
            _y = Margin;
        }

        public void Space(double points) => _y += points;

        public void Paragraph(string text, XFont font, XColor color, double after, bool bullet = false, double keepWithNext = 0)
        {
            var lineHeight = font.Size * 1.4;
            var indent = bullet ? BulletIndent : 0;
            var lines = Wrap(text, font, Width - indent);
            if (_y + lineHeight + keepWithNext > Bottom) NewPage();
            var brush = new XSolidBrush(color);
            var first = true;
            foreach (var line in lines)
            {
                if (_y + lineHeight > Bottom) NewPage();
                if (bullet && first) _g.DrawString("•", font, brush, Margin + 2, _y + font.Size);
                _g.DrawString(line, font, brush, Margin + indent, _y + font.Size);
                _y += lineHeight;
                first = false;
            }
            _y += after;
        }

        private List<string> Wrap(string text, XFont font, double width)
        {
            var lines = new List<string>();
            foreach (var paragraph in text.Replace("\r", "").Split('\n'))
            {
                var line = "";
                foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && _g.MeasureString(candidate, font).Width > width)
                    {
                        lines.Add(line);
                        line = word;
                    }
                    else line = candidate;
                }
                lines.Add(line);
            }
            return lines;
        }

        public void Finish() => _g.Dispose();
    }
}
