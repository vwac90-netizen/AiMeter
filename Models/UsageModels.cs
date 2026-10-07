namespace AiMeter.Models;

// [Part 246] 수집↔UI 분리의 경계. 화면은 이 모델만 알고 공급자(Claude·Codex…)의 응답 모양은 모른다.

/// <summary>사용 한도 창 하나(5시간·주간·모델별 주간 등).</summary>
/// <param name="Key">새로고침 사이에 같은 창을 알아보는 고정 키</param>
/// <param name="UsedPercent">사용률 0~100. <c>null</c> = 서버가 값을 주지 않음(「모름」) — 0 과 구분한다(CLAUDE.md §3.4 폴백 0 금지)</param>
/// <param name="Duration">창 길이. 모르면 <c>null</c> → 소진 예측을 하지 않는다</param>
public sealed record UsageWindow(string Key, string Label, double? UsedPercent, DateTimeOffset? ResetsAt, TimeSpan? Duration)
{
    public double? RemainingPercent => UsedPercent is double used ? Math.Clamp(100 - used, 0, 100) : null;
}

public enum SnapshotState
{
    Ok,
    NotSignedIn,
    TokenExpired,
    Error,
    /// <summary>[Part 250] 이 PC 에 도구가 설치돼 있지 않다 — 화면에서는 빼고 설정 「계정」 에만 보인다.</summary>
    NotInstalled,
}

/// <summary>도구 하나의 한 시점 사용량.</summary>
/// <param name="CapturedAt">서버에서 실제로 받아 온 시각. 실패 뒤 마지막 값을 다시 보여 줄 때도 이 값은 그대로라 「n분 전」이 정직하게 늘어난다</param>
public sealed record UsageSnapshot(
    string Tool,
    string? Plan,
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset? CapturedAt,
    SnapshotState State,
    string? Message);
