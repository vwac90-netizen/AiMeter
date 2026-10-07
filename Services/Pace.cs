using AiMeter.Models;

namespace AiMeter.Services;

// [Part 246] 「지금 속도면 언제 바닥나나」 — 시안 B 의 핵심 문장.
// 창이 시작된 뒤 지금까지의 「평균」 속도가 그대로 이어진다고 가정한 단순 추정이다. 화면에도 그렇게 밝힌다.
// 근거가 약할 때(창 초반 10% 이내, 사용 5% 미만)는 예측하지 않는다 — 모르는 것을 그럴듯하게 채우지 않는다.
public static class Pace
{
    public sealed record Estimate(UsageWindow Window, DateTimeOffset EmptyAt, DateTimeOffset ResetsAt)
    {
        public bool RunsOutBeforeReset => EmptyAt < ResetsAt;
    }

    public static Estimate? For(UsageWindow w, DateTimeOffset now)
    {
        if (w.UsedPercent is not double used || w.ResetsAt is not { } reset || w.Duration is not { } duration) return null;
        var start = reset - duration;
        var elapsed = now - start;
        if (elapsed <= TimeSpan.Zero || elapsed > duration) return null;
        if (elapsed.TotalSeconds < duration.TotalSeconds * 0.1 || used < 5) return null;
        if (used >= 100) return new Estimate(w, now, reset);
        var emptyAt = start + TimeSpan.FromSeconds(elapsed.TotalSeconds * 100 / used);
        return new Estimate(w, emptyAt, reset);
    }

    /// <summary>초기화 전에 바닥날 창 중 가장 이른 것. 없으면 null.</summary>
    public static Estimate? MostUrgent(IEnumerable<UsageWindow> windows, DateTimeOffset now) =>
        windows.Select(w => For(w, now))
            .Where(e => e is not null && e.RunsOutBeforeReset)
            .OrderBy(e => e!.EmptyAt)
            .FirstOrDefault();

    public static bool HasAnyEstimate(IEnumerable<UsageWindow> windows, DateTimeOffset now) =>
        windows.Any(w => For(w, now) is not null);
}
