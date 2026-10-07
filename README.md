# AiMeter

**AI 코딩 도구의 사용 한도를 Windows 작업 표시줄에서 바로 보는 트레이 앱.**
Claude Code · Codex · Cursor · Antigravity 의 「얼마나 남았나 / 언제 다시 차나」를 한곳에서 보여 줍니다.

> English summary is at the bottom.

PC Up Check([pcupcheck.com](https://pcupcheck.com)) 도구 모음의 하나입니다. macOS 앱 **AgentGauge** 를 벤치마킹해 Windows 용으로 만들었습니다(코드는 새로 작성).

> 벤치마킹 출처: AgentGauge — https://github.com/ghostface2232/AgentGauge

## 무엇을 보여 주나

| 위치 | 내용 |
| :--- | :--- |
| 작업 표시줄 줄 | 도구마다 글자(C Claude · X Codex · A Antigravity · Cu Cursor) + 막대 + 숫자. 자리가 넉넉하면 한도마다 막대, 모자라면 스스로 줄어 앱 버튼을 덮지 않습니다 |
| 트레이 링 아이콘 | 가장 빠듯한 한도 하나. 마우스를 올리면 도구별 값 |
| 팝오버(누르면 열림) | 셋 중 하나를 골라 씁니다 — **계기판**(언제 바닥나나) · **배터리**(언제 다시 차나, 7일 타임라인) · **트리맵**(이번 달 어디에 썼나, Claude Code 기록 기준) |

설정에서 고를 수 있는 것: 도구별 표시/숨김 · 게이지 기준(남은 양 / 쓴 양) · 초기화 표기(남은 시간 / 시각 / 둘 다) · 날짜 표기 · 작업 표시줄 줄 켜고 끄기 · Windows 시작 때 자동 실행.

**모르는 값은 모른다고 표시합니다.** 사용률을 받지 못하면 0% 가 아니라 「모름」(점선)으로 그립니다. 예: Cursor 무료 플랜은 한도 값을 주지 않아 늘 「모름」입니다.

## 사용량을 어디서 읽나

AiMeter 는 로그인하지 않습니다. 각 도구가 이 PC 에 이미 저장해 둔 로그인을 **읽기 전용으로** 쓰고, 그 도구가 쓰는 사용량 주소를 부릅니다.

| 도구 | 읽는 곳 | 부르는 곳 |
| :--- | :--- | :--- |
| Claude Code | `~/.claude/.credentials.json` | `api.anthropic.com/api/oauth/usage` (5분에 1번까지) |
| Codex | `~/.codex/auth.json` (`CODEX_HOME` 우선) | `chatgpt.com/backend-api/wham/usage` |
| Cursor | Cursor 의 `state.vscdb` | `cursor.com/api/usage-summary` |
| Antigravity | 실행 중인 Antigravity 엔진(이 PC 안 루프백) | 엔진이 없으면 설치된 엔진을 잠깐 띄워 묻고 바로 끝냅니다 |

- 토큰·계정 정보는 저장·기록·전송하지 않습니다. 디스크에 남는 것은 화면에 보이는 사용률·초기화 시각·플랜 이름(`%APPDATA%\AiMeter\`)뿐입니다.
- 트리맵은 켜야만(기본 꺼짐) `~/.claude/projects` 기록에서 **토큰 수만** 셉니다. 대화 내용은 저장·전송하지 않습니다.
- ⚠️ 위 사용량 주소들은 **공개 문서화된 API 가 아닙니다.** 각 회사가 바꾸면 값이 안 보일 수 있습니다(그때는 「모름」·안내 문구로 표시).

## 설치

1. [Releases](https://github.com/vwac90-netizen/AiMeter/releases) 에서 `AiMeter-<버전>-win-x64.zip` 을 받습니다.
2. 원하는 곳(예: `%LOCALAPPDATA%\Programs\AiMeter`)에 압축을 풀고 `AiMeter.exe` 를 실행합니다. 설치 과정·.NET 설치가 필요 없습니다(런타임 포함, 약 200MB).
3. 트레이(^)의 링 아이콘이나 작업 표시줄 줄을 누르면 팝오버가 열립니다.

요구 사항: Windows 10 1809 이상 / Windows 11, x64.

### ⚠️ 코드 서명이 없습니다

개인이 비영리로 만든 앱이라 아직 코드 서명 인증서가 없습니다.

- **스마트 앱 컨트롤**이 켜진 PC 에서는 Windows 가 실행을 막습니다. 끄는 것은 사용자 판단입니다(Windows 보안 → 앱 및 브라우저 컨트롤).
- 그 밖의 PC 에서는 「Windows 의 PC 보호」 파란 창이 뜰 수 있습니다 → 「추가 정보」 → 「실행」.
- 걱정되면 아래 「직접 빌드」로 소스에서 만들어 쓰셔도 됩니다. 릴리스마다 zip 의 SHA-256 을 적어 둡니다.

### 지우기

설정에서 「Windows 시작 때 자동 실행」을 끄고 → 트레이 메뉴 「끝내기」 → 폴더와 `%APPDATA%\AiMeter` 를 지웁니다.

## 직접 빌드

.NET 10 SDK 가 필요합니다.

```powershell
dotnet publish -c Release -r win-x64 -o out
# unpackaged WinUI 의 publish 는 AiMeter.pri 를 빠뜨릴 수 있어 csproj 의 CopyAppPriToPublish 대상이 복사합니다
.\out\AiMeter.exe
```

스택: .NET 10 · WinUI 3 (Windows App SDK, unpackaged · self-contained) · H.NotifyIcon.WinUI · System.Management.
코드 주석의 `[Part N]` 은 원 개발 기록(PC Up Check 저장소)의 항목 번호입니다.

## 상표

Claude·Claude Code 는 Anthropic, Codex·ChatGPT 는 OpenAI, Cursor 는 Anysphere, Antigravity·Gemini 는 Google 의 상표입니다. AiMeter 는 이 회사들과 관계가 없는 개인 프로젝트입니다.

## 라이선스

[MIT](LICENSE)

---

## English summary

AiMeter is a Windows app benchmarked on the macOS app [AgentGauge](https://github.com/ghostface2232/AgentGauge) (code written from scratch). It is a tray app that shows how much of your AI coding tool limits (Claude Code, Codex, Cursor, Antigravity) is left and when it refills — on a taskbar strip, a tray ring icon and a popup (gauges / battery + 7-day timeline / monthly token treemap). Unknown values are shown as "unknown", never as 0%.

It reuses the sign-in each tool already stored on your PC (read-only) and calls the same usage endpoints those tools use. These endpoints are **not documented public APIs** and may change. Tokens are never stored, logged or sent anywhere else.

Download the zip from Releases, extract, run `AiMeter.exe` (self-contained, no .NET install). **The app is not code-signed yet**: Smart App Control blocks it, and SmartScreen may warn ("More info" → "Run anyway"). You can also build it yourself with the .NET 10 SDK. Not affiliated with Anthropic, OpenAI, Anysphere or Google. MIT licensed.
