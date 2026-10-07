using AiMeter.Models;

namespace AiMeter.Providers;

public interface IUsageProvider
{
    string Name { get; }

    /// <param name="userInitiated">새로고침 버튼처럼 사용자가 직접 요청했으면 true — 백그라운드 쿨다운을 건너뛴다</param>
    Task<UsageSnapshot> GetSnapshotAsync(bool userInitiated, CancellationToken ct);
}
