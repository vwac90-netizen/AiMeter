using System.Globalization;

namespace AiMeter;

// [Part 246] 문자열은 호출 지점에서 { ko, en } 쌍으로만 받는다 — 한쪽만 쓰는 것이 구조적으로 불가능하게(CLAUDE.md §4.1 프롬프트 쌍 규칙과 같은 이유).
// [Part 266] 언어는 설정(자동 · 한국어 · English)으로 정한다. 자동이면 Windows 표시 언어가 한국어일 때 ko, 그 밖은 en.
//   앱 시작 때 한 번만 Apply 한다 — 바꾸면 앱을 다시 시작한다(받아 온 라벨까지 한 번에 새 언어로).
internal static class S
{
    public static bool IsKorean { get; private set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";

    /// <summary>[Part 266] 날짜·요일·숫자 서식. 언어를 고르면 그 언어의 문화권 — Windows 지역 설정을 쓰면 「Sat」 자리에 「토」 가 섞였다.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentCulture;

    public static void Apply(string language)
    {
        switch (language)
        {
            case "ko":
                IsKorean = true;
                Culture = CultureInfo.GetCultureInfo("ko-KR");
                break;
            case "en":
                IsKorean = false;
                Culture = CultureInfo.GetCultureInfo("en-US");
                break;
            default:
                IsKorean = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
                // 자동: Windows 표시 언어와 지역이 어긋나면(한국어 표시 + 미국 지역 등) 요일이 섞이므로 표시 언어 쪽 문화권을 쓴다
                Culture = IsKorean == (CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "ko")
                    ? CultureInfo.CurrentCulture
                    : CultureInfo.GetCultureInfo(IsKorean ? "ko-KR" : "en-US");
                break;
        }
    }

    public static string T(string ko, string en) => IsKorean ? ko : en;
}
