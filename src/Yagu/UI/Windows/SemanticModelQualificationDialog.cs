using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Yagu.Helpers;
using Yagu.Services.Ai;
using System.Diagnostics.CodeAnalysis;

namespace Yagu;

/// <summary>The user's decision from <see cref="SemanticModelQualificationDialog"/>.</summary>
internal sealed class SemanticModelQualificationDialogResult
{
    /// <summary>The user picked a model to use. <see cref="ChosenAlias"/> is the model.</summary>
    public bool Accepted { get; init; }

    /// <summary>The model alias the user chose (when <see cref="Accepted"/>).</summary>
    public string? ChosenAlias { get; init; }

    /// <summary>The user finished the check but chose not to switch models ("Skip"). The one-time check
    /// should be marked complete and the recommendation recorded, without changing the effective model.</summary>
    public bool Declined { get; init; }

    /// <summary>The user cancelled the running sweep. Settings should be left untouched so the check is
    /// offered again next launch.</summary>
    public bool Cancelled { get; init; }

    /// <summary>The check could not validate a model that produces usable results on this PC. AI
    /// (Semantic) search should be turned off and the app defaulted to Traditional (literal) search; the
    /// one-time check is marked complete.</summary>
    public bool SwitchToTraditional { get; init; }

    /// <summary>The user clicked the "AI settings" link on the no-usable-model notice, so the AI settings
    /// tab should be opened after the dialog closes.</summary>
    public bool OpenAiSettingsRequested { get; init; }

    /// <summary>The completed sweep result, when one was produced.</summary>
    public ModelQualificationResult? Result { get; init; }
}

/// <summary>
/// Borderless first-run modal that runs the AI-model qualification sweep (testing candidate models with
/// a mix of simple and complex queries), then presents the recommended model plus per-candidate accuracy
/// and speed so the user can accept it or pick another. Mirrors the title-bar-less owned-window pattern
/// used by <see cref="SemanticModelDownloadDialog"/>.
/// </summary>
[SuppressMessage(
    "Reliability", "CA1001",
    Justification = "The CancellationTokenSource is cancelled and disposed in OnClosed when the window closes.")]
internal sealed class SemanticModelQualificationDialog : Window
{
    public delegate Task<ModelQualificationResult> RunDelegate(
        ModelQualificationThresholds thresholds,
        IProgress<SemanticQualificationProgress>? progress, CancellationToken cancellationToken);

    private const int DialogWidth = 760;
    private const int DialogHeight = 720;

    private static readonly HashSet<SemanticModelQualificationDialog> OpenWindows = new();

    private readonly TaskCompletionSource<SemanticModelQualificationDialogResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IntPtr _ownerHwnd;
    private readonly ElementTheme _theme;
    private readonly RunDelegate _run;
    private readonly CancellationTokenSource _cts = new();

    private Grid _root = null!;
    private TextBlock _titleText = null!;
    private TextBlock _subtitleText = null!;
    private Border _bodyHost = null!;
    private StackPanel _footer = null!;

    private ModelQualificationResult? _result;
    private string? _selectedAlias;

    // ── Config state (user-chosen time limits, collected before the sweep) ──
    private ModelQualificationThresholds _thresholds = ModelQualificationThresholds.Default;
    private NumberBox? _modelLoadBox;
    private NumberBox? _simpleQueryBox;
    private NumberBox? _complexQueryBox;

    private bool _accepted;
    private bool _declined;
    private bool _cancelled;
    private bool _switchToTraditional;
    private bool _openAiSettings;
    private bool _completed;

    /// <summary><see cref="Environment.TickCount64"/> before which a <see cref="Cancel"/> click is ignored,
    /// so the second click of an accidental double-click that STARTED the sweep can't immediately cancel
    /// it (the primary button is replaced by "Cancel" at the same spot). 0 = no suppression.</summary>
    private long _suppressCancelUntilTick;

    private SemanticModelQualificationDialog(IntPtr ownerHwnd, ElementTheme theme, RunDelegate run)
    {
        _ownerHwnd = ownerHwnd;
        _theme = theme;
        _run = run;

        Title = "AI model check";
        Content = BuildSkeleton();
        Closed += OnClosed;

        ExtendsContentIntoTitleBar = true;

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowForegroundHelper.ConfigureOwnedWindow(hwnd, _ownerHwnd);

        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Title = Title;
        WindowForegroundHelper.CenterWindowOverOwner(appWindow, _ownerHwnd, DialogWidth, DialogHeight, minHeight: 340);
        TryConfigurePresenter(appWindow);
        TrySetIcon(appWindow);
    }

