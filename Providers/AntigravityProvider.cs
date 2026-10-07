using System.Globalization;
using System.Management;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiMeter.Models;

namespace AiMeter.Providers;

// [Part 250] Antigravity 사용 한도 — 공개 엔드포인트도, 읽을 자격 증명 파일도 없다.
// Antigravity 앱이 띄우는 로컬 엔진(language_server_windows_x64.exe)에 127.0.0.1 로만 묻는다.
// - 2026-10-07 실측: 엔진 실행 인자 --csrf_token 을 X-Codeium-Csrf-Token 헤더로, Connect-Protocol-Version: 1,
//   POST /exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary, 본문 {} → 200
//   response.groups[]{displayName, buckets[]{bucketId, window(5h|weekly), remainingFraction, resetTime}}.
//   엔진의 대기 포트 여러 개 중 하나만 답하고, 그 포트는 http 였다(다른 포트는 400/500/TLS 오류) → 포트마다 http·https 를 다 시도.
//   로그인 전에는 500 「error getting token source … key not found」.
// - TLS 검증 생략은 127.0.0.1 전용 HttpClient 에만 둔다. 앱의 다른 통신은 약해지지 않는다.
// - 앱이 꺼져 있으면 엔진도 없다 → 마지막 값을 보여 주고 그렇다고 알린다(엔진을 직접 띄우지 않는다).
public sealed class AntigravityProvider : IUsageProvider
{
    private const string Service = "/exa.language_server_pb.LanguageServerService/";
    private readonly HttpClient http;
    private UsageSnapshot? lastGood;
    private Uri? lastBase; // 지난번에 답한 포트·프로토콜 — 먼저 시도한다
    private DateTimeOffset lastStandalone = DateTimeOffset.MinValue; // [Part 252] 엔진 단독 실행은 무거워 5분에 1회
    private static readonly TimeSpan StandaloneInterval = TimeSpan.FromMinutes(5);

