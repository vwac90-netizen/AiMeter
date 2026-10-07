using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AiMeter.Models;

namespace AiMeter.Providers;

// [Part 246] Claude Code 사용 한도.
// - 자격 증명은 Claude Code CLI 가 관리하는 파일을 「읽기만」 한다. 쓰기·갱신·로그 기록을 하지 않는다(토큰 회전은 CLI 소유).
// - 엔드포인트는 비공개(undocumented)라 응답 모양이 바뀔 수 있다 → 모르는 필드는 무시, 값이 없으면 null(「모름」).
//   2026-10-07 실측: limits[] = { kind, group, percent, severity, resets_at, scope.model.display_name, is_active }.
//   활성 한도인데도 is_active:false 로 오는 항목이 있었다 → is_active 로 거르지 않는다.
// - 짧은 시간에 여러 번 부르면 429 로 막힌다는 보고가 있어(직접 재현은 안 함) 백그라운드 호출은 5분에 1회로 묶는다.
public sealed class ClaudeProvider : IUsageProvider
{
    private static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly TimeSpan BackgroundInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ManualInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8),
        TimeSpan.FromMinutes(16), TimeSpan.FromMinutes(30),
    };

    private readonly HttpClient http;
    private UsageSnapshot? lastGood;
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset cooldownUntil = DateTimeOffset.MinValue;
    private int failures;

    public ClaudeProvider()
    {
        LoadCache();
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AiMeter/0.1.9");
    }

    public string Name => "Claude Code";

    public async Task<UsageSnapshot> GetSnapshotAsync(bool userInitiated, CancellationToken ct)
    {
        var cred = ReadCredential();
        if (cred is null)
        {
            return Empty(SnapshotState.NotSignedIn,
                S.T("Claude Code 에 로그인되어 있지 않습니다. 터미널에서 claude 를 실행해 로그인하세요.",
                    "Not signed in to Claude Code. Run claude in a terminal to sign in."));
        }

        string? plan = PlanLabel(cred.Subscription, cred.Tier);
        var now = DateTimeOffset.UtcNow;

        // 만료된 토큰은 보내지 않는다. 갱신은 Claude Code 가 다음에 실행될 때 스스로 한다.
        if (cred.ExpiresAt is { } exp && exp <= now)
        {
            return Stale(plan, SnapshotState.TokenExpired,
                S.T("로그인 토큰이 만료됐습니다. Claude Code 를 한 번 실행하면 갱신됩니다.",
                    "Sign-in token expired. Run Claude Code once to refresh it."));
        }

        var minGap = userInitiated ? ManualInterval : BackgroundInterval;
        if (lastGood is not null && now - lastAttempt < minGap) return lastGood;
        if (!userInitiated && now < cooldownUntil)
        {
            var wait = cooldownUntil - now;
            string mins = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes)).ToString(CultureInfo.InvariantCulture);
            return lastGood ?? Empty(SnapshotState.Error,
                S.T($"Claude 서버의 호출 제한에 걸렸습니다. {mins}분 뒤 다시 시도합니다.", $"Hit the Claude server's rate limit. Retrying in {mins} min."), plan);
        }

        lastAttempt = now;
        SaveCache();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.Token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Stale(plan, SnapshotState.TokenExpired,
                    S.T("서버가 로그인을 거부했습니다. Claude Code 를 다시 실행하거나 다시 로그인하세요.",
                        "The server rejected the sign-in. Run Claude Code again or sign in again."));
            }

            if (IsRetryable(resp.StatusCode))
            {
                ArmCooldown(resp.Headers.RetryAfter?.Delta);
                return lastGood ?? Empty(SnapshotState.Error,
                    S.T($"서버가 잠시 요청을 막았습니다({(int)resp.StatusCode}). 자동으로 다시 시도합니다.",
                        $"The server is throttling requests ({(int)resp.StatusCode}). Retrying automatically."), plan);
            }

            if (!resp.IsSuccessStatusCode)
            {
                return lastGood ?? Empty(SnapshotState.Error,
                    S.T($"사용량을 불러오지 못했습니다({(int)resp.StatusCode}).", $"Could not load usage ({(int)resp.StatusCode})."), plan);
            }

            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            var windows = ParseWindows(doc.RootElement);
            if (windows.Count == 0)
            {
                // 응답은 왔는데 아는 창이 하나도 없다 = 응답 모양이 바뀌었을 가능성. 0% 로 꾸미지 않는다.
                return lastGood ?? Empty(SnapshotState.Error,
                    S.T("응답 형식을 알아보지 못했습니다. 앱 업데이트가 필요할 수 있습니다.",
                        "Unrecognized response format. The app may need an update."), plan);
            }

            failures = 0;
            cooldownUntil = DateTimeOffset.MinValue;
            lastGood = new UsageSnapshot(Name, plan, windows, DateTimeOffset.UtcNow, SnapshotState.Ok, null);
            SaveCache();
            return lastGood;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            ArmCooldown(null);
            return lastGood ?? Empty(SnapshotState.Error,
                S.T("네트워크 오류로 사용량을 불러오지 못했습니다.", "Could not load usage because of a network error."), plan);
        }
    }

    private static bool IsRetryable(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private void ArmCooldown(TimeSpan? retryAfter)
    {
        var wait = retryAfter is { } ra && ra > TimeSpan.Zero
            ? (ra > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : ra)
            : Backoff[Math.Min(failures, Backoff.Length - 1)];
        failures++;
        var until = DateTimeOffset.UtcNow + wait;
        if (until > cooldownUntil) cooldownUntil = until; // 새 실패는 막는 시간을 늘리기만 한다
        SaveCache();
    }

    private UsageSnapshot Empty(SnapshotState state, string message, string? plan = null) =>
        new(Name, plan, Array.Empty<UsageWindow>(), null, state, message);

    /// <summary>상태 문구를 바꾸되, 마지막으로 받은 값이 있으면 화면에서 지우지 않는다.</summary>
    private UsageSnapshot Stale(string? plan, SnapshotState state, string message) =>
        lastGood is null
            ? Empty(state, message, plan)
            : lastGood with { State = state, Message = message };

    // ── 응답 해석 ─────────────────────────────────────────────

    private static List<UsageWindow> ParseWindows(JsonElement root)
    {
        var list = new List<UsageWindow>();
        var fiveHour = TimeSpan.FromHours(5);
        var week = TimeSpan.FromDays(7);

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in limits.EnumerateArray())
            {
                if (l.ValueKind != JsonValueKind.Object) continue;
                double? pct = Number(l, "percent");
                var reset = Date(l, "resets_at");
                switch (Text(l, "kind"))
                {
                    case "session":
                        list.Add(new UsageWindow("session", S.T("5시간", "5-hour"), pct, reset, fiveHour));
                        break;
                    case "weekly_all":
                        list.Add(new UsageWindow("weekly", S.T("주간 전체", "Weekly · all"), pct, reset, week));
                        break;
                    case "weekly_scoped":
                        string model = ScopedModelName(l) ?? S.T("모델", "model");
                        list.Add(new UsageWindow("weekly:" + model, S.T($"주간 {model}", $"Weekly {model}"), pct, reset, week));
                        break;
                    // 모르는 kind 는 뜻을 추측하지 않고 건너뛴다
                }
            }
        }

        // limits[] 가 없던 시절의 응답 모양
        if (list.Count == 0)
        {
            if (root.TryGetProperty("five_hour", out var fh) && fh.ValueKind == JsonValueKind.Object)
                list.Add(new UsageWindow("session", S.T("5시간", "5-hour"), Number(fh, "utilization"), Date(fh, "resets_at"), fiveHour));
            if (root.TryGetProperty("seven_day", out var sd) && sd.ValueKind == JsonValueKind.Object)
                list.Add(new UsageWindow("weekly", S.T("주간 전체", "Weekly · all"), Number(sd, "utilization"), Date(sd, "resets_at"), week));
        }

        return list;
    }

    private static string? ScopedModelName(JsonElement limit)
    {
        if (limit.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
            && scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
        {
            return Text(model, "display_name") ?? Text(model, "id");
        }
        return null;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTimeOffset? Date(JsonElement e, string name)
    {
        var s = Text(e, name);
        return s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    // ── 자격 증명(읽기 전용) ───────────────────────────────────

    private sealed record Credential(string Token, DateTimeOffset? ExpiresAt, string? Subscription, string? Tier);

    private static Credential? ReadCredential()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        if (!File.Exists(path)) return null;

        // CLI 가 토큰을 다시 쓰는 순간과 겹치면 공유 위반·반쯤 쓴 파일을 읽을 수 있다 → 짧게 두 번 더 시도
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o) || o.ValueKind != JsonValueKind.Object) return null;
                var token = Text(o, "accessToken");
                if (string.IsNullOrEmpty(token)) return null;
                DateTimeOffset? expires = o.TryGetProperty("expiresAt", out var ex) && ex.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ex.GetInt64())
                    : null;
                return new Credential(token, expires, Text(o, "subscriptionType"), Text(o, "rateLimitTier"));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Thread.Sleep(60);
            }
        }
        return null;
    }

    // ── [Part 251] 디스크 캐시 ───────────────────────────────────
    // 새 프로세스(재부팅·재실행)가 이전 값을 잊고 곧바로 서버를 부르면 짧은 시간 호출 제한(429)에 걸려 최대 30분 동안 값이 안 보였다
    // (2026-10-07 실측: 검사로 수십 번 재실행 → 「잠시 후 다시 시도합니다」). 마지막 값·마지막 호출 시각·쿨다운을 파일로 이어 간다.
    // 토큰·계정 정보는 넣지 않는다 — 화면에 보이는 사용률·초기화 시각·플랜 이름뿐이다.

    private sealed record CacheFile(UsageSnapshot? LastGood, DateTimeOffset LastAttempt, DateTimeOffset CooldownUntil, int Failures);

    private static readonly string CachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AiMeter", "claude-cache.json");

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));
            if (c is null) return;
            lastGood = c.LastGood;
            lastAttempt = c.LastAttempt;
            cooldownUntil = c.CooldownUntil;
            failures = c.Failures;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // 캐시를 못 읽으면 처음부터 — 캐시는 있으면 좋은 것이지 필수가 아니다
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new CacheFile(lastGood, lastAttempt, cooldownUntil, failures)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 저장 실패는 다음 호출에 영향이 없다 — 다음 실행이 서버를 한 번 더 부를 뿐
        }
    }

    /// <summary>예: subscriptionType "max" + rateLimitTier "default_claude_max_5x" → "Max 5x".</summary>
    private static string? PlanLabel(string? subscription, string? tier)
    {
        if (tier is not null)
        {
            if (tier.Contains("max_20x", StringComparison.OrdinalIgnoreCase)) return "Max 20x";
            if (tier.Contains("max_5x", StringComparison.OrdinalIgnoreCase)) return "Max 5x";
        }
        if (string.IsNullOrWhiteSpace(subscription)) return null;
        return char.ToUpperInvariant(subscription[0]) + subscription[1..];
    }
}
