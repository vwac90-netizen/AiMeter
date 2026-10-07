using System.Text.Json;

namespace AiMeter.Services;

// [Part 246] 사용자 설정. %APPDATA%\AiMeter\settings.json — 토큰·계정 정보는 절대 넣지 않는다.
public sealed class SettingsStore
{
    public const string ViewGauge = "gauge";     // 시안 B · 계기판
    public const string ViewBattery = "battery"; // 시안 C · 배터리 + 초기화 타임라인
    public const string ViewSpend = "spend";     // [Part 248] 시안 D · 도구별 트리맵(로컬 기록)

    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AiMeter");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public string View { get; set; } = ViewGauge;

    /// <summary>[Part 248] 시안 A · 작업 표시줄 미니 스트립. 트레이 링 아이콘은 이 값과 무관하게 항상 있다.</summary>
    public bool TaskbarStrip { get; set; } = true;

    /// <summary>[Part 248] 시안 D 를 위해 로컬 기록(~/.claude/projects)의 토큰 수를 읽어도 되는가. 기본 꺼짐 — 사용자가 켠다.</summary>
    public bool ReadLocalLogs { get; set; }

    /// <summary>[Part 248] 시안 D 를 무엇으로 나눌까 — "tool"(AI별) · "model"(모델별).</summary>
    public string SpendBy { get; set; } = SpendByTool;

    public const string SpendByTool = "tool";
    public const string SpendByModel = "model";

    /// <summary>[Part 253] 사용자가 「표시」 를 끈 도구 이름(예: "Cursor"). 팝오버·작업 표시줄 스트립·트레이 링 아이콘에서 모두 빠진다.</summary>
    public List<string> HiddenTools { get; set; } = new();

    public bool IsHidden(string tool) => HiddenTools.Contains(tool);

    /// <summary>[Part 254] 초기화 표기 — auto(24시간 안은 남은 시간, 그 밖은 시각) · remain(남은 시간) · clock(시각) · both(시각 · 남은 시간).</summary>
    public string ResetStyle { get; set; } = ResetAuto;

    public const string ResetAuto = "auto";
    public const string ResetRemain = "remain";
    public const string ResetClock = "clock";
    public const string ResetBoth = "both";

    /// <summary>[Part 254] 먼 날짜 표기 — short(11-6(금)) · long(11월 6일(금)).</summary>
    public string DateStyle { get; set; } = DateShort;

    public const string DateShort = "short";
    public const string DateLong = "long";

    /// <summary>[Part 254] 게이지 기준 — remaining(남은 양) · used(쓴 양). 팝오버·작업 표시줄 스트립·트레이 링 아이콘이 모두 따른다.
    /// 색은 기준과 무관하게 「남은 양이 적을수록 경고색」.</summary>
    public string GaugeBasis { get; set; } = BasisRemaining;

    public const string BasisRemaining = "remaining";
    public const string BasisUsed = "used";

    /// <summary>[Part 266] 언어 — auto(Windows 표시 언어) · ko · en. 바꾸면 앱을 다시 시작한다.</summary>
    public string Language { get; set; } = LangAuto;

    public const string LangAuto = "auto";
    public const string LangKo = "ko";
    public const string LangEn = "en";

    [System.Text.Json.Serialization.JsonIgnore] // [Part 255] 계산값 — 파일에 쓰면 GaugeBasis 와 엇갈려 보였다("ShowUsed": true + "remaining")
    public bool ShowUsed => GaugeBasis == BasisUsed;

    public static SettingsStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<SettingsStore>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    if (loaded.View is not (ViewGauge or ViewBattery or ViewSpend)) loaded.View = ViewGauge;
                    if (loaded.SpendBy is not (SpendByTool or SpendByModel)) loaded.SpendBy = SpendByTool;
                    loaded.HiddenTools ??= new();
                    if (loaded.ResetStyle is not (ResetAuto or ResetRemain or ResetClock or ResetBoth)) loaded.ResetStyle = ResetAuto;
                    if (loaded.DateStyle is not (DateShort or DateLong)) loaded.DateStyle = DateShort;
                    if (loaded.GaugeBasis is not (BasisRemaining or BasisUsed)) loaded.GaugeBasis = BasisRemaining;
                    if (loaded.Language is not (LangAuto or LangKo or LangEn)) loaded.Language = LangAuto;
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 읽지 못하면 기본값으로 시작한다 — 파일은 다음 저장 때 덮어쓴다
        }
        return new SettingsStore();
    }

    /// <returns>저장에 실패하면 false — 화면이 알릴 수 있게</returns>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
