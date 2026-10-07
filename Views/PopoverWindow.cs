using System.Globalization;
using System.Runtime.InteropServices;
using AiMeter.Models;
using AiMeter.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace AiMeter.Views;

// [Part 246] 트레이를 누르면 오른쪽 아래에 뜨는 팝오버.
// 시안(.omc/specs/agentgauge-integrated.html) 결정: B 계기판 · C 배터리 중 「하나만」 보이고 탭이 없다. 설정에서 고른다.
// D 트리맵(로컬 세션 로그 필요)과 A 작업 표시줄 스트립은 2차 범위 — 미리 자리만 만들지 않는다(죽은 UI 금지).
public sealed class PopoverWindow : Window
{
    private const double PopWidth = 380;

    private static readonly Windows.UI.Color Text1 = Hex("#F8FAFC");
    private static readonly Windows.UI.Color Text2 = Hex("#94A3B8");
    private static readonly Windows.UI.Color Track = Windows.UI.Color.FromArgb(0x29, 0x94, 0xA3, 0xB8);
    private static readonly Windows.UI.Color Line = Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
    private static readonly Windows.UI.Color Surface = Hex("#1E293B");
    private static readonly Windows.UI.Color Accent = Hex("#38BDF8");
    private static readonly Windows.UI.Color Lime = Hex("#84CC16");
    private static readonly Windows.UI.Color Sky = Hex("#38BDF8");
    private static readonly Windows.UI.Color Orange = Hex("#FB923C");
    private static readonly Windows.UI.Color Red = Hex("#F87171");

    private readonly UsageService usage;
    private readonly SettingsStore settings;
    private readonly ClaudeLogScanner scanner;
    private readonly Grid root;
    private readonly StackPanel body;
    private readonly TextBlock updatedText;
    private readonly Button settingsButton;
    private bool showingSettings;
    private bool isVisible;
    private DateTimeOffset lastHidden = DateTimeOffset.MinValue;
    private SubclassProc? subclassProc; // 창이 사는 동안 GC 되지 않게 붙잡아 둔다

    /// <summary>[Part 248] 설정에서 작업 표시줄 스트립을 켜거나 껐다 — App 이 스트립을 붙이거나 뗀다.</summary>
    public event Action? TaskbarStripChanged;

    /// <summary>[Part 253] 설정에서 도구 「표시」 를 바꿨다 — App 이 링 아이콘·스트립을 새 목록으로 갱신한다.</summary>
    public event Action? ToolVisibilityChanged;

    /// <summary>[Part 254] 게이지 기준(남은 양/쓴 양)이 바뀌었다 — App 이 스트립·링 아이콘을 다시 그린다.</summary>
    public event Action? GaugeBasisChanged;

    public PopoverWindow(UsageService usage, SettingsStore settings, ClaudeLogScanner scanner)
    {
        this.usage = usage;
        this.settings = settings;
        this.scanner = scanner;

        body = new StackPanel { Spacing = 10, Padding = new Thickness(14, 14, 14, 12) };
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        updatedText = new TextBlock { FontSize = 11, Foreground = Brush(Text2), VerticalAlignment = VerticalAlignment.Center };
        var refreshButton = IconButton("", S.T("새로고침", "Refresh"));
        refreshButton.Click += async (_, _) => await usage.RefreshAsync(true);
        settingsButton = IconButton("", S.T("설정", "Settings"));
        settingsButton.Click += (_, _) => { showingSettings = !showingSettings; Render(); Place(); };

        var footer = new Grid
        {
            Padding = new Thickness(14, 6, 8, 6),
            BorderBrush = Brush(Line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } },
        };
        footer.Children.Add(updatedText);
        Grid.SetColumn(refreshButton, 1);
        Grid.SetColumn(settingsButton, 2);
        footer.Children.Add(refreshButton);
        footer.Children.Add(settingsButton);

