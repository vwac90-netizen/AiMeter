using Avalonia;

namespace AiMeter;

// [Part 287] 진입점 — WinUI 의 XAML 생성 Main 대신 Avalonia AppBuilder. 창이 다 숨어도 끝나지 않게(트레이 상주) 명시적 종료만.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 두 번째 실행은 조용히 끝낸다 — 트레이 아이콘이 두 개 생기지 않게
        using var singleInstance = new Mutex(true, "AiMeter.SingleInstance", out bool created);
        if (!created) return 0;
        App.SingleInstance = singleInstance;
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }
}