    public static Task<SemanticModelQualificationDialogResult> ShowAsync(
        IntPtr ownerHwnd, ElementTheme theme, RunDelegate run)
    {
        var dialog = new SemanticModelQualificationDialog(ownerHwnd, theme, run);
        return dialog.ShowModalAsync();
    }

    private async Task<SemanticModelQualificationDialogResult> ShowModalAsync()
    {
        OpenWindows.Add(this);

        if (_ownerHwnd != IntPtr.Zero)
            EnableWindow(_ownerHwnd, false);

        Activate();
        WindowForegroundHelper.BringOwnedWindowToFront(this, _ownerHwnd);

        // The sweep does not start automatically: the user first chooses their time limits in the config
        // state and clicks "Run". BuildSkeleton() already left the dialog showing that state.
        return await _completion.Task.ConfigureAwait(true);
    }

    // ── Skeleton ──────────────────────────────────────────────────────────────────────────────

    private Grid BuildSkeleton()
    {
        _root = new Grid
        {
            Padding = new Thickness(28, 26, 28, 24),
            RowSpacing = 16,
        };
        Yagu.Services.AppThemeService.ApplyThemedDialogSurface(_root, _theme);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // footer

        var header = new StackPanel { Spacing = 16 };
        _titleText = new TextBlock
        {
            Text = "Checking AI models",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.WrapWholeWords,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleLine.Children.Add(new FontIcon
        {
            Glyph = "\uE99A", // AI model
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleLine.Children.Add(_titleText);
        _subtitleText = new TextBlock
        {
            Text = "Testing models on your PC…",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Opacity = 0.82,
        };
        header.Children.Add(titleLine);
        header.Children.Add(_subtitleText);
        Grid.SetRow(header, 0);
        _root.Children.Add(header);

        _bodyHost = new Border { VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(_bodyHost, 1);
        _root.Children.Add(_bodyHost);

        _footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };
        Grid.SetRow(_footer, 2);
        _root.Children.Add(_footer);

        _root.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape)
            {
                args.Handled = true;
                // Escape before/at the config or running state cancels; on the result screen it skips.
                if (_result is null) Cancel();
                else Skip();
            }
        };

        ShowConfigState();
        return _root;
    }

    // ── States ────────────────────────────────────────────────────────────────────────────────

    // Live chat transcript shown while the sweep runs (see ShowRunningState / OnProgress).
    private ScrollViewer? _chatScroll;
    private StackPanel? _chatLog;
    private TextBlock? _streamingAssistantText;   // assistant bubble currently streaming a response
    private StackPanel? _streamingAssistantStack; // its container, so a status chip can be appended
    private TextBlock? _modelProgressText;         // persistent "phi-4 — 3 / 17 tests · 18%" line
    private ProgressBar? _modelProgressBar;        // determinate per-model progress (0-100)

    // Typewriter reveal of the (non-streaming) answer: buffered text drained a few chars per frame.
    private const int RevealCharsPerTick = 12;
    private readonly System.Text.StringBuilder _revealBuffer = new();
    private DispatcherTimer? _revealTimer;
    private SemanticQualificationProgress? _pendingChipVerdict;

    /// <summary>First screen: the user picks how long to wait for a model to load and how slow a simple /
    /// complex query may be before a candidate is abandoned, then clicks "Run" to start the sweep.</summary>
    private void ShowConfigState()
    {
        _titleText.Text = "Checking AI models";
        _subtitleText.Text = "Choose how long you're willing to wait before a model is judged too slow, then start " +
            "the check. Yagu tests the models that fit this PC with a few sample searches and recommends the " +
            "fastest one that answers accurately. Everything runs on your PC.";

        var panel = new StackPanel { Spacing = 20 };
        _modelLoadBox = BuildThresholdInput(
            panel, "Max time to load a model (seconds)",
            ModelQualificationThresholds.DefaultModelLoadMaxMs / 1000);
        _simpleQueryBox = BuildThresholdInput(
            panel, "Max time for a simple query (seconds)",
            ModelQualificationThresholds.DefaultSimpleQueryMaxMs / 1000);
        _complexQueryBox = BuildThresholdInput(
            panel, "Max time for a complex query (seconds)",
            ModelQualificationThresholds.DefaultComplexQueryMaxMs / 1000);

        _bodyHost.Child = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        _footer.Children.Clear();
        AddFooterButton("Cancel", accent: false, Cancel);
        AddFooterButton("Run", accent: true, StartSweepFromConfig);
    }

    private static NumberBox BuildThresholdInput(StackPanel parent, string label, int defaultSeconds)
    {
        var box = new NumberBox
        {
            Header = label,
            Value = defaultSeconds,
            Minimum = 1,
            Maximum = 600,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 240,
        };
        parent.Children.Add(box);
        return box;
    }

    private void StartSweepFromConfig()
    {
        _thresholds = new ModelQualificationThresholds
        {
            ModelLoadMaxMs = SecondsToMs(_modelLoadBox, ModelQualificationThresholds.DefaultModelLoadMaxMs),
            SimpleQueryMaxMs = SecondsToMs(_simpleQueryBox, ModelQualificationThresholds.DefaultSimpleQueryMaxMs),
            ComplexQueryMaxMs = SecondsToMs(_complexQueryBox, ModelQualificationThresholds.DefaultComplexQueryMaxMs),
        };
        StartSweepGuarded();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>Starts (or restarts) the sweep from a footer button while guarding against an accidental
    /// double-click. Starting the sweep replaces the clicked primary button ("Run"/"Try again") with a
    /// "Cancel" button at the same spot, so the second click of a double-click would otherwise land on
    /// "Cancel" and close the dialog. We suppress <see cref="Cancel"/> for the system double-click
    /// interval after starting, which reliably swallows that second click regardless of dispatcher
    /// timing (a deliberate cancel a moment later still works).</summary>
    private void StartSweepGuarded()
    {
        uint doubleClickMs = GetDoubleClickTime();
        _suppressCancelUntilTick = Environment.TickCount64 + (doubleClickMs > 0 ? doubleClickMs : 500);
        _ = RunSweepAsync();
    }

    private static int SecondsToMs(NumberBox? box, int fallbackMs)
    {
        if (box is null || double.IsNaN(box.Value) || box.Value <= 0)
            return fallbackMs;
        return (int)Math.Round(box.Value * 1000);
    }

    private void ShowRunningState(string message)
    {
        _titleText.Text = "Checking AI models";
        _subtitleText.Text = "Yagu is asking each AI model that fits this PC a few sample searches and watching how " +
            "it answers, to pick the fastest one that's accurate. Everything runs on your PC.";

        _chatLog = new StackPanel { Spacing = 14 };
        _chatScroll = new ScrollViewer
        {
            Content = _chatLog,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        // Persistent per-model progress (always visible, above the scrolling transcript).
        _modelProgressText = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.NoWrap };
        _modelProgressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, IsIndeterminate = false };
        var progressPanel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 2, 0, 30) };
        progressPanel.Children.Add(_modelProgressText);
        progressPanel.Children.Add(_modelProgressBar);

