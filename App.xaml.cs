using AiMeter.Providers;
using AiMeter.Services;
using AiMeter.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AiMeter;

// [Part 246] 진입점. 메인 창 없이 트레이에 상주하고, 트레이를 누르면 팝오버를 연다.
// [Part 248] 시안 A — 작업 표시줄 스트립(설정으로 켜고 끔)도 누르면 같은 팝오버를 연다.
public partial class App : Application
{
    private Mutex? singleInstance;
    private TrayIconService? tray;
    private TaskbarStrip? strip;
    private PopoverWindow? popover;
    private UsageService? usage;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 두 번째 실행은 조용히 끝낸다 — 트레이 아이콘이 두 개 생기지 않게
        singleInstance = new Mutex(true, "AiMeter.SingleInstance", out bool created);
        if (!created)
        {
            Exit();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var settings = SettingsStore.Load();
        usage = new UsageService(new IUsageProvider[] { new ClaudeProvider(), new CodexProvider(), new CursorProvider(), new AntigravityProvider() }, settings, dispatcher);
        popover = new PopoverWindow(usage, settings, new ClaudeLogScanner());

        tray = new TrayIconService();
        tray.LeftClick += () => popover.Toggle();
        tray.ExitRequested += Shutdown;

        tray.ShowUsed = settings.ShowUsed;
        strip = new TaskbarStrip(dispatcher) { ShowUsed = settings.ShowUsed };
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

        usage.Updated += () =>
        {
            tray.Update(usage.Snapshots);
            strip.Update(usage.Snapshots);
            popover.Render();
        };
        usage.Start();

        // --show: 시작하자마자 팝오버를 연다(설치 직후 확인·점검용)
        if (Environment.GetCommandLineArgs().Contains("--show")) popover.Toggle();
    }

    private void Shutdown()
    {
        strip?.Dispose();
        tray?.Dispose();
        popover?.Close();
        singleInstance?.ReleaseMutex();
        Exit();
    }
}
