using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using AiMeter.Models;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace AiMeter.Services;

// [Part 248] 시안 A — 작업 표시줄에 붙는 미니 스트립. 도구마다 「글자 + 창별 세로 막대(사용률) + 가장 높은 값」.
// - 도킹 방식은 tools/HwMonitorMini/TaskbarDocker.cs 와 같다: Shell_TrayWnd 의 자식으로 SetParent, 알림 영역(TrayNotifyWnd) 왼쪽.
// - 다른 점: HwMonitorMini 는 클릭 통과 + 크로마키 투명이다. 크로마키는 투명 픽셀에서 클릭이 아래로 빠져 「누르면 팝오버」 가 안 된다
//   → 픽셀 알파 레이어드 창(UpdateLayeredWindow)으로 그리고 배경 알파를 1 로 둔다(눈에 안 보이지만 전체가 클릭된다).
// - 붙이지 못하면(작업 표시줄 없음·세로) 조용히 숨는다. 트레이 링 아이콘은 이것과 무관하게 항상 남는다(튕김 금지).
// - 탐색기가 다시 시작되면 부모와 함께 이 창도 파괴된다 → 2초 틱에서 다시 만들어 붙인다.
public sealed class TaskbarStrip : IDisposable
{
    private const string ClassName = "AiMeter.TaskbarStrip";
    private static TaskbarStrip? instance;       // 창 프로시저(정적 대리자)가 찾아갈 대상 — 스트립은 하나뿐
    private static WndProc? procKeepAlive;       // 대리자가 GC 되면 창 프로시저 호출이 죽는다

    private readonly DispatcherQueueTimer timer;
    private readonly DispatcherQueue dispatcher;
    private int? appsRight;   // [Part 255] 작업 표시줄 앱 버튼 오른쪽 끝(화면 픽셀) — 모르면 null(제한 없음)
    private int appsTick;
    private bool appsBusy;
    private IntPtr hwnd;
    private IntPtr taskbar;
    private IReadOnlyList<UsageSnapshot> data = Array.Empty<UsageSnapshot>();
    private bool hover;
    private bool light;
    private Rectangle lastRect;
    private int lastDpi;

    public event Action? Click;

    public TaskbarStrip(DispatcherQueue dispatcher)
    {
        instance = this;
        this.dispatcher = dispatcher;
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (_, _) => Tick();
    }

    public bool IsDocked => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public void Show()
    {
        Tick();
        timer.Start();
    }

    public void Hide()
    {
        timer.Stop();
        DestroyStrip();
    }

    /// <summary>[Part 254] 게이지 기준 — true 면 쓴 양, false 면 남은 양을 막대 높이·숫자로. 색은 늘 쓴 양 기준(많이 쓸수록 경고색).</summary>
    public bool ShowUsed { get; set; }

    public void Update(IReadOnlyList<UsageSnapshot> snapshots)
    {
        data = snapshots;
        if (IsDocked) Layout(force: true);
    }

    /// <summary>[Part 255] 앱 버튼 끝을 백그라운드(MTA)에서 재고, 바뀌었으면 다시 배치한다. UI 스레드를 막지 않는다.</summary>
    private void RefreshAppsEdge()
    {
        if (appsBusy || taskbar == IntPtr.Zero) return;
        appsBusy = true;
        var tb = taskbar;
        Task.Run(() => TaskbarApps.RightEdge(tb)).ContinueWith(t => dispatcher.TryEnqueue(() =>
        {
            appsBusy = false;
            int? edge = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            if (edge == appsRight) return;
            appsRight = edge;
            try
            {
                if (IsDocked) Layout(force: true);
            }
            catch
            {
                // Tick 과 같은 규칙 — 그리기 실패가 앱을 죽이지 않는다
            }
        }));
    }

    private void Tick()
    {
        try
        {
            if (!IsDocked && !TryDock()) return;
            if (appsTick++ % 5 == 0) RefreshAppsEdge(); // [Part 255] 10초마다 — 창을 열고 닫으면 앱 버튼 수가 바뀐다
            bool nowLight = IsLightTaskbar();
            Layout(force: nowLight != light);
            light = nowLight;
        }
        catch
        {
            // 어떤 Win32 실패도 앱을 죽이지 않는다 — 다음 틱에 다시 시도
        }
    }

