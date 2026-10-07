using AiMeter.Models;
using AiMeter.Providers;
using Microsoft.UI.Dispatching;

namespace AiMeter.Services;

// [Part 246] 공급자들을 1분마다 돌린다. 실제 네트워크 호출 빈도는 공급자가 스스로 제한한다(Claude = 5분에 1회).
public sealed class UsageService
{
    private readonly IUsageProvider[] providers;
    private readonly SettingsStore settings;
    private readonly DispatcherQueueTimer timer;
    private bool busy;

    public UsageService(IUsageProvider[] providers, SettingsStore settings, DispatcherQueue dispatcher)
    {
        this.providers = providers;
        this.settings = settings;
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMinutes(1);
        timer.Tick += async (_, _) => await RefreshAsync(false);
    }

    /// <summary>화면(스트립·트레이·팝오버)에 보일 도구 — [Part 250] 이 PC 에 설치되지 않은 도구와 [Part 253] 사용자가 「표시」 를 끈 도구는 뺀다.</summary>
    public IReadOnlyList<UsageSnapshot> Snapshots => All.Where(s => s.State != SnapshotState.NotInstalled && !settings.IsHidden(s.Tool)).ToList();

    /// <summary>설치·표시 여부와 무관한 전체 — 설정 「계정」 용.</summary>
    public IReadOnlyList<UsageSnapshot> All { get; private set; } = Array.Empty<UsageSnapshot>();

    /// <summary>UI 스레드에서 발생한다(await 가 UI 동기화 컨텍스트로 돌아오므로).</summary>
    public event Action? Updated;

    public void Start()
    {
        timer.Start();
        _ = RefreshAsync(false);
    }

    public async Task RefreshAsync(bool userInitiated)
    {
        if (busy) return;
        busy = true;
        try
        {
            // [Part 253] 숨긴 도구는 다시 묻지 않고 직전 값을 그대로 둔다(Antigravity 엔진 단독 실행 같은 비용을 아낀다).
            // 직전 값이 없으면(첫 실행) 계정 칸에 상태를 보여 주려고 한 번은 읽는다.
            var previous = All;
            var tasks = providers.Select((p, i) =>
                settings.IsHidden(p.Name) && i < previous.Count && previous[i].Tool == p.Name
                    ? Task.FromResult(previous[i])
                    : p.GetSnapshotAsync(userInitiated, CancellationToken.None));
            All = await Task.WhenAll(tasks);
            Updated?.Invoke();
        }
        finally
        {
            busy = false;
        }
    }
}
