using AiMeter.Providers;
using AiMeter.Services;
using AiMeter.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace AiMeter;

// [Part 246] 진입점. 메인 창 없이 트레이에 상주하고, 트레이를 누르면 팝오버를 연다.
// [Part 248] 시안 A — 작업 표시줄 스트립(설정으로 켜고 끔)도 누르면 같은 팝오버를 연다.
// [Part 287] WinUI Application → Avalonia Application(XAML 없이 코드로). 배선은 종전과 같다.
public sealed class App : Application
{
    /// <summary>Program.Main 이 잡은 단일 실행 뮤텍스 — 다시 시작할 때 먼저 놓아야 새 프로세스가 곧바로 끝나지 않는다.</summary>
    internal static Mutex? SingleInstance;

    private IClassicDesktopStyleApplicationLifetime? lifetime;
    private TrayIconService? tray;
    private TaskbarStrip? strip;
    private PopoverWindow? popover;
    private UsageService? usage;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        // [Part 249] 라디오·토글이 Windows 강조색(이 PC 는 분홍)을 따르던 것을 PC Up Check 하늘색으로 고정.
        // [Part 287] Avalonia Fluent 는 Application.Resources 의 SystemAccentColor* 를 보지 않는다(실측: 스위치가 기본 파랑) → 테마 팔레트로 준다
        //   [Part 293] #0284C7 → #38BDF8 — WinUI 다크는 스위치를 Light2(#38BDF8)로 칠했고 Avalonia 는 강조색 그대로 칠한다(0.1.11 실측 대조)
        Styles.Add(new FluentTheme
        {
            Palettes = { [ThemeVariant.Dark] = new ColorPaletteResources { Accent = Color.Parse("#38BDF8") } },
        });
        // [Part 293] WinUI 판과 같은 모양으로(0.1.11 설정 화면 픽셀 실측 · Tools Part 291 과 같은 값) —
        //   켜진 손잡이 검정(Avalonia 기본 흰색) · 꺼진 스위치 바탕 검정 8%·테두리 흰 55%·손잡이 흰 77%(실측 #18202F·#91959D·#CACCCF)
        //   · 콤보·버튼 흰 6% 채움 + 흰 9% 테두리(실측 #283040·#303847, Avalonia 기본 #141A24·#A1A3A7 · 버튼 #4E5563). 바탕을 직접 준 버튼은 영향 없음
        var knobOn = new SolidColorBrush(Colors.Black);
        var fill = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
        var edge = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        var offFill = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        var offStroke = new SolidColorBrush(Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF));
        var offKnob = new SolidColorBrush(Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            Resources["ToggleSwitchKnobFillOn" + state] = knobOn;
            Resources["ToggleSwitchFillOff" + state] = offFill;
            Resources["ToggleSwitchStrokeOff" + state] = offStroke;
            Resources["ToggleSwitchKnobFillOff" + state] = offKnob;
        }
        Resources["ComboBoxBackground"] = fill;
        Resources["ComboBoxBorderBrush"] = edge;
        Resources["ButtonBackground"] = fill;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;

        var settings = SettingsStore.Load();
        S.Apply(settings.Language); // [Part 266] 화면·공급자 문구를 만들기 전에 언어부터
        usage = new UsageService(new IUsageProvider[] { new ClaudeProvider(), new CodexProvider(), new CursorProvider(), new AntigravityProvider() }, settings);
        popover = new PopoverWindow(usage, settings, new ClaudeLogScanner());

        tray = new TrayIconService(this);
        tray.LeftClick += () => popover.Toggle();
        tray.ExitRequested += Shutdown;

        tray.ShowUsed = settings.ShowUsed;
        strip = new TaskbarStrip { ShowUsed = settings.ShowUsed };
        strip.Click += () => popover.Toggle();
        if (settings.TaskbarStrip) strip.Show();
        popover.TaskbarStripChanged += () =>
        {
            if (settings.TaskbarStrip)
            {
                strip.Update(usage.Snapshots);
                strip.Show();
            }
            else
            {
                strip.Hide();
            }
        };

        // [Part 253] 설정에서 도구 「표시」 를 바꿨다 — 다시 읽지 않고 링 아이콘·스트립만 새 목록으로(팝오버는 설정 화면이 열려 있으니 다시 그리지 않는다, Part 247)
        popover.ToolVisibilityChanged += () =>
        {
            tray.Update(usage.Snapshots);
            strip.Update(usage.Snapshots);
            // 여기서 RefreshAsync 를 부르지 않는다 — 공급자가 캐시로 곧바로 끝나면 await 가 동기로 이어져 Updated → Render() 가
            // 스위치 이벤트 처리 도중에 설정 화면을 다시 만든다(Part 247 과 같은 꼴). 다시 켠 도구는 1분 타이머가 새로 읽는다.
        };

        // [Part 254] 게이지 기준이 바뀌었다 — 링 아이콘·스트립을 새 기준으로(팝오버는 설정 화면이 열려 있으니 다시 그리지 않는다)
        popover.GaugeBasisChanged += () =>
        {
            tray.ShowUsed = settings.ShowUsed;
            strip.ShowUsed = settings.ShowUsed;
            tray.Update(usage.Snapshots);
            strip.Update(usage.Snapshots);
        };

        // [Part 266] 언어가 바뀌었다 — 앱을 다시 시작해 팝오버·스트립·트레이 메뉴·받아 온 라벨을 한 번에 새 언어로.
        //   콤보 선택 처리 도중에 창을 닫지 않게 디스패처로 미룬다(Part 247)
        popover.LanguageChanged += () => Dispatcher.UIThread.Post(Restart);

        usage.Updated += () =>
        {
            tray.Update(usage.Snapshots);
            strip.Update(usage.Snapshots);
            popover.Render();
        };
        usage.Start();

        // --show: 시작하자마자 팝오버를 연다(설치 직후 확인·점검용)
        if (Environment.GetCommandLineArgs().Contains("--show")) popover.Toggle();

        base.OnFrameworkInitializationCompleted();
    }

    private void Shutdown()
    {
        Release();
        lifetime?.Shutdown();
    }

    /// <summary>[Part 266] 같은 exe 를 다시 띄우고(팝오버를 바로 연다) 지금 것은 끝낸다. 단일 실행 뮤텍스를 먼저 놓아야 새 프로세스가 곧바로 끝나지 않는다.</summary>
    private void Restart()
    {
        Release();
        try
        {
            if (Environment.ProcessPath is string exe)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--show") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 다시 띄우지 못하면 그냥 끝낸다 — 사용자가 다시 실행하면 새 언어로 시작한다(설정은 이미 저장됨)
        }
        lifetime?.Shutdown();
    }

    private void Release()
    {
        strip?.Dispose();
        tray?.Dispose();
        popover?.CloseForExit();
        SingleInstance?.ReleaseMutex();
        SingleInstance = null; // Program.Main 의 using 이 Dispose 한다
    }
}
