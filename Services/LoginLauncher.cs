using System.Diagnostics;

namespace AiMeter.Services;

// [Part 249] 계정 등록 = 공식 CLI 의 로그인 명령을 「보이는」 콘솔 창으로 띄운다.
// AiMeter 는 자격 증명을 만들거나 쓰거나 갱신하지 않는다 — 로그인과 토큰 보관은 CLI 소유(읽기 전용 원칙).
// 2026-10-07 실측: `claude auth --help` → login(Sign in) · logout · status.
public static class LoginLauncher
{
    /// <summary>[Part 250] 도구별 로그인 진입점. Cursor·Antigravity 는 CLI 로그인이 없어 앱을 연다.</summary>
    public static bool Start(string tool) => tool switch
    {
        "Claude Code" => StartClaudeLogin(),
        "Codex" => StartCliLogin("codex", "login"), // [Part 251]
        "Cursor" => OpenApp(Path.Combine(LocalPrograms, "cursor", "Cursor.exe")),
        "Antigravity" => OpenApp(Path.Combine(LocalPrograms, "Antigravity IDE", "Antigravity IDE.exe")),
        _ => false,
    };

    private static string LocalPrograms => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

    private static bool OpenApp(string exe)
    {
        if (!File.Exists(exe)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <returns>실행 파일을 찾지 못하면 false</returns>
    public static bool StartClaudeLogin() => StartCliLogin("claude", "auth login");

    private static bool StartCliLogin(string name, string args)
    {
        string? exe = FindOnPath(name);
        if (exe is null) return false;
        try
        {
            // cmd /k — 로그인이 끝나도 창이 남아 결과를 읽을 수 있다
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{exe}\" {args}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? FindOnPath(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin")); // Claude Code 기본 설치 위치
        foreach (var dir in dirs)
        {
            foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // PATH 에 잘못된 문자가 섞인 항목은 건너뛴다
                }
            }
        }
        return null;
    }
}
