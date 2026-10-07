using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Input;
using AiMeter.Models;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;

namespace AiMeter.Services;

// [Part 246] 트레이 아이콘 = 가장 높은 사용률을 링으로(시안 「아이콘 하나」). 70%↑ 노랑 / 90%↑ 빨강.
// 값을 하나도 모르면 링을 비워 둔다(0% 로 그리지 않는다).
// A 안(작업 표시줄 미니 스트립)은 2차 범위.
public sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon icon;
    private Icon? currentIcon;
    private IntPtr currentHandle;

    public event Action? LeftClick;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        icon = new TaskbarIcon
        {
            ToolTipText = "AiMeter",
            NoLeftClickDelay = true,
            ContextMenuMode = ContextMenuMode.PopupMenu,
            LeftClickCommand = new Command(() => LeftClick?.Invoke()),
        };

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = S.T("끝내기", "Exit"),
            Command = new Command(() => ExitRequested?.Invoke()),
        });
        icon.ContextFlyout = menu;

        Draw(null);
        icon.ForceCreate();
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
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var track = new Pen(Color.FromArgb(120, 148, 163, 184), 6f);
            g.DrawEllipse(track, 5, 5, 22, 22);

            if (usedPercent is double used)
            {
                var color = used >= 90 ? Color.FromArgb(0xF8, 0x71, 0x71)
                    : used >= 70 ? Color.FromArgb(0xFA, 0xCC, 0x15)
                    : Color.FromArgb(0x38, 0xBD, 0xF8);
                using var pen = new Pen(color, 6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                double fill = ShowUsed ? used : 100 - used; // [Part 254] 링 길이는 게이지 기준, 색은 쓴 양
                if (fill > 0) g.DrawArc(pen, 5, 5, 22, 22, -90, (float)(360 * Math.Min(fill, 100) / 100));
            }
        }

        IntPtr handle = bmp.GetHicon();
        var next = Icon.FromHandle(handle);
        icon.Icon = next;

        // GetHicon 으로 만든 핸들은 직접 해제해야 GDI 핸들이 새지 않는다
        currentIcon?.Dispose();
        if (currentHandle != IntPtr.Zero) DestroyIcon(currentHandle);
        currentIcon = next;
        currentHandle = handle;
    }

    public void Dispose()
    {
        icon.Dispose();
        currentIcon?.Dispose();
        if (currentHandle != IntPtr.Zero) DestroyIcon(currentHandle);
        currentHandle = IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private sealed class Command(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