        var bodyGrid = new Grid();
        bodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(progressPanel, 0);
        bodyGrid.Children.Add(progressPanel);
        Grid.SetRow(_chatScroll, 1);
        bodyGrid.Children.Add(_chatScroll);
        _bodyHost.Child = bodyGrid;

        _streamingAssistantText = null;
        _streamingAssistantStack = null;

        if (!string.IsNullOrWhiteSpace(message))
            AddSystemMessage(message);

        _footer.Children.Clear();
        AddFooterButton("Cancel", accent: false, Cancel);
    }

    // ── Chat transcript ───────────────────────────────────────────────────────────────────────

    /// <summary>Drives the live chat transcript from the sweep's progress stream: model headers and
    /// status lines as system messages, each probe query as a right-aligned "user" bubble, and the
    /// model's streamed answer as a left-aligned "assistant" bubble that grows token-by-token and is
    /// stamped with a ✓/✗ verdict when the probe completes.</summary>
    private void OnProgress(SemanticQualificationProgress p)
    {
        if (_chatLog is null) return;
        switch (p.Stage)
        {
            case SemanticQualificationStage.EnumeratingCandidates:
                AddSystemMessage("Finding AI models that fit this PC…");
                break;
            case SemanticQualificationStage.PreparingCandidate:
                _streamingAssistantText = null;
                _streamingAssistantStack = null;
                AddModelHeader(p.CandidateAlias, p.CandidateIndex, p.CandidateCount);
                UpdateModelProgress(p.CandidateAlias, 0, p.ProbeCount);
                AddSystemMessage($"Loading {p.CandidateAlias}…");
                break;
            case SemanticQualificationStage.Probing:
                AddUserBubble(p.ProbeQuery, p.ProbeIndex, p.ProbeCount);
                StartAssistantBubble();
                break;
            case SemanticQualificationStage.ProbeToken:
                AppendAssistantToken(p.TokenDelta);
                break;
            case SemanticQualificationStage.ProbeCompleted:
                UpdateModelProgress(p.CandidateAlias, p.ProbeIndex, p.ProbeCount);
                FinishAssistantBubble(p);
                break;
            case SemanticQualificationStage.Done:
                break;
        }
    }

    private void AddSystemMessage(string text)
    {
        if (_chatLog is null) return;
        _chatLog.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.6,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2),
        });
        ScrollTranscriptToEnd();
    }

    private void AddModelHeader(string? alias, int index, int count)
    {
        if (_chatLog is null) return;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 14, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        row.Children.Add(new FontIcon { Glyph = "\uE99A", FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        string suffix = count > 0 ? $"  ({index}/{count})" : string.Empty;
        row.Children.Add(new TextBlock
        {
            Text = $"{alias}{suffix}",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _chatLog.Children.Add(row);
        ScrollTranscriptToEnd();
    }

    private void UpdateModelProgress(string? alias, int completed, int total)
    {
        if (_modelProgressText is null || _modelProgressBar is null) return;
        if (total > 0)
        {
            int pct = (int)Math.Round(completed * 100.0 / total);
            _modelProgressText.Text = $"{alias} — {completed} / {total} tests · {pct}%";
            _modelProgressBar.Value = pct;
        }
        else
        {
            _modelProgressText.Text = alias is null ? string.Empty : $"{alias}…";
            _modelProgressBar.Value = 0;
        }
    }

    private void AddUserBubble(string? text, int probeIndex, int probeCount)
    {
        if (_chatLog is null) return;
        if (probeCount > 0)
            _chatLog.Children.Add(new TextBlock
            {
                Text = $"Test {probeIndex} of {probeCount}",
                FontSize = 10.5,
                Opacity = 0.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 2, 0),
            });
        _chatLog.Children.Add(new Border
        {
            Background = ResourceBrush("AccentFillColorDefaultBrush", ColorHelper.FromArgb(0xFF, 0x4C, 0x8B, 0xF5)),
            CornerRadius = new CornerRadius(10, 10, 2, 10),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 520,
            Margin = new Thickness(48, 0, 0, 0),
            Child = new TextBlock
            {
                Text = text ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            },
        });
        ScrollTranscriptToEnd();
    }

    private void StartAssistantBubble()
    {
        if (_chatLog is null) return;
        FlushReveal(); // safety: finalize any leftover reveal from a prior bubble before starting a new one
        _streamingAssistantStack = new StackPanel { Spacing = 6 };
        _streamingAssistantText = new TextBlock
        {
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            Opacity = 0.92,
        };
        _streamingAssistantStack.Children.Add(_streamingAssistantText);
        _chatLog.Children.Add(new Border
        {
            Background = ResourceBrush("LayerFillColorDefaultBrush", ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ResourceBrush("ControlElevationBorderBrush", ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10, 10, 10, 2),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 470,
            Margin = new Thickness(0, 0, 48, 0),
            Child = _streamingAssistantStack,
        });
        ScrollTranscriptToEnd();
    }

    // The model's answer arrives as one chunk (the sweep uses reliable non-streaming inference), but we
    // reveal it a few characters per frame so it still reads as a live "typed" response — a purely UI
    // animation driven by a dispatcher timer, so it can never stall the way SDK token-streaming does.
    private void AppendAssistantToken(string? delta)
    {
        if (_streamingAssistantText is null || string.IsNullOrEmpty(delta)) return;
        _revealBuffer.Append(delta);
        if (_revealTimer is null)
        {
            _revealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _revealTimer.Tick += (_, _) => RevealTick();
            _revealTimer.Start();
        }
    }

    private void RevealTick()
    {
        if (_streamingAssistantText is null || _revealBuffer.Length == 0)
        {
            StopRevealTimer();
            if (_revealBuffer.Length == 0 && _pendingChipVerdict is not null)
                CompleteAssistantBubble();
            return;
        }
        int n = Math.Min(RevealCharsPerTick, _revealBuffer.Length);
        _streamingAssistantText.Text += _revealBuffer.ToString(0, n);
        _revealBuffer.Remove(0, n);
        ScrollTranscriptToEnd(updateLayout: false);
        if (_revealBuffer.Length == 0)
        {
            StopRevealTimer();
            if (_pendingChipVerdict is not null)
                CompleteAssistantBubble();
        }
    }

    private void FlushReveal()
    {
        StopRevealTimer();
        if (_streamingAssistantText is not null && _revealBuffer.Length > 0)
            _streamingAssistantText.Text += _revealBuffer.ToString();
        _revealBuffer.Clear();
    }

    private void StopRevealTimer()
    {
        _revealTimer?.Stop();
        _revealTimer = null;
    }

    private void FinishAssistantBubble(SemanticQualificationProgress p)
    {
        // A candidate that never loaded reports completion with no query and no assistant bubble.
        if (_streamingAssistantStack is null)
        {
            if (p.ProbeQuery is null)
                AddSystemFailure(p.CandidateAlias);
            return;
        }

        // Defer the ✓/✗ verdict chip until the typewriter reveal of the answer finishes, so the verdict
        // isn't stamped on a half-shown response. If nothing is left to reveal, finalize immediately.
        _pendingChipVerdict = p;
        if (_revealTimer is null || _revealBuffer.Length == 0)
            CompleteAssistantBubble();
    }

    private void CompleteAssistantBubble()
    {
        if (_streamingAssistantStack is null || _pendingChipVerdict is null) return;
        FlushReveal();
        if (_streamingAssistantText is { Text.Length: 0 } empty)
            empty.Text = "(no answer)";
        _streamingAssistantStack.Children.Add(BuildStatusChip(_pendingChipVerdict));
        _streamingAssistantText = null;
        _streamingAssistantStack = null;
        _pendingChipVerdict = null;
        ScrollTranscriptToEnd();
    }

    private void AddSystemFailure(string? alias)
    {
        if (_chatLog is null) return;
        var red = ColorHelper.FromArgb(0xFF, 0xE0, 0x5A, 0x4F);
        _chatLog.Children.Add(new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, red.R, red.G, red.B)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2),
            Child = new TextBlock
            {
                Text = $"\u2717 {alias} couldn't load — skipping.",
                FontSize = 12,
                Foreground = new SolidColorBrush(red),
                TextWrapping = TextWrapping.Wrap,
            },
        });
        ScrollTranscriptToEnd();
    }

    private static Border BuildStatusChip(SemanticQualificationProgress p)
    {
        Windows.UI.Color color;
        string label;
        if (p.ProbePassed)
        {
            if (p.ProbeSlowWarning)
            {
                color = ColorHelper.FromArgb(0xFF, 0xE0, 0xA5, 0x2E); // amber
                label = $"\u26A0 passed (slow) · {FormatSeconds(p.LatencyMs)}";
            }
            else
            {
                color = ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50); // green
                label = $"\u2713 passed · {FormatSeconds(p.LatencyMs)}";
            }
        }
        else
        {
            color = ColorHelper.FromArgb(0xFF, 0xE0, 0x5A, 0x4F); // red
            label = "\u2717 " + FailureText(p);
        }
        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 1, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(color),
            },
        };
    }

    private static string FailureText(SemanticQualificationProgress p) => p.FailureReason switch
    {
        SemanticProbeFailureReason.TooSlow => $"too slow ({FormatSeconds(p.LatencyMs)})",
        SemanticProbeFailureReason.Inaccurate => "wrong answer",
        SemanticProbeFailureReason.NoAnswer => "no usable answer",
        SemanticProbeFailureReason.Crashed => "couldn't run",
        _ => "failed",
    };

    private static string FormatSeconds(int ms)
    {
        if (ms <= 0) return "0.0s";
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 1000d:0.0}s");
    }

    private void ScrollTranscriptToEnd(bool updateLayout = true)
    {
        if (_chatScroll is null) return;
        if (updateLayout) _chatScroll.UpdateLayout();
        _chatScroll.ChangeView(null, double.MaxValue, null, disableAnimation: true);
    }

    private void ShowResultState()
    {
        StopRevealTimer();
        if (_result is null) return;

        string? suggestion = SemanticModelQualificationCoordinator.Suggestion(_result);
        _selectedAlias = suggestion;

        if (suggestion is null)
        {
            ShowNoUsableModelState();
            return;
        }

        bool qualified = _result.QualifiedModelAlias is not null;
        _titleText.Text = qualified ? "Recommended model ready" : "Best available model";
        _subtitleText.Text = qualified
            ? $"“{suggestion}” passed the accuracy and speed checks on this PC. Use it, or pick another model."
            : $"No model fully cleared the bar on this PC. “{suggestion}” did best. Use it, or pick another model.";

        var list = new StackPanel { Spacing = 12 };
        foreach (var report in _result.Reports)
            list.Children.Add(BuildReportRow(report, isSuggestion: string.Equals(report.ModelAlias, suggestion, StringComparison.OrdinalIgnoreCase)));

        _bodyHost.Child = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        _footer.Children.Clear();
        AddFooterButton("Skip", accent: false, Skip);
        _primaryButton = AddFooterButton("Use this model", accent: true, Accept);
    }

    /// <summary>Shown when the sweep finished but no candidate produced usable results on this PC. Yagu
    /// does NOT silently auto-pick a model: AI (Semantic) search is turned off and the app defaults to
    /// Traditional (literal) search. The message explains why and links to the AI settings tab so the user
    /// can opt back in and choose a model themselves later.</summary>
    private void ShowNoUsableModelState()
    {
        _titleText.Text = "No usable AI model found";
        _subtitleText.Text = "Yagu tested the AI models that fit this PC, but none returned usable results within your " +
            "time limits.";

        var panel = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = "AI (Semantic) search has been turned off and Yagu will use Traditional (literal) search instead, " +
                "so your searches stay fast and reliable.",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Opacity = 0.9,
        });

        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20, Opacity = 0.9 };
        body.Inlines.Add(new Run { Text = "You can turn AI search back on and pick a model yourself any time in " });
        var link = new Hyperlink();
        link.Inlines.Add(new Run { Text = "AI settings" });
        link.Click += (_, _) => { _openAiSettings = true; UseTraditional(); };
        body.Inlines.Add(link);
        body.Inlines.Add(new Run { Text = "." });
        panel.Children.Add(body);

        _bodyHost.Child = panel;

        _footer.Children.Clear();
        AddFooterButton("Try again", accent: false, () => { _result = null; StartSweepGuarded(); });
        _primaryButton = AddFooterButton("Use Traditional search", accent: true, UseTraditional);
    }

    private void ShowErrorState(string message)
    {
        _titleText.Text = "Model check didn't finish";
        _subtitleText.Text = "The check couldn't finish, so Yagu will use Traditional (literal) search unless you try again.";

        _bodyHost.Child = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Opacity = 0.9,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _footer.Children.Clear();
        AddFooterButton("Use Traditional search", accent: false, UseTraditional);
        AddFooterButton("Try again", accent: true, () => { _result = null; StartSweepGuarded(); });
    }

    private Border BuildReportRow(CandidateQualificationReport report, bool isSuggestion)
    {
        bool selectable = !report.Crashed && report.Probes.Any(p => p.Completed);

        var radio = new RadioButton
        {
            GroupName = "SemanticQualCandidate",
            IsChecked = isSuggestion,
            IsEnabled = selectable,
            Tag = report.ModelAlias,
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0),
        };
        radio.Checked += (s, _) =>
        {
            if (s is RadioButton { Tag: string alias })
                _selectedAlias = alias;
        };

        var content = new StackPanel { Spacing = 3 };

        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleLine.Children.Add(new TextBlock
        {
            Text = report.ModelAlias,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (isSuggestion)
            titleLine.Children.Add(BuildPill("Recommended", accent: true));
        if (report.Verdict.Passed)
            titleLine.Children.Add(BuildPill("Passed", accent: false));
        if (report.Crashed)
            titleLine.Children.Add(BuildPill("Failed to run", accent: false));
        content.Children.Add(titleLine);

        var detailParts = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"{report.EffectiveAccuracy * 100:0}% accurate"),
            FormatLatency(report.MedianLatencyMs),
        };
        if (report.Crashed)
            detailParts.Add("crashed during testing");
        content.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", detailParts),
            FontSize = 12,
            Opacity = 0.72,
        });

        radio.Content = content;

        return new Border
        {
            Background = ResourceBrush("LayerFillColorDefaultBrush", ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ResourceBrush("ControlElevationBorderBrush", ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Child = radio,
        };
    }

    private static Border BuildPill(string text, bool accent)
    {
        var brushKey = accent ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush";
        return new Border
        {
            Background = ResourceBrush(brushKey, accent
                ? ColorHelper.FromArgb(0xFF, 0x4C, 0x8B, 0xF5)
                : ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 1, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = accent
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
                    : ResourceBrush("TextFillColorPrimaryBrush", ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            },
        };
    }

    // ── Async flow ────────────────────────────────────────────────────────────────────────────

    private async Task RunSweepAsync()
    {
        ShowRunningState("Preparing the AI model check…");
        var progress = new Progress<SemanticQualificationProgress>(OnProgress);
        try
        {
            var result = await _run(_thresholds, progress, _cts.Token).ConfigureAwait(true);
            if (_cts.IsCancellationRequested) return;

            _result = result;
            ShowResultState();
        }
        catch (OperationCanceledException)
        {
            // Dialog closing / cancelled.
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
                ShowErrorState($"The model check couldn't complete: {ex.Message}");
        }
    }

    // ── Footer buttons ────────────────────────────────────────────────────────────────────────

    private Button? _primaryButton;

    private Button AddFooterButton(string text, bool accent, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            Padding = new Thickness(18, 8, 18, 8),
        };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out object style) && style is Style accentStyle)
            button.Style = accentStyle;
        button.Click += (_, _) => onClick();
        _footer.Children.Add(button);
        return button;
    }

    // ── Completion ────────────────────────────────────────────────────────────────────────────

    private void Accept()
    {
        _accepted = true;
        Complete();
    }

    private void Skip()
    {
        _declined = true;
        Complete();
    }

    /// <summary>The check couldn't validate a usable model: default the app to Traditional (literal)
    /// search (AI search is turned off) and mark the one-time check complete.</summary>
    private void UseTraditional()
    {
        _switchToTraditional = true;
        Complete();
    }

    private void Cancel()
    {
        // Swallow the second click of an accidental double-click that just started the sweep, so it does
        // not close the dialog (the primary button was replaced by this "Cancel"; see StartSweepGuarded).
        if (Environment.TickCount64 < _suppressCancelUntilTick)
            return;
        _cancelled = true;
        Complete();
    }

    private void Complete()
    {
        if (_completed) return;
        _completed = true;
        try { _cts.Cancel(); } catch { }
        Close();
    }

    private static string FormatLatency(int medianMs)
    {
        if (medianMs <= 0 || medianMs == int.MaxValue)
            return "speed unknown";
        double seconds = medianMs / 1000d;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds:0.0}s per query");
    }

    private static Brush ResourceBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out object resource) && resource is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    private static void TryConfigurePresenter(AppWindow appWindow)
    {
        try
        {
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            }
        }
        catch { }
    }

    private static void TrySetIcon(AppWindow appWindow)
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "yagu.ico");
            if (File.Exists(iconPath))
                appWindow.SetIcon(iconPath);
        }
        catch { }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        OpenWindows.Remove(this);
        StopRevealTimer();
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();

        if (_ownerHwnd != IntPtr.Zero)
        {
            EnableWindow(_ownerHwnd, true);
            SetForegroundWindow(_ownerHwnd);
        }

        _completion.TrySetResult(new SemanticModelQualificationDialogResult
        {
            Accepted = _accepted,
            ChosenAlias = _accepted ? _selectedAlias : null,
            Declined = _declined,
            Cancelled = _cancelled || (!_accepted && !_declined && !_switchToTraditional),
            SwitchToTraditional = _switchToTraditional,
            OpenAiSettingsRequested = _openAiSettings,
            Result = _result,
        });
    }

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