    public AntigravityProvider()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri?.Host == "127.0.0.1",
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
    }

    public string Name => "Antigravity";

    private static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Antigravity IDE");

    public async Task<UsageSnapshot> GetSnapshotAsync(bool userInitiated, CancellationToken ct)
    {
        var engines = await Task.Run(FindEngines, ct).ConfigureAwait(false);
        if (engines.Count == 0)
        {
            if (!Directory.Exists(InstallDir) && !AntigravityStandalone.Available)
                return Empty(SnapshotState.NotInstalled, S.T("Antigravity 가 설치돼 있지 않습니다.", "Antigravity is not installed."));

            // [Part 252] 앱이 꺼져 있어도 Antigravity 제품의 엔진을 잠깐 띄워 읽는다(앱을 켤 필요 없음)
            if (AntigravityStandalone.Available)
            {
                if (lastGood is not null && DateTimeOffset.UtcNow - lastStandalone < (userInitiated ? TimeSpan.FromMinutes(1) : StandaloneInterval)) return lastGood;
                lastStandalone = DateTimeOffset.UtcNow;
                var read = await AntigravityStandalone.RunAsync(ReadFromAsync, ct).ConfigureAwait(false);
                if (read is not null)
                {
                    lastGood = read;
                    return lastGood;
                }
            }
            string msg = AntigravityStandalone.Available
                ? S.T("Antigravity 엔진에서 값을 받지 못했습니다(로그인이 필요할 수 있음). 잠시 뒤 다시 읽습니다.", "Couldn't get usage from the Antigravity engine (sign-in may be needed). Retrying shortly.")
                : S.T("Antigravity 앱이 꺼져 있습니다. 앱을 켜면 사용량을 읽습니다.", "The Antigravity app is closed. Open it to read usage.");
            return lastGood is null ? Empty(SnapshotState.Error, msg) : lastGood with { Message = S.T("Antigravity 앱이 꺼져 있어 마지막으로 읽은 값입니다.", "The Antigravity app is closed — showing the last value read.") };
        }

        var candidates = new List<(Uri Base, string Token)>();
        foreach (var (token, ports) in engines)
        {
            foreach (int port in ports)
            {
                candidates.Add((new Uri($"http://127.0.0.1:{port}"), token));
                candidates.Add((new Uri($"https://127.0.0.1:{port}"), token));
            }
        }
        if (lastBase is not null) candidates = candidates.OrderBy(c => c.Base == lastBase ? 0 : 1).ToList();

        bool signedOut = false;
        foreach (var (baseUri, token) in candidates)
        {
            var (status, body) = await Call(baseUri, token, "RetrieveUserQuotaSummary", ct).ConfigureAwait(false);
            if (status == 200 && body is not null)
            {
                lastBase = baseUri;
                string? plan = null;
                var (s2, b2) = await Call(baseUri, token, "GetUserStatus", ct).ConfigureAwait(false);
                if (s2 == 200 && b2 is not null) plan = PlanName(b2);
                try
                {
                    var windows = ParseQuota(body);
                    lastGood = new UsageSnapshot(Name, plan, windows, DateTimeOffset.UtcNow, SnapshotState.Ok,
                        windows.Count == 0 ? S.T("현재 플랜에서 받은 사용 한도가 없습니다.", "Your current plan reports no usage limits.") : null);
                    return lastGood;
                }
                catch (JsonException)
                {
                    break;
                }
            }
            if (status == 500 && body is not null && body.Contains("token source", StringComparison.OrdinalIgnoreCase)) signedOut = true;
        }

        if (signedOut) return Empty(SnapshotState.NotSignedIn, S.T("Antigravity 앱에서 로그인하세요.", "Sign in from the Antigravity app."));
        return lastGood ?? Empty(SnapshotState.Error, S.T("Antigravity 엔진이 아직 준비되지 않았습니다. 잠시 뒤 다시 읽습니다.", "The Antigravity engine isn't ready yet. Retrying shortly."));
    }

    /// <summary>[Part 252] 엔진 하나에서 사용량·플랜을 읽는다. 아직 준비 전이거나 실패면 null.</summary>
    private async Task<UsageSnapshot?> ReadFromAsync(Uri baseUri, string token)
    {
        var (status, body) = await Call(baseUri, token, "RetrieveUserQuotaSummary", CancellationToken.None).ConfigureAwait(false);
        if (status != 200 || body is null) return null;
        string? plan = null;
        var (s2, b2) = await Call(baseUri, token, "GetUserStatus", CancellationToken.None).ConfigureAwait(false);
        if (s2 == 200 && b2 is not null) plan = PlanName(b2);
        try
        {
            var windows = ParseQuota(body);
            return new UsageSnapshot(Name, plan, windows, DateTimeOffset.UtcNow, SnapshotState.Ok,
                windows.Count == 0 ? S.T("현재 플랜에서 받은 사용 한도가 없습니다.", "Your current plan reports no usage limits.") : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(int Status, string? Body)> Call(Uri baseUri, string token, string method, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, Service + method))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Connect-Protocol-Version", "1");
            req.Headers.Add("X-Codeium-Csrf-Token", token);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (0, null);
        }
    }

    private static List<UsageWindow> ParseQuota(string json)
    {
        var list = new List<UsageWindow>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array) return list;
        foreach (var g in groups.EnumerateArray())
        {
            // 「Gemini Models」 → Gemini · 「Claude and GPT models」 → Claude·GPT
            string group = Regex.Replace(Text(g, "displayName") ?? "", @"\s+models?$", "", RegexOptions.IgnoreCase).Replace(" and ", "·");
            if (!g.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) continue;
            foreach (var b in buckets.EnumerateArray())
            {
                string? id = Text(b, "bucketId");
                if (id is null) continue;
                string window = Text(b, "window") ?? "";
                (string label, TimeSpan? dur) = window switch
                {
                    "5h" => (S.T("5시간", "5-hour"), TimeSpan.FromHours(5)),
                    "weekly" => (S.T("주간", "weekly"), TimeSpan.FromDays(7)),
                    _ => (window, (TimeSpan?)null),
                };
                double? used = b.TryGetProperty("remainingFraction", out var f) && f.ValueKind == JsonValueKind.Number
                    ? Math.Clamp((1 - f.GetDouble()) * 100, 0, 100)
                    : null; // 값이 없으면 「모름」 — 소진으로 가정하지 않는다
                DateTimeOffset? reset = Text(b, "resetTime") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
                list.Add(new UsageWindow(id, $"{group} {label}".Trim(), used, reset, dur));
            }
        }
        return list;
    }

    private static string? PlanName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("userStatus", out var u) && u.TryGetProperty("planStatus", out var ps)
                && ps.TryGetProperty("planInfo", out var pi) ? Text(pi, "planName") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private UsageSnapshot Empty(SnapshotState state, string message) => new(Name, null, Array.Empty<UsageWindow>(), null, state, message);

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── 엔진 찾기: WMI(실행 인자) + 대기 포트 표 ────────────────────

    private static List<(string Token, List<int> Ports)> FindEngines()
    {
        var result = new List<(string, List<int>)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name LIKE 'language_server%'");
            var listening = ListeningLoopbackPorts();
            foreach (ManagementObject p in searcher.Get())
            {
                string? path = p["ExecutablePath"] as string;
                string? cmd = p["CommandLine"] as string;
                if (path is null || cmd is null || !path.Contains("Antigravity", StringComparison.OrdinalIgnoreCase)) continue; // 다른 제품의 엔진은 건드리지 않는다
                var m = Regex.Match(cmd, @"--csrf_token[= ](\S+)");
                if (!m.Success) continue;
                uint pid = (uint)p["ProcessId"];
                if (listening.TryGetValue(pid, out var ports)) result.Add((m.Groups[1].Value.Trim('"'), ports));
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            // WMI 를 쓸 수 없으면 「앱이 꺼져 있음」 과 같게 다룬다
        }
        return result;
    }

    /// <summary>PID → 127.0.0.1 에서 대기 중인 TCP 포트들.</summary>
    private static Dictionary<uint, List<int>> ListeningLoopbackPorts()
    {
        var map = new Dictionary<uint, List<int>>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2 /* AF_INET */, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, 2, 3, 0) != 0) return map;
            int count = Marshal.ReadInt32(buf);
            IntPtr row = buf + 4;
            for (int i = 0; i < count; i++, row += 24)
            {
                uint localAddr = (uint)Marshal.ReadInt32(row + 4);
                int rawPort = Marshal.ReadInt32(row + 8);
                uint pid = (uint)Marshal.ReadInt32(row + 20);
                if (localAddr != 0x0100007F) continue; // 127.0.0.1
                int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF); // 네트워크 바이트 순서
                if (!map.TryGetValue(pid, out var list)) map[pid] = list = new List<int>();
                list.Add(port);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return map;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);
}
