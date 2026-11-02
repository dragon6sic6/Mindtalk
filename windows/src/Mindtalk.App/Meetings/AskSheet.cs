using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mindtalk.App.Controls;
using Mindtalk.Core;
using Mindtalk.Core.Meetings;

namespace Mindtalk.App.Meetings;

/// <summary>
/// The answer to a question about one meeting, a folder or all of them — written as it
/// comes, with where each part comes from: the meeting and the moment, a click away.
/// A sheet over the window; Esc or a click outside closes it (and stops the answer).
/// </summary>
internal sealed partial class AskSheet : Grid, IDisposable
{
    private readonly App _app;
    private readonly MainWindow _main;
    private readonly MeetingHub _hub;
    private readonly StackPanel _thread = new() { Spacing = 22 };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _field;
    private readonly MenuPicker<string> _scope;
    private readonly Guid? _meetingId;
    private CancellationTokenSource? _cts;

    private const string All = "\u0001all", This = "\u0001this";

    private AskSheet(App app, MainWindow main, MeetingHub hub, string? folder, Meeting? meeting)
    {
        _app = app;
        _main = main;
        _hub = hub;
        _meetingId = meeting?.Id;
        Width = 640;
        MaxHeight = Math.Max(360, main.AppWindow.Size.Height / Math.Max(1, main.Content.XamlRoot?.RasterizationScale ?? 1) - 140);
        Padding = new Thickness(28, 24, 28, 22);
        RowSpacing = 16;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Ask about [All meetings ▾]                                   ✕
        var options = new List<(string, string)>();
        if (meeting is not null) options.Add((This, Loc.T("Det här mötet", "This meeting")));
        options.Add((All, Loc.T("Alla möten", "All meetings")));
        foreach (var f in app.Meetings.Folders) options.Add((f, f));
        _scope = new MenuPicker<string>(options.ToArray(), meeting is not null ? This : folder ?? All, _ => { });
        _scope.HorizontalAlignment = HorizontalAlignment.Left;
        var header = new Grid();
        header.Children.Add(UI.HStack(10, UI.Text(Loc.T("Fråga om", "Ask about"), 14, FontWeights.SemiBold), _scope));
        var close = Parts.Circle(Icons.XMark, main.CloseSheet, Loc.T("Stäng", "Close"), 28);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(close);
        Children.Add(header);

        _scroll = new ScrollViewer { Content = _thread, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, Padding = new Thickness(0, 0, 12, 0) };
        SetRow(_scroll, 1);
        Children.Add(_scroll);

        // A follow-up question.
        var bar = new Border { CornerRadius = new CornerRadius(21), BorderThickness = new Thickness(1), Padding = new Thickness(4, 0, 6, 0), Height = 42 };
        UI.Bind(bar, Border.BackgroundProperty, "FieldBrush");
        UI.Bind(bar, Border.BorderBrushProperty, "FieldStrokeBrush");
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _field = new TextBox
        {
            PlaceholderText = Loc.T("Fråga något mer", "Ask something else"),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
        };
        _field.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _field.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(_field, _field.PlaceholderText);
        _field.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            Submit();
        };
        row.Children.Add(_field);
        var send = Parts.Circle(Icons.ArrowUp, Submit, Loc.T("Fråga", "Ask"), 28);
        SetColumn(send, 1);
        row.Children.Add(send);
        bar.Child = row;
        SetRow(bar, 2);
        Children.Add(bar);