        root = new Grid
        {
            Width = PopWidth,
            RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0x0F, 0x17, 0x2A)),
            RowDefinitions = { new RowDefinition(), new RowDefinition { Height = GridLength.Auto } },
        };
        root.Children.Add(scroll);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        var esc = new KeyboardAccelerator { Key = VirtualKey.Escape };
        esc.Invoked += (_, e) => { e.Handled = true; HideNow(); };
        root.KeyboardAccelerators.Add(esc);
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden; // [Part 249] 「Esc」 안내 툴팁이 팝오버 위에 떠 있던 것

        Content = root;
        Title = "AiMeter";
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, e) => { e.Cancel = true; HideNow(); };

        var hwnd = WindowNative.GetWindowHandle(this);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        // 둥근 모서리는 유지하고 DWM 이 덧그리는 얇은 테두리만 없앤다
        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref noBorder, sizeof(int));

        // 크기 조절을 끈 창에도 Windows 가 얇은 대화상자 프레임(WS_DLGFRAME)을 되살려 흰 띠가 남는다.
        // WM_NCCALCSIZE 에서 창 전체를 내용 영역으로 돌려주면 프레임이 사라지고, 둥근 모서리·그림자는 DWM 이 그대로 그린다.
        subclassProc = (h, msg, wParam, lParam, id, data) =>
            msg == 0x0083 /* WM_NCCALCSIZE */ && wParam != IntPtr.Zero ? IntPtr.Zero : DefSubclassProc(h, msg, wParam, lParam);
        SetWindowSubclass(hwnd, subclassProc, 1, IntPtr.Zero);
        Closed += (_, _) => RemoveWindowSubclass(hwnd, subclassProc, 1);

        // 바깥을 누르면 닫힌다(라이트 디스미스)
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) HideNow();
        };

        Render();
    }

    // ── 열기/닫기 ─────────────────────────────────────────────

    public void Toggle()
    {
        if (isVisible) { HideNow(); return; }
        // 팝오버가 열린 채 트레이를 다시 누르면, 먼저 포커스를 잃어 닫히고 곧바로 이 호출이 온다 → 다시 열지 않는다
        if ((DateTimeOffset.UtcNow - lastHidden).TotalMilliseconds < 250) return;
        showingSettings = false;
        Render();
        Place();
        AppWindow.Show();
        Activate();
        isVisible = true;
        _ = usage.RefreshAsync(false);
        if (settings.View == SettingsStore.ViewSpend) StartScan(force: false);
    }

    private void HideNow()
    {
        if (!isVisible) return;
        isVisible = false;
        lastHidden = DateTimeOffset.UtcNow;
        AppWindow.Hide();
    }

    /// <summary>작업 영역(작업 표시줄 제외) 오른쪽 아래에 내용 높이만큼.</summary>
    private void Place()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double margin = 12;
        double maxHeight = area.Height / scale - margin * 2;

        root.Measure(new Size(PopWidth, double.PositiveInfinity));
        double height = Math.Min(Math.Ceiling(root.DesiredSize.Height), maxHeight);

        int w = (int)Math.Round(PopWidth * scale);
        int h = (int)Math.Round(height * scale);
        int m = (int)Math.Round(margin * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - w - m, area.Y + area.Height - h - m, w, h));
    }

    // ── 그리기 ───────────────────────────────────────────────

    public void Render()
    {
        body.Children.Clear();
        resetStyle = settings.ResetStyle;
        dateStyle = settings.DateStyle;
        showUsed = settings.ShowUsed;
        var snapshots = usage.Snapshots;

        if (showingSettings)
        {
            RenderSettings();
        }
        else if (snapshots.Count == 0)
        {
            body.Children.Add(Caption(S.T("불러오는 중…", "Loading…")));
        }
        else if (settings.View == SettingsStore.ViewBattery)
        {
            RenderBattery(snapshots);
        }
        else if (settings.View == SettingsStore.ViewSpend)
        {
            RenderSpend(snapshots);
        }
        else
        {
            RenderGauge(snapshots);
        }

        var captured = snapshots.Where(s => s.CapturedAt is not null).Select(s => s.CapturedAt!.Value).DefaultIfEmpty().Max();
        updatedText.Text = captured == default ? S.T("아직 받은 값 없음", "No data yet") : Ago(captured);
        string settingsName = showingSettings ? S.T("돌아가기", "Back") : S.T("설정", "Settings");
        ToolTipService.SetToolTip(settingsButton, settingsName);
        // [Part 253] 접근성 이름도 함께 — 툴팁만 바뀌고 이름은 처음 값(설정)에 머물러 화면 읽기·UIA 가 「돌아가기」 를 못 찾았다
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settingsButton, settingsName);
        ((FontIcon)settingsButton.Content).Glyph = showingSettings ? "" : "";

        if (isVisible) Place();
    }

    // 시안 B — 남은 양을 바늘 게이지로, 핵심 문장은 「언제 바닥나나」
    private void RenderGauge(IReadOnlyList<UsageSnapshot> snapshots)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var snap in snapshots)
        {
            body.Children.Add(ToolHeader(snap));
            if (snap.Message is not null) body.Children.Add(Notice(snap.Message, snap.State == SnapshotState.Ok ? Sky : Orange));
            if (snap.Windows.Count == 0) continue;

            var grid = new Grid { ColumnSpacing = 4, RowSpacing = 8 };
            for (int c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < snap.Windows.Count; i++)
            {
                if (i % 3 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var dial = Dial(snap.Windows[i]);
                Grid.SetRow(dial, i / 3);
                Grid.SetColumn(dial, i % 3);
                grid.Children.Add(dial);
            }
            body.Children.Add(grid);

            // [Part 250] 값을 하나도 모르면(예: Cursor 무료) 「쓴 양이 적어」 같은 예측 문장을 붙이지 않는다 — 사실이 아니다
            if (snap.Windows.All(w => w.UsedPercent is null)) continue;
            var urgent = Pace.MostUrgent(snap.Windows, now);
            if (urgent is not null)
            {
                var early = urgent.ResetsAt - urgent.EmptyAt;
                body.Children.Add(Notice(
                    S.T($"지금 속도면 {urgent.Window.Label} 한도가 {When(urgent.EmptyAt)}쯤 바닥납니다. 초기화({When(urgent.ResetsAt)})보다 {Span(early)} 빠릅니다.",
                        $"At this pace your {urgent.Window.Label} limit runs out around {When(urgent.EmptyAt)}, {Span(early)} before it resets ({When(urgent.ResetsAt)})."),
                    Orange));
            }
            else if (Pace.HasAnyEstimate(snap.Windows, now))
            {
                body.Children.Add(Notice(S.T("지금 속도면 초기화 전까지 여유가 있습니다.", "At this pace you have room until the reset."), Sky));
            }
            else
            {
                body.Children.Add(Notice(S.T("아직 쓴 양이 적어 소진 시각을 예측하지 않습니다.", "Too little usage yet to predict when it runs out."), Sky));
            }
        }
        body.Children.Add(Caption(S.T("예측은 이 창이 시작된 뒤의 평균 속도가 그대로 이어진다고 가정한 값입니다.",
            "Predictions assume your average pace since the window started continues.")));
    }

    // 시안 C — 남은 양을 배터리 10칸으로(PureBattery 색 기준), 아래에 다음 7일 초기화 시점
    private void RenderBattery(IReadOnlyList<UsageSnapshot> snapshots)
    {
        var all = new List<(string Tool, UsageWindow Window)>();
        foreach (var snap in snapshots)
        {
            body.Children.Add(ToolHeader(snap));
            if (snap.Message is not null) body.Children.Add(Notice(snap.Message, snap.State == SnapshotState.Ok ? Sky : Orange));
            foreach (var w in snap.Windows)
            {
                body.Children.Add(BatteryRow(w));
                all.Add((snap.Tool, w));
            }
        }
        if (all.Count == 0) return;

        body.Children.Add(Label(S.T("다음 7일 · 다시 차는 시점", "Next 7 days · when limits refill")));
        body.Children.Add(Timeline(all));

        // [Part 253] 7일 밖에서 다시 차는 한도(예: Codex 30일)는 타임라인에서 말없이 빠졌다 — 글로 적는다
        var later = all.Where(i => i.Window.ResetsAt is { } r && r > DateTimeOffset.Now.Date.AddDays(8))
            .OrderBy(i => i.Window.ResetsAt).ToList();
        if (later.Count > 0)
        {
            string list = string.Join(" · ", later.Select(i => $"{PinName(i.Tool, i.Window.Label)} {When(i.Window.ResetsAt!.Value)}"));
            body.Children.Add(Caption(S.T($"7일 뒤에 다시 참: {list}", $"Refills after 7 days: {list}")));
        }
    }

    // [Part 253] 설정 화면 개선 — 구역마다 카드 · 시안 글자(A·B·C·D) 제거 · 도구별 「표시」 스위치 · 스트립 글자 뜻.
    // [Part 254] 사용자 「설정 화면 좀 더 짧게」 — 한 화면에 들어가게: 제목 제거 · 계정 한 줄 · 라디오 3줄 → 콤보 한 줄 · 시간 표기 콤보 · 설명 한 줄씩.
    // 규칙은 Part 247 그대로: 선택·스위치 처리기는 값이 같으면 무시하고, 처리기 안에서 화면(Render)을 다시 만들지 않는다.
    private void RenderSettings()
    {
        var saveFailed = Notice(S.T("설정을 저장하지 못했습니다. 다음 실행 때는 기본값으로 돌아갑니다.",
            "Couldn't save the setting. It will reset next launch."), Orange);
        saveFailed.Visibility = Visibility.Collapsed;
        void Save() => saveFailed.Visibility = settings.Save() ? Visibility.Collapsed : Visibility.Visible;

        // ── 계정 · 표시 ──
        // [Part 249] 로그인은 공식 CLI·앱이 한다. AiMeter 는 상태를 보여 주고 로그인 창을 띄울 뿐이다.
        body.Children.Add(Label(S.T("계정 · 표시 (끄면 팝오버·작업 표시줄·트레이에서 숨김)", "Accounts · Visibility (off hides it everywhere)")));
        var accounts = new List<FrameworkElement>();
        foreach (var snap in usage.All)
        {
            string tool = snap.Tool;
            bool installed = snap.State != SnapshotState.NotInstalled;
            string state = snap.State switch
            {
                SnapshotState.Ok => snap.Plan ?? S.T("로그인됨", "Signed in"),
                SnapshotState.NotSignedIn => S.T("로그인 필요", "Sign-in needed"),
                SnapshotState.TokenExpired => S.T("다시 로그인 필요", "Sign in again"),
                SnapshotState.NotInstalled => S.T("설치 안 됨", "Not installed"),
                _ => S.T("읽기 실패", "Couldn't load"),
            };

            var row = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } },
            };
            // 이름과 상태를 한 줄에 — 「Claude Code  Max 5x」
            var line = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var name = new Microsoft.UI.Xaml.Documents.Run { Text = tool, FontWeight = FontWeights.SemiBold, Foreground = Brush(Text1) };
            line.Inlines.Add(name);
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = "  " + state, FontSize = 11,
                Foreground = Brush(snap.State is SnapshotState.Ok or SnapshotState.NotInstalled ? Text2 : Orange),
            });
            if (snap.State is not (SnapshotState.Ok or SnapshotState.NotInstalled) && snap.Message is not null) ToolTipService.SetToolTip(line, snap.Message);
            row.Children.Add(line);

            var failed = Notice(S.T($"{tool} 를 실행하지 못했습니다. 설치돼 있는지 확인하세요.", $"Couldn't start {tool}. Check that it is installed."), Orange);
            failed.Visibility = Visibility.Collapsed;
            if (installed)
            {
                // Claude·Codex 는 CLI 로그인 명령, Cursor·Antigravity 는 앱을 연다(로그인은 그 앱에서)
                var login = new Button
                {
                    Content = tool is "Claude Code" or "Codex" ? S.T("로그인", "Sign in") : S.T("앱 열기", "Open app"),
                    FontSize = 12,
                    Width = 72,
                    Padding = new Thickness(0, 3, 0, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(login, S.T($"{tool} 로그인 창 열기", $"Open {tool} sign-in"));
                ToolTipService.SetToolTip(login, S.T("로그인을 마치면 새로고침(⟳)을 누르세요. AiMeter 는 로그인 정보를 읽기만 합니다.", "After signing in, press refresh (⟳). AiMeter only reads credentials."));
                login.Click += (_, _) => failed.Visibility = LoginLauncher.Start(tool) ? Visibility.Collapsed : Visibility.Visible;
                Grid.SetColumn(login, 1);
                row.Children.Add(login);

                // [Part 253] 표시 스위치 — 끄면 팝오버·스트립·링 아이콘에서 빠진다(예: Free 플랜이라 볼 것이 없는 도구)
                var show = Switch(!settings.IsHidden(tool), S.T($"{tool} 표시", $"Show {tool}"), on =>
                {
                    if (on == !settings.IsHidden(tool)) return;
                    if (on) settings.HiddenTools.Remove(tool); else settings.HiddenTools.Add(tool);
                    Save();
                    name.Foreground = Brush(on ? Text1 : Text2);
                    ToolVisibilityChanged?.Invoke();
                });
                Grid.SetColumn(show, 2);
                row.Children.Add(show);
                if (settings.IsHidden(tool)) name.Foreground = Brush(Text2);
            }

            var cell = new StackPanel { Spacing = 6 };
            cell.Children.Add(row);
            cell.Children.Add(failed);
            accounts.Add(cell);
        }
        body.Children.Add(Card(accounts)); // [Part 254] 아래 설명 두 줄은 구역 제목으로 올리고 뺐다(로그인 뒤 새로고침 안내는 로그인 버튼 툴팁)

        // ── 화면 · 시간 표기 ──
        body.Children.Add(Label(S.T("화면 · 표기", "View · Format")));
        string[] views = { SettingsStore.ViewGauge, SettingsStore.ViewBattery, SettingsStore.ViewSpend };
        string[] resets = { SettingsStore.ResetAuto, SettingsStore.ResetRemain, SettingsStore.ResetClock, SettingsStore.ResetBoth };
        string[] dates = { SettingsStore.DateShort, SettingsStore.DateLong };
        string[] bases = { SettingsStore.BasisRemaining, SettingsStore.BasisUsed };
        body.Children.Add(Card(new List<FrameworkElement>
        {
            ComboRow(S.T("열었을 때 화면", "Opens to"),
                new[] { S.T("계기판 — 언제 바닥나나", "Gauges — when it runs out"), S.T("배터리 — 언제 다시 차나", "Battery — when it refills"), S.T("트리맵 — 어디에 썼나", "Treemap — where it went") },
                Array.IndexOf(views, settings.View), i =>
                {
                    if (views[i] == settings.View) return;
                    settings.View = views[i];
                    Save();
                }),
            // [Part 254] 게이지 기준 — 팝오버·스트립·링 아이콘 모두
            ComboRow(S.T("게이지 기준", "Gauges show"),
                new[] { S.T("남은 양 · 78% 남음", "Remaining · 78% left"), S.T("쓴 양 · 22% 사용", "Used · 22% used") },
                Array.IndexOf(bases, settings.GaugeBasis), i =>
                {
                    if (bases[i] == settings.GaugeBasis) return;
                    settings.GaugeBasis = bases[i];
                    Save();
                    GaugeBasisChanged?.Invoke();
                }),
            ComboRow(S.T("초기화 표기", "Reset time"),
                new[] { S.T("자동 (하루 안은 남은 시간)", "Auto (countdown within a day)"), S.T("남은 시간 · 1시간 34분 후", "Countdown · in 1h 34m"), S.T("시각 · 토 19:00", "Clock · Sat 19:00"), S.T("둘 다 · 토 19:00 · 2일 후", "Both · Sat 19:00 · in 2d") },
                Array.IndexOf(resets, settings.ResetStyle), i =>
                {
                    if (resets[i] == settings.ResetStyle) return;
                    settings.ResetStyle = resets[i];
                    Save();
                }),
            ComboRow(S.T("먼 날짜", "Far dates"),
                new[] { S.T("11-6(금)", "Nov 6"), S.T("11월 6일(금)", "November 6") },
                Array.IndexOf(dates, settings.DateStyle), i =>
                {
                    if (dates[i] == settings.DateStyle) return;
                    settings.DateStyle = dates[i];
                    Save();
                }),
        }));

        // ── 작업 표시줄 · 기타 ──
        body.Children.Add(Label(S.T("작업 표시줄 · 기타", "Taskbar · Other")));
        var startupFailed = Notice(S.T("시작 프로그램 등록을 바꾸지 못했습니다.", "Couldn't change the startup setting."), Orange);
        startupFailed.Visibility = Visibility.Collapsed;
        body.Children.Add(Card(new List<FrameworkElement>
        {
            SwitchRow(S.T("작업 표시줄 사용량 줄", "Taskbar usage strip"),
                S.T("C·X·A·Cu = Claude·Codex·Antigravity·Cursor", "C·X·A·Cu = Claude·Codex·Antigravity·Cursor"),
                settings.TaskbarStrip, on =>
                {
                    if (on == settings.TaskbarStrip) return;
                    settings.TaskbarStrip = on;
                    Save();
                    TaskbarStripChanged?.Invoke();
                }),
            SwitchRow(S.T("Windows 시작 때 자동 실행", "Start with Windows"), null,
                StartupService.IsEnabled(), on =>
                {
                    if (on == StartupService.IsEnabled()) return;
                    startupFailed.Visibility = StartupService.Set(on) ? Visibility.Collapsed : Visibility.Visible;
                }),
            SwitchRow(S.T("트리맵용 Claude Code 기록 읽기", "Read Claude Code logs for treemap"), LogsShortText(),
                settings.ReadLocalLogs, on =>
                {
                    if (on == settings.ReadLocalLogs) return;
                    settings.ReadLocalLogs = on;
                    Save();
                    if (on) StartScan(force: true);
                }),
        }));
        body.Children.Add(startupFailed);
        body.Children.Add(saveFailed);
    }

    /// <summary>[Part 254] 설정 화면용 한 줄 개인정보 안내 — 전문은 트리맵 화면(LogsPrivacyText)에 그대로 있다.</summary>
    private static string LogsShortText() => S.T("토큰 수만 셉니다. 대화 내용은 저장·전송하지 않습니다.", "Counts tokens only. Conversation content is never stored or sent.");

    /// <summary>[Part 254] 「이름 ··· [콤보]」 한 줄. 처리기는 사용자가 고른 경우에만 — 로드 때 오는 이벤트는 값이 같아 무시된다(Part 247).</summary>
    private static FrameworkElement ComboRow(string text, string[] items, int selected, Action<int> changed)
    {
        var grid = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        grid.Children.Add(new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(Text1) });
        var combo = new ComboBox { FontSize = 12, MinWidth = 0, Width = 196, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in items) combo.Items.Add(item);
        combo.SelectedIndex = Math.Max(0, selected);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, text);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex); };
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);
        return grid;
    }

    /// <summary>[Part 253] 설정 구역 카드 — 둥근 면 안에 항목을 얇은 줄로 나눈다.</summary>
    private static FrameworkElement Card(IReadOnlyList<FrameworkElement> items)
    {
        var stack = new StackPanel();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = Brush(Line), Margin = new Thickness(0, 7, 0, 7) }); // [Part 254] 10 → 7 (설정 화면 줄이기)
            stack.Children.Add(items[i]);
        }
        return new Border
        {
            Child = stack,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(8),
            Background = Brush(Windows.UI.Color.FromArgb(0x99, Surface.R, Surface.G, Surface.B)),
            BorderBrush = Brush(Line),
            BorderThickness = new Thickness(1),
        };
    }

    private static FrameworkElement SwitchRow(string text, string? description, bool isOn, Action<bool> changed)
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        var texts = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brush(Text1) });
        if (description is not null) texts.Children.Add(new TextBlock { Text = description, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brush(Text2), LineHeight = 16 });
        grid.Children.Add(texts);
        var toggle = Switch(isOn, text, changed);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }

    private static ToggleSwitch Switch(bool isOn, string name, Action<bool> changed)
    {
        // 글자 없는 스위치 — ToggleSwitch 는 오른쪽에 글자 자리를 남기므로 음수 여백으로 당긴다
        var toggle = new ToggleSwitch { IsOn = isOn, OnContent = "", OffContent = "", MinWidth = 0, Margin = new Thickness(8, 0, -12, 0), VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, name);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    private static string LogsPrivacyText() => S.T(
        "응답마다 남는 토큰 수·시각·응답 id 만 셉니다. 대화 내용은 저장·기록·전송하지 않습니다. 끄면 바로 읽기를 멈춥니다.",
        "Only per-response token counts, times and response ids are counted. Conversation content is never stored, logged or sent. Turning it off stops reading immediately.");

    // ── 시안 D · 도구별 트리맵 ───────────────────────────────────

    private void StartScan(bool force)
    {
        if (!settings.ReadLocalLogs) return;
        if (!force && scanner.Last is { } last && DateTimeOffset.UtcNow - last.ScannedAt < TimeSpan.FromMinutes(5)) return;
        scanner.ScanAsync().ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
        {
            if (isVisible && !showingSettings && settings.View == SettingsStore.ViewSpend) Render();
        }));
    }

    private void RenderSpend(IReadOnlyList<UsageSnapshot> snapshots)
    {
        body.Children.Add(Label(S.T("이번 달 · 어디에 썼나", "This month · where it went")));
        if (!settings.ReadLocalLogs)
        {
            body.Children.Add(Notice(S.T(
                "이 화면은 Claude Code 가 이 PC 에 남긴 기록(사용자 폴더의 .claude\\projects)에서 토큰 수를 세어 보여 줍니다. 사용량 API 에는 이 값이 없습니다.",
                "This view counts tokens from the logs Claude Code keeps on this PC (.claude\\projects in your user folder). The usage API does not provide them."), Sky));
            body.Children.Add(Caption(LogsPrivacyText()));
            var enable = new Button { Content = S.T("기록 읽기 켜기", "Turn on log reading") };
            enable.Click += (_, _) =>
            {
                settings.ReadLocalLogs = true;
                settings.Save();
                StartScan(force: true);
                Render(); // 버튼 클릭 처리기 — 로드 때 저절로 오는 이벤트가 아니라 루프가 생기지 않는다
            };
            body.Children.Add(enable);
            return;
        }

        var totals = scanner.Last;
        if (totals is null)
        {
            body.Children.Add(Caption(S.T("기록을 집계하는 중…", "Counting logs…")));
            StartScan(force: false);
            return;
        }

        // [Part 248] AI별 / 모델별 — 사용자가 고른다. 버튼 클릭 처리기에서만 다시 그린다(로드 때 저절로 오는 이벤트가 아님 → 루프 없음).
        bool byModel = settings.SpendBy == SettingsStore.SpendByModel;
        var switcher = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (key, text) in new[] { (SettingsStore.SpendByTool, S.T("AI별", "By AI")), (SettingsStore.SpendByModel, S.T("모델별", "By model")) })
        {
            bool selected = settings.SpendBy == key;
            var b = new Button
            {
                Content = text,
                FontSize = 12,
                Padding = new Thickness(12, 4, 12, 4),
                Background = Brush(selected ? Windows.UI.Color.FromArgb(0x33, Accent.R, Accent.G, Accent.B) : Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderBrush = Brush(selected ? Accent : Line),
                BorderThickness = new Thickness(1),
            };
            b.Click += (_, _) =>
            {
                if (settings.SpendBy == key) return;
                settings.SpendBy = key;
                settings.Save();
                Render();
            };
            switcher.Children.Add(b);
        }
        body.Children.Add(switcher);

        // 지금 로컬 기록을 셀 수 있는 도구는 Claude Code 뿐 — 없는 도구·모델을 꾸며 넣지 않는다.
        var claude = snapshots.FirstOrDefault(s => s.Tool == "Claude Code");
        var tiles = new List<TileItem>();
        if (claude is not null && totals.All > 0)
        {
            if (!byModel)
            {
                tiles.Add(new TileItem(claude.Tool, claude.Plan, totals.All, Hex("#D97757"), Tightest(claude.Windows)));
            }
            else
            {
                // 같은 도구의 모델은 도구 색 계열에서 진하기만 다르게(많이 쓴 모델이 가장 진하다)
                var models = totals.ByModel.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList();
                for (int i = 0; i < models.Count; i++)
                {
                    string name = ClaudeLogScanner.DisplayName(models[i].Key);
                    // 그 모델 전용 주간 한도가 있으면(예: 주간 Fable) 그 값, 없으면 도구 전체에서 가장 빠듯한 값
                    var scoped = claude.Windows.FirstOrDefault(w => w.Key.StartsWith("weekly:", StringComparison.Ordinal)
                        && name.StartsWith(w.Key["weekly:".Length..], StringComparison.OrdinalIgnoreCase));
                    double? remaining = scoped?.RemainingPercent ?? Tightest(claude.Windows);
                    tiles.Add(new TileItem(name, "CLAUDE CODE", models[i].Value, Shade(Hex("#D97757"), i, models.Count), remaining));
                }
            }
        }
        // [Part 253] 사용자: 「CD에는 chatgpt가 표기 안되는듯」 — 다른 도구는 말없이 빠져 있었다(맨 아래 작은 글씨뿐).
        //   토큰 수를 셀 근거가 없는 도구를 넓이로 꾸며 넣지 않고, 빠졌다는 사실과 이유를 맨 위에 적는다.
        //   Codex: ~/.codex/sessions 에 기록은 남지만 이 PC 의 기록에 토큰 줄이 없어 형식을 확인하지 못했다(Part 251) · Cursor·Antigravity: 로컬 토큰 기록 없음
        var others = snapshots.Where(s => s.Tool != "Claude Code").Select(s => s.Tool).ToList();
        if (others.Count > 0)
        {
            string names = string.Join(" · ", others);
            body.Children.Add(Notice(S.T($"{names} 는 이 화면에 없습니다 — 토큰 수를 셀 기록을 아직 읽지 못합니다. 남은 한도는 계기판·배터리 화면에서 보세요.",
                $"{names} are not shown here — their token records can't be read yet. See remaining limits in the gauge or battery view."), Sky));
        }

        if (tiles.Count == 0)
        {
            body.Children.Add(Caption(S.T("이번 달 기록에 토큰 사용이 없습니다.", "No token usage in this month's logs.")));
            return;
        }
        // 3% 미만 조각은 타일로 그리면 이름도 안 보이고, 최소 크기를 주면 넓이가 거짓말을 한다 → 「그 밖」 한 줄로 적는다
        long sum = tiles.Sum(t => t.Tokens);
        var main = tiles.Where(t => t.Tokens * 100.0 / sum >= 3).ToList();
        var minor = tiles.Except(main).OrderByDescending(t => t.Tokens).ToList();
        if (main.Count == 0) { main = tiles; minor.Clear(); }
        body.Children.Add(Treemap(main, sum));
        if (minor.Count > 0)
        {
            string list = string.Join(" · ", minor.Select(t => $"{t.Title} {Tokens(t.Tokens)}"));
            body.Children.Add(Caption(S.T($"그 밖(각 3% 미만): {list}", $"Others (each under 3%): {list}")));
        }
        body.Children.Add(Caption(showUsed
            ? S.T("넓이 = 이번 달 처리한 토큰(캐시 포함) 비중 · 아래 띠 = 쓴 한도(모델 전용 한도가 없으면 가장 빠듯한 한도)",
                "Area = share of tokens processed this month (incl. cache) · bottom band = limit used (tightest one if the model has no limit of its own)")
            : S.T("넓이 = 이번 달 처리한 토큰(캐시 포함) 비중 · 아래 띠 = 남은 한도(모델 전용 한도가 없으면 가장 빠듯한 한도)",
                "Area = share of tokens processed this month (incl. cache) · bottom band = remaining limit (tightest one if the model has no limit of its own)")));
        body.Children.Add(Caption(S.T(
            $"입력 {Tokens(totals.Input)} · 출력 {Tokens(totals.Output)} · 캐시 생성 {Tokens(totals.CacheCreate)} · 캐시 읽기 {Tokens(totals.CacheRead)} · 응답 {totals.Messages:N0}개",
            $"Input {Tokens(totals.Input)} · output {Tokens(totals.Output)} · cache write {Tokens(totals.CacheCreate)} · cache read {Tokens(totals.CacheRead)} · {totals.Messages:N0} responses")));
        var age = DateTimeOffset.UtcNow - totals.ScannedAt;
        body.Children.Add(Caption(age.TotalMinutes < 1
            ? S.T("지금은 Claude Code 만 집계합니다 · 방금 집계", "Only Claude Code is counted for now · counted just now")
            : S.T($"지금은 Claude Code 만 집계합니다 · {Span(age)} 전 집계", $"Only Claude Code is counted for now · counted {Span(age)} ago")));
    }

    private sealed record TileItem(string Title, string? Sub, long Tokens, Windows.UI.Color Color, double? Remaining);

    private static double? Tightest(IEnumerable<UsageWindow> windows)
    {
        var known = windows.Where(w => w.RemainingPercent is not null).Select(w => w.RemainingPercent!.Value).ToList();
        return known.Count > 0 ? known.Min() : null;
    }

    /// <summary>기준색에서 순위가 낮을수록 흰색 쪽으로 옅게.</summary>
    private static Windows.UI.Color Shade(Windows.UI.Color c, int index, int count)
    {
        if (count <= 1) return c;
        double t = 0.45 * index / (count - 1);
        byte Mix(byte v) => (byte)Math.Round(v + (255 - v) * t);
        return Windows.UI.Color.FromArgb(0xFF, Mix(c.R), Mix(c.G), Mix(c.B));
    }

    /// <param name="total">비중의 분모(그리지 않는 「그 밖」 조각까지 포함한 전체)</param>
    private static FrameworkElement Treemap(List<TileItem> tiles, long total)
    {
        var sorted = tiles.OrderByDescending(t => t.Tokens).ToList();
        long drawn = sorted.Sum(t => t.Tokens);
        int rowCount = sorted.Count <= 2 ? 1 : 2;
        var rows = Enumerable.Range(0, rowCount).Select(_ => new List<TileItem>()).ToList();
        double acc = 0;
        foreach (var t in sorted)
        {
            rows[Math.Min((int)(acc / (drawn / (double)rowCount)), rowCount - 1)].Add(t);
            acc += t.Tokens;
        }

        var panel = new StackPanel { Spacing = 3 };
        foreach (var row in rows.Where(r => r.Count > 0))
        {
            // 줄 높이도 그 줄의 몫에 비례(넓이 = 비중). 글자가 들어갈 최소 높이만 보장한다
            double rowShare = row.Sum(t => t.Tokens) / (double)drawn;
            var grid = new Grid { ColumnSpacing = 3, Height = rowCount == 1 ? 112 : Math.Max(64, 180 * rowShare) };
            foreach (var item in row)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(item.Tokens, GridUnitType.Star) });
                var tile = Tile(item, total);
                Grid.SetColumn(tile, grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(tile);
            }
            panel.Children.Add(grid);
        }
        return panel;
    }

    private static FrameworkElement Tile(TileItem item, long total)
    {
        double share = item.Tokens * 100.0 / total;
        var content = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition(), new RowDefinition { Height = GridLength.Auto } } };
        var head = new StackPanel { Padding = new Thickness(10, 8, 10, 0) };
        head.Children.Add(new TextBlock { Text = item.Title, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brush(Colors.White), TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.Sub is not null) head.Children.Add(new TextBlock { Text = item.Sub.ToUpper(CultureInfo.InvariantCulture), FontSize = 11, CharacterSpacing = 60, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush(Windows.UI.Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)) });
        content.Children.Add(head);

        var value = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(10, 0, 10, 6), VerticalAlignment = VerticalAlignment.Bottom };
        value.Children.Add(new TextBlock { Text = share < 1 ? "<1%" : $"{share:0}%", FontSize = 16, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Foreground = Brush(Colors.White) });
        value.Children.Add(new TextBlock { Text = S.T($"{Tokens(item.Tokens)} 토큰", $"{Tokens(item.Tokens)} tokens"), FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 2), Foreground = Brush(Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)) });
        Grid.SetRow(value, 1);
        content.Children.Add(value);

        // 아래 띠 = 한도(게이지 기준 — [Part 254]). 모르면 채우지 않는다. 색은 남은 양.
        var band = new Grid { Height = 5, Background = Brush(Windows.UI.Color.FromArgb(0x47, 0, 0, 0)) };
        if (item.Remaining is double remaining && Shown(remaining) is double fill)
        {
            band.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fill, 0.01), GridUnitType.Star) });
            band.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - fill, 0.01), GridUnitType.Star) });
            band.Children.Add(new Border { Background = Brush(LevelColor(remaining)) });
        }
        Grid.SetRow(band, 2);
        content.Children.Add(band);

        var border = new Border { Background = Brush(item.Color), CornerRadius = new CornerRadius(6), Child = content };
        ToolTipService.SetToolTip(border, S.T($"{item.Title} — 이번 달 {Tokens(item.Tokens)} 토큰 ({share:0.#}%)", $"{item.Title} — {Tokens(item.Tokens)} tokens this month ({share:0.#}%)"));
        return border;
    }

    private static string Tokens(long n)
    {
        if (S.IsKorean)
        {
            if (n >= 100_000_000) return $"{n / 100_000_000.0:0.##}억";
            if (n >= 10_000) return $"{n / 10_000.0:0.#}만";
            return n.ToString("N0", CultureInfo.CurrentCulture);
        }
        if (n >= 1_000_000_000) return $"{n / 1_000_000_000.0:0.##}B";
        if (n >= 1_000_000) return $"{n / 1_000_000.0:0.#}M";
        if (n >= 1_000) return $"{n / 1_000.0:0.#}K";
        return n.ToString("N0", CultureInfo.InvariantCulture);
    }

    // ── 부품 ────────────────────────────────────────────────

    private static FrameworkElement ToolHeader(UsageSnapshot snap)
    {
        string text = snap.Plan is null ? snap.Tool : $"{snap.Tool} · {snap.Plan}";
        return new TextBlock
        {
            Text = text.ToUpper(CultureInfo.InvariantCulture),
            FontSize = 11,
            CharacterSpacing = 80,
            Foreground = Brush(Text2),
            Margin = new Thickness(0, 4, 0, 0),
        };
    }

    private static FrameworkElement Dial(UsageWindow w)
    {
        double? remain = w.RemainingPercent;
        double? shown = Shown(remain); // [Part 254] 바늘·호·숫자는 게이지 기준, 색은 남은 양
        var canvas = new Canvas { Width = 120, Height = 66, HorizontalAlignment = HorizontalAlignment.Center };

        for (int t = 0; t <= 10; t++)
        {
            double a = Math.PI * (1 - t / 10.0);
            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 60 + 52 * Math.Cos(a), Y1 = 60 - 52 * Math.Sin(a),
                X2 = 60 + 56 * Math.Cos(a), Y2 = 60 - 56 * Math.Sin(a),
                Stroke = Brush(Hex("#475569")), StrokeThickness = 1.2,
            });
        }

        var track = Arc(1, Brush(Track));
        if (remain is null) track.StrokeDashArray = new DoubleCollection { 1.2, 0.8 }; // 「모름」은 점선 — 0 으로 칠하지 않는다
        canvas.Children.Add(track);

        if (remain is double rem && shown is double r)
        {
            if (r > 0.5) canvas.Children.Add(Arc(r / 100, Brush(LevelColor(rem))));
            double a = Math.PI * (1 - r / 100);
            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 60, Y1 = 60, X2 = 60 + 36 * Math.Cos(a), Y2 = 60 - 36 * Math.Sin(a),
                Stroke = Brush(Text1), StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
            var hub = new Ellipse { Width = 8, Height = 8, Fill = Brush(Text1) };
            Canvas.SetLeft(hub, 56);
            Canvas.SetTop(hub, 56);
            canvas.Children.Add(hub);
        }

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(canvas);
        panel.Children.Add(new TextBlock
        {
            Text = shown is double rr ? $"{rr:0}%" : S.T("모름", "Unknown"),
            FontSize = 14, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = Brush(remain is null ? Text2 : Text1), HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(Small(showUsed ? S.T($"{w.Label} 사용", $"{w.Label} used") : S.T($"{w.Label} 남음", $"{w.Label} left"), Text2));
        if (w.ResetsAt is { } reset) panel.Children.Add(Small(Until(reset), Text2));
        return panel;
    }

    /// <summary>반원(왼쪽→오른쪽)의 0~fraction 구간.</summary>
    private static Path Arc(double fraction, Brush stroke)
    {
        static Point P(double f) => new(60 + 46 * Math.Cos(Math.PI * (1 - f)), 60 - 46 * Math.Sin(Math.PI * (1 - f)));
        var figure = new PathFigure { StartPoint = P(0), IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = P(Math.Clamp(fraction, 0, 0.9999)),
            Size = new Size(46, 46),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = false,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return new Path
        {
            Data = geometry, Stroke = stroke, StrokeThickness = 8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
    }

    private static FrameworkElement BatteryRow(UsageWindow w)
    {
        double? remain = w.RemainingPercent;
        double? shown = Shown(remain); // [Part 254] 칸 수·숫자는 게이지 기준, 색은 남은 양
        var grid = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(120) },
                new ColumnDefinition(),
                new ColumnDefinition { Width = new GridLength(48) },
            },
        };

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = w.Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brush(Text1), TextTrimming = TextTrimming.CharacterEllipsis });
        if (w.ResetsAt is { } reset) label.Children.Add(Small(Until(reset), Text2, HorizontalAlignment.Left));
        grid.Children.Add(label);

        var cells = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        int filled = shown is double r ? (int)Math.Round(r / 10) : 0;
        for (int i = 0; i < 10; i++)
        {
            cells.Children.Add(new Border
            {
                Width = 12, Height = 16, CornerRadius = new CornerRadius(2),
                Background = Brush(remain is double rv && i < filled ? LevelColor(rv) : Track),
                BorderBrush = remain is null ? Brush(Hex("#475569")) : null,
                BorderThickness = remain is null ? new Thickness(1) : new Thickness(0),
            });
        }
        var battery = new Border
        {
            Child = cells, Padding = new Thickness(3), CornerRadius = new CornerRadius(5),
            BorderBrush = Brush(Windows.UI.Color.FromArgb(0x73, 0x94, 0xA3, 0xB8)), BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(battery, 1);
        grid.Children.Add(battery);

        var value = new TextBlock
        {
            Text = shown is double v ? $"{v:0}%" : S.T("모름", "?"),
            FontWeight = FontWeights.Bold, FontSize = 13, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = Brush(remain is double c ? LevelColor(c) : Text2),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        return grid;
    }

    private static FrameworkElement Timeline(List<(string Tool, UsageWindow Window)> items)
    {
        const double width = PopWidth - 28 - 20;
        var canvas = new Canvas { Width = width, Height = 62, Margin = new Thickness(10, 0, 10, 0) };
        double axisY = 40;
        canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = 0, Y1 = axisY, X2 = width, Y2 = axisY, Stroke = Brush(Windows.UI.Color.FromArgb(0x59, 0x94, 0xA3, 0xB8)), StrokeThickness = 1 });

        var nowLocal = DateTimeOffset.Now;
        var startOfToday = new DateTimeOffset(nowLocal.Date, nowLocal.Offset);
        for (int d = 0; d <= 7; d++)
        {
            string name = d == 0 ? S.T("오늘", "Today") : startOfToday.AddDays(d).ToString("ddd", CultureInfo.CurrentCulture);
            var tb = Small(name, Text2);
            tb.Width = 36;
            tb.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(tb, d / 7.0 * width - 18);
            Canvas.SetTop(tb, axisY + 4);
            canvas.Children.Add(tb);
        }

        var nowMark = new Rectangle { Width = 2, Height = 18, Fill = Brush(Accent) };
        Canvas.SetLeft(nowMark, Fraction(nowLocal) * width - 1);
        Canvas.SetTop(nowMark, axisY - 10);
        canvas.Children.Add(nowMark);

        // [Part 252] 도구가 늘자 비슷한 시각에 초기화되는 한도가 몰려 이름표가 포개졌다 → 가까운 점(60dip 안)은 이름표 하나로 「첫 이름 외 n」.
        //   어느 도구의 「5시간」 인지 헷갈리지 않게 도구 이름을 붙인다(Antigravity 는 이미 Gemini·Claude·GPT 가 붙어 있다).
        var pins = items.Where(i => i.Window.ResetsAt is not null)
            .Select(i => (Tool: i.Tool, W: i.Window, X: Fraction(i.Window.ResetsAt!.Value.ToLocalTime()) * width))
            .Where(p => p.X >= 0 && p.X <= width)
            .OrderBy(p => p.X)
            .ToList();
        int lane = 0;
        for (int i = 0; i < pins.Count;)
        {
            int j = i;
            while (j + 1 < pins.Count && pins[j + 1].X - pins[i].X < 60) j++;
            for (int k = i; k <= j; k++)
            {
                var color = pins[k].W.RemainingPercent is double r ? LevelColor(r) : Text2;
                var pin = new Ellipse { Width = 11, Height = 11, Fill = Brush(color), Stroke = Brush(Hex("#0B1220")), StrokeThickness = 2 };
                Canvas.SetLeft(pin, pins[k].X - 5.5);
                Canvas.SetTop(pin, axisY - 5.5);
                canvas.Children.Add(pin);
            }
            string first = PinName(pins[i].Tool, pins[i].W.Label);
            string text = j > i ? S.T($"{first} 외 {j - i}", $"{first} +{j - i}") : first;
            var tag = Small(text, Text1, HorizontalAlignment.Left);
            ToolTipService.SetToolTip(tag, string.Join(Environment.NewLine, pins.Skip(i).Take(j - i + 1).Select(p => PinName(p.Tool, p.W.Label))));
            Canvas.SetLeft(tag, Math.Min(pins[i].X - 4, width - 110));
            Canvas.SetTop(tag, axisY - 22 - (lane % 2) * 16);
            canvas.Children.Add(tag);
            lane++;
            i = j + 1;
        }
        return canvas;

        double Fraction(DateTimeOffset t) => (t - startOfToday).TotalDays / 7.0;
    }

    private static string PinName(string tool, string label) => tool switch
    {
        "Claude Code" => $"Claude {label}",
        "Codex" or "Cursor" => $"{tool} {label}",
        _ => label,
    };

    private static Windows.UI.Color LevelColor(double remain) =>
        remain >= 80 ? Lime : remain >= 30 ? Sky : remain >= 10 ? Orange : Red;

    private static FrameworkElement Notice(string text, Windows.UI.Color tone) => new Border
    {
        Padding = new Thickness(12, 9, 12, 9),
        CornerRadius = new CornerRadius(8),
        Background = Brush(Windows.UI.Color.FromArgb(0x14, tone.R, tone.G, tone.B)),
        BorderBrush = Brush(Windows.UI.Color.FromArgb(0x59, tone.R, tone.G, tone.B)),
        BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Brush(Text1), LineHeight = 19 },
    };

    private static TextBlock Label(string text) => new()
    {
        // [Part 253] 자간 80 은 영문 대문자 라벨용 — 한글에 주면 「열 었 을 때」 처럼 어절이 벌어진다
        Text = text, FontSize = 11, CharacterSpacing = S.IsKorean ? 0 : 80, Foreground = Brush(Text2), Margin = new Thickness(0, 6, 0, 0),
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = Brush(Text2), TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock Small(string text, Windows.UI.Color color, HorizontalAlignment align = HorizontalAlignment.Center) => new()
    {
        Text = text, FontSize = 11, Foreground = Brush(color), HorizontalAlignment = align, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static Button IconButton(string glyph, string name)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);
        ToolTipService.SetToolTip(b, name);
        return b;
    }

    // ── 시간 표기 ─────────────────────────────────────────────

    // [Part 254] 시간 표기 설정 — Render() 첫머리에서 settings 값으로 맞춘다(시간 함수들이 static 이라)
    private static string resetStyle = SettingsStore.ResetAuto;
    private static string dateStyle = SettingsStore.DateShort;
    private static bool showUsed;

    /// <summary>[Part 254] 게이지에 그릴 값 — 남은 양 기준이면 남은 %, 쓴 양 기준이면 100 − 남은 %. 모르면 null(0 으로 채우지 않는다).
    /// 색은 이 값이 아니라 늘 남은 양(LevelColor)으로 정한다.</summary>
    private static double? Shown(double? remain) => remain is double r ? (showUsed ? 100 - r : r) : null;

    private static string When(DateTimeOffset t)
    {
        var local = t.ToLocalTime().AddSeconds(30); // 분 단위 반올림 — 서버가 10:00:00 앞뒤 밀리초를 섞어 보내 18:59/19:00 으로 갈렸다
        int days = (local.Date - DateTime.Today).Days;
        if (days == 0) return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        // [Part 250] 6일 넘게 남으면 요일만으로는 「이번 주」 로 읽힌다(Cursor 청구 주기 11/7 이 「토」, 다음 주 수요일이 「수」 로 보였다) → 날짜까지
        if (days >= 6)
        {
            bool longDate = dateStyle == SettingsStore.DateLong; // [Part 254] 11-6(금) / 11월 6일(금)
            // 긴 날짜는 시각을 뺀다 — 계기판 칸에서 「11월 6일(금) 11:01…」 로 잘렸다. 엿새 넘게 남은 초기화는 날짜면 충분하다
            string fmt = S.IsKorean ? (longDate ? "M월 d일(ddd)" : "M/d(ddd) HH:mm") : (longDate ? "MMMM d" : "MMM d HH:mm");
            return local.ToString(fmt, CultureInfo.CurrentCulture);
        }
        return local.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
    }

    private static string Until(DateTimeOffset reset)
    {
        var left = reset - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return S.T("곧 초기화", "Resetting");
        // [Part 254] 사용자가 고른 표기 — auto 는 종전 그대로(24시간 안은 남은 시간, 그 밖은 시각)
        string mode = resetStyle == SettingsStore.ResetAuto ? (left.TotalHours < 24 ? SettingsStore.ResetRemain : SettingsStore.ResetClock) : resetStyle;
        return mode switch
        {
            SettingsStore.ResetRemain => S.T($"{Span(left)} 후 초기화", $"resets in {Span(left)}"),
            SettingsStore.ResetBoth => left.TotalHours < 24
                ? S.T($"{When(reset)} · {Span(left)} 후", $"{When(reset)} · in {Span(left)}")
                : S.T($"{When(reset)} · {(int)left.TotalDays}일 후", $"{When(reset)} · in {(int)left.TotalDays}d"), // [Part 254] 「11-6(금) 10:53 · 29일…」 잘림
            _ => S.T($"{When(reset)} 초기화", $"resets {When(reset)}"),
        };
    }

    private static string Span(TimeSpan t)
    {
        if (t.TotalMinutes < 60) return S.T($"{Math.Max(1, (int)t.TotalMinutes)}분", $"{Math.Max(1, (int)t.TotalMinutes)}m");
        if (t.TotalHours < 24) return S.T($"{(int)t.TotalHours}시간 {t.Minutes}분", $"{(int)t.TotalHours}h {t.Minutes}m");
        return S.T($"{(int)t.TotalDays}일 {t.Hours}시간", $"{(int)t.TotalDays}d {t.Hours}h");
    }

    private static string Ago(DateTimeOffset t)
    {
        var ago = DateTimeOffset.UtcNow - t;
        return ago.TotalMinutes < 1 ? S.T("방금 갱신", "Updated just now") : S.T($"{Span(ago)} 전 갱신", $"Updated {Span(ago)} ago");
    }

    // ── 유틸 ────────────────────────────────────────────────

    private static SolidColorBrush Brush(Windows.UI.Color c) => new(c);

    private static Windows.UI.Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(0xFF,
            byte.Parse(hex[..2], NumberStyles.HexNumber),
            byte.Parse(hex[2..4], NumberStyles.HexNumber),
            byte.Parse(hex[4..6], NumberStyles.HexNumber));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
