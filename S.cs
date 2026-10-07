using System.Globalization;

namespace AiMeter;

// [Part 246] 문자열은 호출 지점에서 { ko, en } 쌍으로만 받는다 — 한쪽만 쓰는 것이 구조적으로 불가능하게(CLAUDE.md §4.1 프롬프트 쌍 규칙과 같은 이유).
// Windows 표시 언어가 한국어면 ko, 그 밖은 en.
internal static class S
{
    public static readonly bool IsKorean = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";

    public static string T(string ko, string en) => IsKorean ? ko : en;
}
