using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AiMeter.Models;

namespace AiMeter.Providers;

// [Part 250] Cursor 사용 한도 — 청구 주기 하나.
// - 로그인 정보는 Cursor 앱이 %APPDATA%\Cursor\User\globalStorage\state.vscdb(SQLite) 에 둔다. 「읽기 전용」 으로만 연다(실행 중인 Cursor 를 방해하지 않게).
//   Windows 에 기본으로 있는 winsqlite3.dll 을 써서 DLL 을 따로 싣지 않는다.
// - 2026-10-07 실측: ItemTable 의 cursorAuth/accessToken = JWT(sub = "google-oauth2|<id>").
//   GET https://cursor.com/api/usage-summary + Cookie WorkosCursorSessionToken=<id>%3A%3A<token> → 200.
//   무료 플랜은 individualUsage.plan.limit = 0 이라 비율을 낼 수 없다 → 0% 가 아니라 null(「모름」).
public sealed class CursorProvider : IUsageProvider
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private UsageSnapshot? lastGood;
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;

    public string Name => "Cursor";

    private static string DbPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");

    public async Task<UsageSnapshot> GetSnapshotAsync(bool userInitiated, CancellationToken ct)
    {
        if (!File.Exists(DbPath)) return Empty(SnapshotState.NotInstalled, S.T("Cursor 가 설치돼 있지 않습니다.", "Cursor is not installed."));

        string? token = ReadValue("cursorAuth/accessToken");
        if (string.IsNullOrEmpty(token)) return Empty(SnapshotState.NotSignedIn, S.T("Cursor 앱에서 로그인하세요.", "Sign in from the Cursor app."));

        var (userId, expires) = ParseJwt(token);
        if (userId is null) return Empty(SnapshotState.Error, S.T("Cursor 로그인 정보를 알아보지 못했습니다.", "Couldn't read the Cursor sign-in."));
        if (expires is { } exp && exp <= DateTimeOffset.UtcNow)
            return Stale(SnapshotState.TokenExpired, S.T("Cursor 로그인이 만료됐습니다. Cursor 앱을 열면 갱신됩니다.", "Cursor sign-in expired. Open the Cursor app to refresh it."));

        var now = DateTimeOffset.UtcNow;
        if (lastGood is not null && now - lastAttempt < (userInitiated ? TimeSpan.FromMinutes(1) : MinInterval)) return lastGood;
        lastAttempt = now;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://cursor.com/api/usage-summary");
            req.Headers.Add("Cookie", $"WorkosCursorSessionToken={userId}%3A%3A{token}");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Stale(SnapshotState.TokenExpired, S.T("서버가 Cursor 로그인을 거부했습니다. Cursor 앱에서 다시 로그인하세요.", "The server rejected the Cursor sign-in. Sign in again in the Cursor app."));
            if (!resp.IsSuccessStatusCode)
                return lastGood ?? Empty(SnapshotState.Error, S.T($"Cursor 사용량을 불러오지 못했습니다({(int)resp.StatusCode}).", $"Couldn't load Cursor usage ({(int)resp.StatusCode})."));

            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            lastGood = Parse(doc.RootElement);
            return lastGood;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return lastGood ?? Empty(SnapshotState.Error, S.T("네트워크 오류로 Cursor 사용량을 불러오지 못했습니다.", "Couldn't load Cursor usage because of a network error."));
        }
    }

    private UsageSnapshot Parse(JsonElement root)
    {
        var start = Date(root, "billingCycleStart");
        var end = Date(root, "billingCycleEnd");
        string? membership = Text(root, "membershipType");
        string? plan = string.IsNullOrEmpty(membership) ? null : char.ToUpperInvariant(membership[0]) + membership[1..];

        double? used = null;
        string? message = null;
        if (root.TryGetProperty("individualUsage", out var iu) && iu.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            double? limit = Number(p, "limit");
            if (limit is > 0) used = Number(p, "totalPercentUsed") ?? (Number(p, "used") is double u ? u * 100 / limit.Value : null);
        }
        if (used is null)
        {
            message = S.T("이 플랜은 사용 한도 값을 주지 않아 비율을 계산할 수 없습니다(무료 플랜은 한도가 0 으로 옵니다).",
                "This plan reports no usage limit, so a percentage can't be computed (free plans report a limit of 0).");
        }

        TimeSpan? duration = start is { } s && end is { } e && e > s ? e - s : null;
        var window = new UsageWindow("billing", S.T("이번 청구 주기", "This billing cycle"), used, end, duration);
        return new UsageSnapshot(Name, plan, new[] { window }, DateTimeOffset.UtcNow, SnapshotState.Ok, message);
    }

    private UsageSnapshot Empty(SnapshotState state, string message) => new(Name, null, Array.Empty<UsageWindow>(), null, state, message);

    private UsageSnapshot Stale(SnapshotState state, string message) =>
        lastGood is null ? Empty(state, message) : lastGood with { State = state, Message = message };

    private static (string? UserId, DateTimeOffset? Expires) ParseJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return (null, null);
            string b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            string? sub = Text(doc.RootElement, "sub");
            DateTimeOffset? exp = doc.RootElement.TryGetProperty("exp", out var e) && e.TryGetInt64(out var x) ? DateTimeOffset.FromUnixTimeSeconds(x) : null;
            return (sub?.Split('|')[^1], exp);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return (null, null);
        }
    }

    // ── state.vscdb 읽기 전용 ─────────────────────────────────────

    private static string? ReadValue(string key)
    {
        IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(Utf8(DbPath), out db, 0x1 /* SQLITE_OPEN_READONLY */, IntPtr.Zero) != 0) return null;
            sqlite3_busy_timeout(db, 500);
            if (sqlite3_prepare_v2(db, Utf8("SELECT value FROM ItemTable WHERE key = ?1"), -1, out stmt, IntPtr.Zero) != 0) return null;
            byte[] k = Encoding.UTF8.GetBytes(key);
            sqlite3_bind_text(stmt, 1, k, k.Length, new IntPtr(-1) /* SQLITE_TRANSIENT */);
            if (sqlite3_step(stmt) != 100 /* SQLITE_ROW */) return null;
            IntPtr text = sqlite3_column_text(stmt, 0);
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
        }
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    [DllImport("winsqlite3.dll")] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] text, int bytes, IntPtr destructor);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll")] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_close_v2(IntPtr db);

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Text(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}