        // A single meeting: what was asked before.
        if (meeting is not null)
            foreach (var exchange in meeting.Questions.TakeLast(3))
            {
                _thread.Children.Add(Exchange(exchange.Question, out var answer, out var sources, out var waiting));
                waiting.Visibility = Visibility.Collapsed;
                answer.Text = exchange.Answer;
                AddSources(sources, exchange);
            }
    }

    public static void Show(App app, MainWindow main, MeetingHub hub, string question, string? folder, Meeting? meeting)
    {
        var sheet = new AskSheet(app, main, hub, folder, meeting);
        main.ShowSheet(sheet);
        sheet.Ask(question);
    }

    private void Submit()
    {
        var question = _field.Text.Trim();
        if (question.Length == 0) return;
        _field.Text = "";
        Ask(question);
    }

    private IReadOnlyList<Meeting> Scope()
    {
        var done = _app.Meetings.All.Where(m => m.State is MeetingState.Done && !(_hub.IsLive && _hub.Live!.Meeting.Id == m.Id));
        return _scope.Selection switch
        {
            This => _meetingId is { } id && _app.Meetings.Get(id) is { } m ? [m] : [],
            All => done.ToList(),
            var folder => done.Where(m => string.Equals(m.Folder, folder, StringComparison.CurrentCultureIgnoreCase)).ToList(),
        };
    }

    private async void Ask(string question)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var block = Exchange(question, out var answer, out var sources, out var waiting);
        _thread.Children.Add(block);
        ScrollToEnd();

        if (_app.Asker is not { } asker)
        {
            waiting.Visibility = Visibility.Collapsed;
            answer.Text = Loc.T("För att fråga om möten behövs en modell som skriver svaren. Välj en under Inställningar → Möten.",
                                "Asking about meetings needs a model to write the answers. Choose one under Settings → Meetings.");
            var settings = UI.SoftButton(Loc.T("Öppna inställningar", "Open Settings"), () => { _main.CloseSheet(); _main.ShowSection(SettingsSection.Meetings); });
            settings.HorizontalAlignment = HorizontalAlignment.Left;
            sources.Children.Add(settings);
            return;
        }
        var scope = Scope();
        if (scope.Count == 0)
        {
            waiting.Visibility = Visibility.Collapsed;
            answer.Text = Loc.T("Det finns inga möten att fråga om här än.", "There are no meetings to ask about here yet.");
            return;
        }
        var progress = new Progress<string>(text =>
        {
            if (token.IsCancellationRequested) return;
            waiting.Visibility = Visibility.Collapsed;
            answer.Text = text;
            ScrollToEnd();
        });
        try
        {
            var exchange = await asker.AskAsync(question, scope, progress, token);
            if (token.IsCancellationRequested) return;
            waiting.Visibility = Visibility.Collapsed;
            answer.Text = exchange.Answer;
            AddSources(sources, exchange);
            ScrollToEnd();
            if (_scope.Selection == This && _meetingId is { } id && _app.Meetings.Get(id) is { } meeting)
            {
                meeting.Questions.Add(exchange);
                _app.Meetings.Save(meeting);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Http.HttpRequestException)
        {
            waiting.Visibility = Visibility.Collapsed;
            answer.Text = Loc.T($"Svaret kunde inte skrivas: {ex.Message}", $"The answer couldn't be written: {ex.Message}");
        }
    }

    private static StackPanel Exchange(string question, out TextBlock answer, out StackPanel sources, out StackPanel waiting)
    {
        var q = Parts.Serif(question, 21);
        q.IsTextSelectionEnabled = true;
        waiting = UI.HStack(8, new ProgressRing { Width = 14, Height = 14, IsActive = true }, UI.Text(Loc.T("Läser mötena…", "Reading the meetings…"), 13, brush: "MutedBrush"));
        answer = new TextBlock { FontSize = 14.5, LineHeight = 23, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = UI.Brush("PrimaryBrush") };
        sources = new StackPanel { Spacing = 0 };
        return UI.VStack(10, q, waiting, answer, sources);
    }

    /// Where the answer comes from: each meeting and moment, clickable.
    private void AddSources(StackPanel sources, AskExchange exchange)
    {
        if (exchange.Citations.Count == 0) return;
        var caption = Parts.Caption(Loc.T("Varifrån", "Where it comes from"));
        caption.Margin = new Thickness(0, 8, 0, 8);
        sources.Children.Add(caption);
        var card = new StackPanel();
        var first = true;
        var now = DateTime.Now;
        foreach (var citation in exchange.Citations)
        {
            var meeting = _app.Meetings.Get(citation.MeetingId);
            if (!first) card.Children.Add(UI.Divider());
            first = false;
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(18, 11, 18, 11) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = meeting is null ? Loc.T("Ett borttaget möte", "A deleted meeting") : $"{Format.Title(meeting)} · {Format.Day(meeting.Started, now)}";
            var quote = UI.Text($"”{citation.Quote}”", 13, brush: "MutedBrush", wrap: true);
            quote.MaxLines = 2;
            quote.TextTrimming = TextTrimming.WordEllipsis;
            row.Children.Add(UI.VStack(3, UI.Text(title, 13, FontWeights.SemiBold), quote));
            var time = Parts.Time(citation.Time, null);
            time.IsEnabled = true;
            SetColumn(time, 1);
            time.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(time);
            var holder = new Border { Child = row, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(14) };
            if (meeting is not null)
            {
                Parts.Hover(holder, "Transparent");
                void Go() { _main.CloseSheet(); _hub.OpenMeeting(citation.MeetingId, citation.Time); }
                Parts.OnClick(holder, Go);
                time.Click += (_, _) => Go();
                AutomationProperties.SetName(holder, $"{title}, {Meeting.Clock(citation.Time)}");
            }
            card.Children.Add(holder);
        }
        var border = new Border { CornerRadius = new CornerRadius(14), Child = card };
        UI.Bind(border, Border.BackgroundProperty, "CardBrush");
        sources.Children.Add(border);
    }

    private void ScrollToEnd() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
        () => _scroll.ChangeView(null, _scroll.ScrollableHeight, null));

    public void Dispose() => _cts?.Cancel();
}
