using Microsoft.Win32;

namespace AiMeter.Services;

// [Part 249] 시작 프로그램 등록 — HKCU\...\Run 의 「AiMeter」 값(관리자 권한 불필요).
// 상태는 설정 파일이 아니라 레지스트리에서 직접 읽는다 — 사용자가 작업 관리자 「시작 앱」 에서 꺼도 화면이 사실대로 보이게.
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AiMeter";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && string.Equals(v, Command, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <returns>레지스트리에 쓰거나 지우는 데 성공하면 true</returns>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return IsEnabled() == enabled;
        }
        catch
        {
            return false;
        }
    }
}