    private bool TryDock()
    {
        hwnd = IntPtr.Zero;
        taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var tr) || tr.Bottom - tr.Top > tr.Right - tr.Left) return false;

        EnsureClass();
        var created = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, "AiMeter", unchecked((uint)WS_POPUP),
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (created == IntPtr.Zero) return false;

        SetParent(created, taskbar);
        long style = GetWindowLongPtr(created, GWL_STYLE).ToInt64();
        SetWindowLongPtr(created, GWL_STYLE, new IntPtr((style & ~WS_POPUP) | WS_CHILD | WS_VISIBLE));
        hwnd = created;
        lastRect = Rectangle.Empty;
        light = IsLightTaskbar();
        Layout(force: true);
        return true;
    }

    private void DestroyStrip()
    {
        if (hwnd != IntPtr.Zero && IsWindow(hwnd)) DestroyWindow(hwnd);
        hwnd = IntPtr.Zero;
    }

    // ── 배치 + 그리기 ───────────────────────────────────────────

    private void Layout(bool force)
    {
        if (!GetWindowRect(taskbar, out var tr)) return;
        int dpi = (int)GetDpiForWindow(taskbar);
        float s = (dpi == 0 ? 96 : dpi) / 96f;
        int height = tr.Bottom - tr.Top;

        int trayLeft = tr.Right;
        var notify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && GetWindowRect(notify, out var nr)) trayLeft = nr.Left;

        // [Part 255] 앱 버튼 끝과 알림 영역 사이에 들어가는 만큼만 그린다(가운데 정렬 + 앱이 많으면 마지막 버튼을 덮었다, Part 254)
        float? room = appsRight is int ar ? trayLeft - (int)(4 * s) - (ar + 4 * s) : null; // [Part 256] 간격 8·6 → 4·4
        var segments = Fit(s, room);
        int width = Math.Max(1, (int)Math.Ceiling(Total(segments, s)));
        int x = Math.Clamp(trayLeft - tr.Left - width - (int)(4 * s), 0, Math.Max(0, tr.Right - tr.Left - width));
        var rect = new Rectangle(x, 0, width, height);

        if (rect != lastRect)
        {
            SetWindowPos(hwnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW);
            force = true;
        }
        if (dpi != lastDpi) force = true;
        lastRect = rect;
        lastDpi = dpi;
        if (force) Paint(rect.Size, s, segments);
    }

    private sealed record Segment(string Letter, double?[] Used, double? Max, float Width);

    /// <summary>[Part 254] 쓴 양 → 화면에 그릴 값(게이지 기준).</summary>
    private double Shown(double used) => ShowUsed ? used : 100 - used;

    private static float Total(List<Segment> segments, float s) => segments.Sum(x => x.Width) + 4 * s;

    /// <summary>[Part 255] 자리가 모자라면 단계적으로 줄인다. 자리를 모르면(앱 버튼을 못 읽음) 막대 1개 단계로 그린다(종전 동작).
    /// [Part 256] 사용자 「자리 넉넉하면 막대 여러 개로 해줘. 지금은 막대가 안보이네」 — 막대를 숫자보다 늦게 뺀다:
    ///   ①창마다 막대(시안 A) ②도구당 막대 1개 ③값 모르는 도구(「Cu –」) 빼기 ④숫자만 ⑤들어가는 도구까지만</summary>
    private List<Segment> Fit(float s, float? room)
    {
        if (room is not float r) return Measure(s, Bars.One, unknown: true);
        var multi = Measure(s, Bars.All, unknown: true);
        if (Total(multi, s) <= r) return multi;
        var one = Measure(s, Bars.One, unknown: true);
        if (Total(one, s) <= r) return one;
        var oneKnown = Measure(s, Bars.One, unknown: false);
        if (Total(oneKnown, s) <= r) return oneKnown;
        var known = Measure(s, Bars.None, unknown: false);
        if (Total(known, s) <= r) return known;
        var fit = new List<Segment>();
        float sum = 4 * s;
        foreach (var seg in known)
        {
            if (sum + seg.Width > r) break;
            fit.Add(seg);
            sum += seg.Width;
        }
        return fit;
    }

    private enum Bars { All, One, None }

    /// <summary>[Part 256] 글자 앞뒤 여백 없는 형식 — 잴 때와 그릴 때 같은 것을 쓴다(Part 254: 잴 때와 그릴 때가 다르면 잘린다).</summary>
    private static StringFormat Typo() => new(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };

    private List<Segment> Measure(float s, Bars bars, bool unknown)
    {
        using var typo = Typo();
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        using var letterFont = new Font("Segoe UI", 11 * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var numFont = new Font("Consolas", 13 * s, FontStyle.Bold, GraphicsUnit.Pixel);
        var list = new List<Segment>();
        foreach (var snap in data)
        {
            string letter = snap.Tool switch { "Claude Code" => "C", "Codex" => "X", "Antigravity" => "A", "Cursor" => "Cu", _ => snap.Tool[..1] };
            double?[] used = snap.Windows.Select(w => w.UsedPercent).ToArray();
            var known = used.Where(u => u is not null).Select(u => u!.Value).ToList();
            double? max = known.Count > 0 ? known.Max() : null;
            // [Part 250] 도구가 둘 이상이면 막대를 도구당 하나(가장 빠듯한 값)로 — 세 도구 막대 8개가 427px 로 늘어 작업 표시줄 앱 버튼을 덮었다
            // [Part 256] 이제는 자리를 재므로(Part 255) 넉넉하면 창마다 막대(Bars.All)
            if (bars == Bars.One && data.Count > 1) used = used.Length == 0 ? used : new[] { max };
            // [Part 251] 도구가 넷이 되자 값 없는 도구(「Cu –」·「A –」)까지 자리를 차지해 다시 429px → 작업 표시줄 앱 버튼을 덮었다.
            //   여럿일 때는 값을 아는 도구만 그린다 — 값이 없는 이유는 팝오버에 있다
            // [Part 254] 사용자 「Cursor는 왜 A 에 표기 안되는거야」 — 말없이 빼지 않고 막대 없이 「Cu –」 로 좁게 그린다.
            //   아예 빼고 싶으면 설정의 도구별 표시 스위치(Part 253)
            if (data.Count > 1 && max is null)
            {
                if (!unknown) continue; // [Part 255] 자리가 모자랄 때 가장 먼저 빠지는 것
                used = Array.Empty<double?>();
            }
            if (bars == Bars.None) used = Array.Empty<double?>(); // [Part 255] 자리가 모자라면 숫자만
            // [Part 254] 폭은 「그릴 숫자」(게이지 기준)로 잰다 — 쓴 양 0(한 자리)으로 재고 남은 양 100(세 자리)을 그려 끝 숫자가 잘렸다.
            //   좌우 여백도 8·6 → 5·3 으로 줄였다(Cu – 가 늘어난 만큼 앱 버튼 쪽으로 덜 밀리게)
            float w = 5 * s + g.MeasureString(letter, letterFont, PointF.Empty, typo).Width + 4 * s
                      + (used.Length == 0 ? 0 : used.Length * 5 * s + (used.Length - 1) * 3 * s + 6 * s)
                      + g.MeasureString(max is double m ? $"{Shown(m):0}" : "–", numFont, PointF.Empty, typo).Width + 3 * s;
            list.Add(new Segment(letter, used, max, w));
        }
        return list;
    }

    private void Paint(Size size, float s, List<Segment> segments)
    {
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // 투명 바탕에는 ClearType 을 쓸 수 없다
            // 알파 1 = 눈에 보이지 않지만 클릭은 받는다
            g.Clear(Color.FromArgb(1, 0, 0, 0));

            float barH = Math.Min(22 * s, size.Height * 0.5f);
            float top = (size.Height - barH) / 2f;
            if (hover)
            {
                using var hb = new SolidBrush(light ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255));
                using var path = Rounded(new RectangleF(1, top - 6 * s, size.Width - 2, barH + 12 * s), 6 * s);
                g.FillPath(hb, path);
            }

            var text = light ? Color.FromArgb(0x0F, 0x17, 0x2A) : Color.FromArgb(0xF8, 0xFA, 0xFC);
            var label = light ? Color.FromArgb(0x47, 0x55, 0x69) : Color.FromArgb(0x94, 0xA3, 0xB8);
            var track = light ? Color.FromArgb(70, 71, 85, 105) : Color.FromArgb(70, 148, 163, 184);
            using var letterFont = new Font("Segoe UI", 11 * s, FontStyle.Bold, GraphicsUnit.Pixel);
            using var numFont = new Font("Consolas", 13 * s, FontStyle.Bold, GraphicsUnit.Pixel);
            using var labelBrush = new SolidBrush(label);
            using var trackBrush = new SolidBrush(track);
            using var center = Typo(); // [Part 256] Measure 와 같은 형식

            float x = 2 * s;
            foreach (var seg in segments)
            {
                float cx = x + 5 * s; // [Part 254] 8 → 5 (Measure 와 같이)
                var lsz = g.MeasureString(seg.Letter, letterFont, PointF.Empty, center);
                g.DrawString(seg.Letter, letterFont, labelBrush, new RectangleF(cx, 0, lsz.Width, size.Height), center);
                cx += lsz.Width + 4 * s;

                foreach (var u in seg.Used)
                {
                    var bar = new RectangleF(cx, top, 5 * s, barH);
                    using (var tp = Rounded(bar, 2 * s)) g.FillPath(trackBrush, tp);
                    if (u is double used && Shown(used) > 0)
                    {
                        float fh = barH * (float)Math.Clamp(Shown(used), 0, 100) / 100f;
                        using var fb = new SolidBrush(Level(used));
                        using var fp = Rounded(new RectangleF(bar.X, bar.Bottom - fh, bar.Width, fh), Math.Min(2 * s, fh / 2));
                        g.FillPath(fb, fp);
                    }
                    else if (u is null)
                    {
                        using var pen = new Pen(label, Math.Max(1, s)) { DashStyle = DashStyle.Dot }; // 「모름」 — 비워 두되 점선으로
                        g.DrawRectangle(pen, bar.X, bar.Y, bar.Width, bar.Height);
                    }
                    cx += 8 * s;
                }
                if (seg.Used.Length > 0) cx += 3 * s;

                using var nb = new SolidBrush(seg.Max is double m ? (m >= 70 ? Level(m) : text) : label);
                g.DrawString(seg.Max is double mv ? $"{Shown(mv):0}" : "–", numFont, nb, new RectangleF(cx, 0, size.Width, size.Height), center);
                x += seg.Width;
            }
        }
        Present(bmp);
    }

    private Color Level(double used) => light
        ? (used >= 90 ? Color.FromArgb(0xDC, 0x26, 0x26) : used >= 70 ? Color.FromArgb(0xCA, 0x8A, 0x04) : Color.FromArgb(0x02, 0x84, 0xC7))
        : (used >= 90 ? Color.FromArgb(0xF8, 0x71, 0x71) : used >= 70 ? Color.FromArgb(0xFA, 0xCC, 0x15) : Color.FromArgb(0x38, 0xBD, 0xF8));

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>GDI+ 비트맵을 미리 곱한 알파(PArgb) DIB 로 옮겨 레이어드 창에 올린다.</summary>
    private void Present(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(screen, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, dib);
        try
        {
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    Marshal.Copy(row, 0, bits + y * w * 4, row.Length);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            var size = new SIZE { cx = w, cy = h };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(hwnd, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ── 창 프로시저 ────────────────────────────────────────────

    private static void EnsureClass()
    {
        if (procKeepAlive is not null) return;
        procKeepAlive = StaticWndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procKeepAlive),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, 32649 /* IDC_HAND */),
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
    }

    private static IntPtr StaticWndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = instance;
        switch (msg)
        {
            case 0x0021: // WM_MOUSEACTIVATE — 눌러도 포커스를 뺏지 않는다
                return new IntPtr(3); // MA_NOACTIVATE
            case 0x0200: // WM_MOUSEMOVE
                if (self is not null && !self.hover)
                {
                    self.hover = true;
                    var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 /* TME_LEAVE */, hwndTrack = h };
                    TrackMouseEvent(ref tme);
                    self.Layout(force: true);
                }
                return IntPtr.Zero;
            case 0x02A3: // WM_MOUSELEAVE
                if (self is not null)
                {
                    self.hover = false;
                    self.Layout(force: true);
                }
                return IntPtr.Zero;
            case 0x0202: // WM_LBUTTONUP
                self?.Click?.Invoke();
                return IntPtr.Zero;
        }
        return DefWindowProc(h, msg, wParam, lParam);
    }

    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Hide();
        if (instance == this) instance = null;
    }

    // ── Win32 ──────────────────────────────────────────────

    private const int GWL_STYLE = -16;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, IntPtr dstPoint, ref SIZE size, IntPtr srcDc,
        ref POINT srcPoint, int key, ref BLENDFUNCTION blend, int flags);
}
