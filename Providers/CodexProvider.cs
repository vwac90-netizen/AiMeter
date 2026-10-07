using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AiMeter.Models;

namespace AiMeter.Providers;

// [Part 251] Codex(ChatGPT) 사용 한도.
// - 자격 증명은 Codex CLI 가 관리하는 auth.json 을 「읽기만」 한다(CODEX_HOME 이 있으면 그 아래). 갱신·기록하지 않는다.
// - 2026-10-07 실측: GET https://chatgpt.com/backend-api/wham/usage + Bearer + ChatGPT-Account-Id → 200
//   rate_limit.primary_window/secondary_window = {used_percent, limit_window_seconds, reset_at(epoch 초)}.
//   무료 플랜은 primary 하나가 30일(2,592,000초) 창이었다 → 창 종류는 위치가 아니라 limit_window_seconds 로 판별한다.
public sealed class CodexProvider : IUsageProvider
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);
    private readonly HttpClient http;
    private UsageSnapshot? lastGood;
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;

    public CodexProvider()
    {
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AiMeter/0.1");
    }

    public string Name => "Codex";

    private static string Home => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
        ? h
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public async Task<UsageSnapshot> GetSnapshotAsync(bool userInitiated, CancellationToken ct)
    {
        if (!Directory.Exists(Home)) return Empty(SnapshotState.NotInstalled, S.T("Codex 가 설치돼 있지 않습니다.", "Codex is not installed."));
        var cred = ReadCredential();
        if (cred is null) return Empty(SnapshotState.NotSignedIn, S.T("Codex 에 로그인되어 있지 않습니다. 터미널에서 codex login 을 실행하세요.", "Not signed in to Codex. Run codex login in a terminal."));
        if (cred.Value.Expires is { } exp && exp <= DateTimeOffset.UtcNow)
            return Stale(SnapshotState.TokenExpired, S.T("Codex 로그인 토큰이 만료됐습니다. Codex 를 한 번 실행하면 갱신됩니다.", "Codex sign-in token expired. Run Codex once to refresh it."));

        var now = DateTimeOffset.UtcNow;
        if (lastGood is not null && now - lastAttempt < (userInitiated ? TimeSpan.FromMinutes(1) : MinInterval)) return lastGood;
        lastAttempt = now;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.Value.Token);
            if (cred.Value.AccountId is { } id) req.Headers.Add("ChatGPT-Account-Id", id);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Stale(SnapshotState.TokenExpired, S.T("서버가 Codex 로그인을 거부했습니다. codex login 으로 다시 로그인하세요.", "The server rejected the Codex sign-in. Sign in again with codex login."));
            if (!resp.IsSuccessStatusCode)
                return lastGood ?? Empty(SnapshotState.Error, S.T($"Codex 사용량을 불러오지 못했습니다({(int)resp.StatusCode}).", $"Couldn't load Codex usage ({(int)resp.StatusCode})."));

            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var windows = new List<UsageWindow>();
            if (root.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "primary_window", "secondary_window" })
                {
                    if (rl.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object && ParseWindow(w) is { } uw) windows.Add(uw);
                }
            }
            string? planType = root.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString() : null;
            string? plan = string.IsNullOrEmpty(planType) ? null : char.ToUpperInvariant(planType[0]) + planType[1..];
            lastGood = new UsageSnapshot(Name, plan, windows, DateTimeOffset.UtcNow, SnapshotState.Ok,
                windows.Count == 0 ? S.T("응답에 사용 한도 창이 없습니다.", "The response has no usage windows.") : null);
            return lastGood;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return lastGood ?? Empty(SnapshotState.Error, S.T("네트워크 오류로 Codex 사용량을 불러오지 못했습니다.", "Couldn't load Codex usage because of a network error."));
        }
    }

    private static UsageWindow? ParseWindow(JsonElement w)
    {
        if (!w.TryGetProperty("limit_window_seconds", out var sec) || !sec.TryGetInt64(out long seconds) || seconds <= 0) return null;
        double? used = w.TryGetProperty("used_percent", out var u) && u.ValueKind == JsonValueKind.Number ? Math.Clamp(u.GetDouble(), 0, 100) : null;
        DateTimeOffset? reset = w.TryGetProperty("reset_at", out var r) && r.TryGetInt64(out long epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null;
        var span = TimeSpan.FromSeconds(seconds);
        string label = seconds switch
        {
            18_000 => S.T("5시간", "5-hour"),
            604_800 => S.T("주간", "Weekly"),
            _ when span.TotalDays >= 1 && span.TotalDays % 1 == 0 => S.T($"{span.TotalDays:0}일", $"{span.TotalDays:0}-day"),
            _ => S.T($"{span.TotalHours:0.#}시간", $"{span.TotalHours:0.#}-hour"),
        };
        return new UsageWindow($"window:{seconds}", label, used, reset, span);
    }

    private (string Token, string? AccountId, DateTimeOffset? Expires)? ReadCredential()
    {
        string path = Path.Combine(Home, "auth.json");
        if (!File.Exists(path)) return null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                if (!doc.RootElement.TryGetProperty("tokens", out var t) || t.ValueKind != JsonValueKind.Object) return null;
                string? token = t.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                if (string.IsNullOrEmpty(token)) return null;
                string? account = t.TryGetProperty("account_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                return (token, account, JwtExpiry(token));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Thread.Sleep(60); // CLI 가 토큰을 다시 쓰는 순간과 겹쳤을 수 있다
            }
        }
        return null;
    }

    private static DateTimeOffset? JwtExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            string b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            return doc.RootElement.TryGetProperty("exp", out var e) && e.TryGetInt64(out var x) ? DateTimeOffset.FromUnixTimeSeconds(x) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private UsageSnapshot Empty(SnapshotState state, string message) => new(Name, null, Array.Empty<UsageWindow>(), null, state, message);

    private UsageSnapshot Stale(SnapshotState state, string message) =>
        lastGood is null ? Empty(state, message) : lastGood with { State = state, Message = message };
}
