using System.Text.Json;

namespace AiMeter.Services;

// [Part 248] 시안 D(어디에 썼나) — 이번 달 Claude Code 가 처리한 토큰 수를 로컬 기록에서 센다.
// - 사용량 API 에는 토큰 수가 없어 로컬 기록(~/.claude/projects/**/*.jsonl)을 읽어야 한다 → 기본 꺼짐, 사용자가 켜야 동작.
// - 응답 줄(type=assistant)의 message.id · timestamp · message.usage 만 쓴다. 대화 내용은 저장·기록·전송하지 않는다.
// - 2026-10-07 실측: 같은 message.id 가 여러 줄에 반복된다 → id 당 한 번(가장 큰 출력 토큰 값)만 센다.
// - 파일별 (크기, 수정 시각) 캐시 — 바뀌지 않은 파일은 다시 읽지 않는다.
// - [Part 248] 모델별 합계도 낸다. 모델 이름은 응답마다 남는 message.model 을 그대로 쓰므로 새 모델이 나와도 앱 수정 없이 잡힌다
//   (이 PC 기록 실측: claude-opus-4-8 → claude-opus-5 → claude-opus-5-5 전환이 모두 남아 있다). 「<synthetic>」 은 모델이 아니라 뺀다.
public sealed class ClaudeLogScanner
{
    public sealed record Totals(long Input, long Output, long CacheCreate, long CacheRead, int Messages, DateTimeOffset ScannedAt,
        IReadOnlyDictionary<string, long> ByModel)
    {
        public long All => Input + Output + CacheCreate + CacheRead;
    }

    private sealed record Usage(string Model, long Input, long Output, long CacheCreate, long CacheRead)
    {
        public long All => Input + Output + CacheCreate + CacheRead;
    }

    private sealed class FileEntry
    {
        public long Length;
        public DateTime WriteTimeUtc;
        public Dictionary<string, Usage> Messages = new();
    }

    private readonly Dictionary<string, FileEntry> cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset cacheMonth;
    private Task<Totals?>? running;

    public Totals? Last { get; private set; }

    /// <summary>백그라운드에서 집계한다. 이미 돌고 있으면 그 작업을 같이 기다린다.</summary>
    public Task<Totals?> ScanAsync()
    {
        if (running is { IsCompleted: false }) return running;
        running = Task.Run(Scan);
        return running;
    }

    private Totals? Scan()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(root)) return Last = null;

        var nowLocal = DateTimeOffset.Now;
        var monthStart = new DateTimeOffset(nowLocal.Year, nowLocal.Month, 1, 0, 0, 0, nowLocal.Offset);
        if (monthStart != cacheMonth)
        {
            cache.Clear();
            cacheMonth = monthStart;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Last;
        }

        foreach (var path in files)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc < monthStart.UtcDateTime) continue; // 이번 달에 쓰이지 않은 파일은 볼 필요가 없다
                seen.Add(path);
                if (cache.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.WriteTimeUtc == info.LastWriteTimeUtc) continue;
                cache[path] = ReadFile(path, info, monthStart);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 쓰는 중이거나 잠긴 파일은 다음 집계 때 다시 본다
            }
        }

        foreach (var gone in cache.Keys.Where(k => !seen.Contains(k)).ToList()) cache.Remove(gone);

        // 같은 응답이 여러 파일(이어 하기 세션 등)에 들어 있을 수 있어 id 로 한 번 더 합친다
        var merged = new Dictionary<string, Usage>(StringComparer.Ordinal);
        foreach (var entry in cache.Values)
        {
            foreach (var (id, u) in entry.Messages)
            {
                if (!merged.TryGetValue(id, out var prev) || u.Output > prev.Output) merged[id] = u;
            }
        }

        long input = 0, output = 0, create = 0, read = 0;
        var byModel = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var u in merged.Values)
        {
            if (u.Model.Length > 0 && !u.Model.StartsWith("<", StringComparison.Ordinal))
                byModel[u.Model] = byModel.GetValueOrDefault(u.Model) + u.All;
            input += u.Input;
            output += u.Output;
            create += u.CacheCreate;
            read += u.CacheRead;
        }
        return Last = new Totals(input, output, create, read, merged.Count, DateTimeOffset.UtcNow, byModel);
    }

    private static FileEntry ReadFile(string path, FileInfo info, DateTimeOffset monthStart)
    {
        var entry = new FileEntry { Length = info.Length, WriteTimeUtc = info.LastWriteTimeUtc };
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // JSON 을 해석하기 전에 응답 줄만 거른다(대부분의 줄은 여기서 버려진다)
            if (!line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) || !line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (!r.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(ts.GetString(), out var when) || when < monthStart) continue;
                if (!r.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                if (!msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
                string id = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()!
                    : r.TryGetProperty("uuid", out var uuid) && uuid.ValueKind == JsonValueKind.String ? uuid.GetString()! : "";
                if (id.Length == 0) continue;

                string model = msg.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                var u = new Usage(model, Num(usage, "input_tokens"), Num(usage, "output_tokens"),
                    Num(usage, "cache_creation_input_tokens"), Num(usage, "cache_read_input_tokens"));
                if (!entry.Messages.TryGetValue(id, out var prev) || u.Output > prev.Output) entry.Messages[id] = u;
            }
            catch (JsonException)
            {
                // 쓰는 중이라 잘린 마지막 줄 등 — 건너뛴다
            }
        }
        return entry;
    }

    /// <summary>
    /// 모델 ID → 화면 이름. 목록을 박아 두지 않고 규칙으로만 바꾼다 — 새 모델이 나와도 앱 수정이 필요 없다.
    /// 예) claude-opus-5-5 → Opus 5.5 · claude-haiku-4-5-20251001 → Haiku 4.5 · opus → Opus.
    /// 규칙에 맞지 않는 형식은 ID 를 그대로 보여 준다(값은 맞고 이름만 덜 예쁘다 — 추측해서 바꾸지 않는다).
    /// </summary>
    public static string DisplayName(string id)
    {
        string s = id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ? id[7..] : id;
        var parts = s.Split('-').ToList();
        if (parts.Count > 1 && parts[^1].Length == 8 && parts[^1].All(char.IsAsciiDigit)) parts.RemoveAt(parts.Count - 1); // 날짜 꼬리
        if (parts.Count == 0 || parts[0].Length == 0 || !parts[0].All(char.IsAsciiLetter)) return id;
        if (!parts.Skip(1).All(x => x.Length > 0 && x.All(char.IsAsciiDigit))) return id;
        string family = char.ToUpperInvariant(parts[0][0]) + parts[0][1..].ToLowerInvariant();
        return parts.Count == 1 ? family : $"{family} {string.Join(".", parts.Skip(1))}";
    }

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}

