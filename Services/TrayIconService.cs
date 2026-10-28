using System.Windows.Input;
using AiMeter.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AiMeter.Services;

// [Part 246] 트레이 아이콘 = 가장 높은 사용률을 링으로(시안 「아이콘 하나」). 70%↑ 노랑 / 90%↑ 빨강.
// 값을 하나도 모르면 링을 비워 둔다(0% 로 그리지 않는다).
// A 안(작업 표시줄 미니 스트립)은 2차 범위.
// [Part 287] H.NotifyIcon(WinUI) → Avalonia TrayIcon. 링은 GDI+ 대신 Avalonia 그리기 — 맥 메뉴 막대에서도 같은 코드로 그린다.
public sealed class TrayIconService : IDisposable
{
    private readonly TrayIcon icon;
    private readonly TrayIcons icons;

    public event Action? LeftClick;
    public event Action? ExitRequested;

    public TrayIconService(Application app)
    {
        icon = new TrayIcon
        {
            ToolTipText = "AiMeter",
            Menu = new NativeMenu
            {
                new NativeMenuItem(S.T("끝내기", "Exit")) { Command = new Command(() => ExitRequested?.Invoke()) },
            },
        };
        icon.Clicked += (_, _) => LeftClick?.Invoke();

        Draw(null);
        icons = new TrayIcons { icon };
        TrayIcon.SetIcons(app, icons);
        icon.IsVisible = true;
    }

    /// <summary>[Part 254] 게이지 기준 — true 면 쓴 양, false 면 남은 양을 링 길이·툴팁 숫자로. 색은 늘 쓴 양 기준.</summary>
    public bool ShowUsed { get; set; }

    public void Update(IReadOnlyList<UsageSnapshot> snapshots)
    {
        var known = snapshots.SelectMany(s => s.Windows)
            .Where(w => w.UsedPercent is not null)
            .ToList();
        double? max = known.Count > 0 ? known.Max(w => w.UsedPercent!.Value) : null;
        Draw(max);

        var lines = new List<string> { ShowUsed ? S.T("AiMeter · 쓴 양", "AiMeter · used") : S.T("AiMeter · 남은 양", "AiMeter · left") };
        foreach (var s in snapshots)
        {
            if (s.Windows.Count == 0)
            {
                lines.Add($"{s.Tool}: {S.T("값 없음", "no data")}");
                continue;
            }
            var parts = s.Windows.Select(w => $"{w.Label} {(w.UsedPercent is double u ? $"{(ShowUsed ? u : 100 - u):0}%" : S.T("모름", "?"))}");
            lines.Add($"{s.Tool} — {string.Join(" · ", parts)}");
        }
        // 트레이 툴팁은 127자 제한
        var tip = string.Join("\n", lines);
        icon.ToolTipText = tip.Length > 127 ? tip[..126] + "…" : tip;
    }

    private void Draw(double? usedPercent)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(32, 32));
        using (var g = bmp.CreateDrawingContext())
        {
            var center = new Point(16, 16);
            g.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(120, 148, 163, 184)), 6), center, 11, 11);

            if (usedPercent is double used)
            {
                var color = used >= 90 ? Color.FromRgb(0xF8, 0x71, 0x71)
                    : used >= 70 ? Color.FromRgb(0xFA, 0xCC, 0x15)
                    : Color.FromRgb(0x38, 0xBD, 0xF8);
                double fill = ShowUsed ? used : 100 - used; // [Part 254] 링 길이는 게이지 기준, 색은 쓴 양
                if (fill > 0) g.DrawGeometry(null, new Pen(new SolidColorBrush(color), 6, lineCap: PenLineCap.Round), Arc(center, 11, Math.Min(fill, 100) / 100));
            }
        }
        icon.Icon = new WindowIcon(bmp);
    }

    /// <summary>12시 방향에서 시계 방향으로 fraction(0~1) 만큼의 호.</summary>
    private static Geometry Arc(Point c, double r, double fraction)
    {
        if (fraction >= 0.9999) return new EllipseGeometry(new Rect(c.X - r, c.Y - r, r * 2, r * 2));
        double a = 2 * Math.PI * fraction;
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false);
            ctx.ArcTo(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        return geo;
    }

    public void Dispose()
    {
        icon.IsVisible = false;
        icon.Dispose();
    }

    private sealed class Command(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
