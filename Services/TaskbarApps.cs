using System.Runtime.InteropServices;

namespace AiMeter.Services;

// [Part 255] 작업 표시줄 앱 버튼이 어디서 끝나는지 — 스트립이 앱 버튼을 덮지 않게 남은 폭을 잰다.
// - Windows 11 은 앱 버튼을 XAML 로 그린다. Win32 자식 창 MSTaskSwWClass 의 사각형은 낡은 값이었다(2026-10-07 실측: 창은 1748 에서 끝, 실제 마지막 버튼은 1925).
// - 그래서 UI Automation 으로 ClassName = "Taskbar.TaskListButtonAutomationPeer" 버튼들을 찾아 오른쪽 끝의 최댓값을 쓴다(실측 25ms).
// - UIAutomationClient.dll(WPF 쪽)을 붙이지 않으려고 COM IUIAutomation 을 필요한 vtable 슬롯까지만 직접 선언한다.
//   슬롯 순서는 Windows SDK UIAutomationClient.h 그대로 — 쓰지 않는 슬롯은 자리만 채운다(순서가 어긋나면 엉뚱한 함수가 불린다).
// - 실패하면 null — 호출하는 쪽은 종전처럼 제한 없이 그린다.
internal static class TaskbarApps
{
    private const int TreeScopeDescendants = 4;
    private const int UIA_ClassNamePropertyId = 30012;
    private const int UIA_BoundingRectanglePropertyId = 30001;

    /// <summary>작업 표시줄 앱 버튼들의 오른쪽 끝(화면 물리 픽셀). 버튼이 없거나 읽지 못하면 null.</summary>
    public static int? RightEdge(IntPtr taskbar)
    {
        try
        {
            var automation = (IUIAutomation)new CUIAutomation();
            var root = automation.ElementFromHandle(taskbar);
            var condition = automation.CreatePropertyCondition(UIA_ClassNamePropertyId, "Taskbar.TaskListButtonAutomationPeer");
            var found = root.FindAll(TreeScopeDescendants, condition);
            int? right = null;
            for (int i = 0; i < found.Length; i++)
            {
                // BoundingRectangle = double[4] { left, top, width, height }
                if (found.GetElement(i).GetCurrentPropertyValue(UIA_BoundingRectanglePropertyId) is double[] r && r.Length == 4 && r[2] > 0)
                {
                    int edge = (int)Math.Ceiling(r[0] + r[2]);
                    if (right is null || edge > right) right = edge;
                }
            }
            return right;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
        {
            return null;
        }
    }

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation
    {
    }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();             // 0
        void CompareRuntimeIds();           // 1
        void GetRootElement();              // 2
        IUIAutomationElement ElementFromHandle(IntPtr hwnd); // 3
        void ElementFromPoint();            // 4
        void GetFocusedElement();           // 5
        void GetRootElementBuildCache();    // 6
        void ElementFromHandleBuildCache(); // 7
        void ElementFromPointBuildCache();  // 8
        void GetFocusedElementBuildCache(); // 9
        void CreateTreeWalker();            // 10
        void get_ControlViewWalker();       // 11
        void get_ContentViewWalker();       // 12
        void get_RawViewWalker();           // 13
        void get_RawViewCondition();        // 14
        void get_ControlViewCondition();    // 15
        void get_ContentViewCondition();    // 16
        void CreateCacheRequest();          // 17
        void CreateTrueCondition();         // 18
        void CreateFalseCondition();        // 19
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value); // 20
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();                    // 0
        void GetRuntimeId();                // 1
        void FindFirst();                   // 2
        IUIAutomationElementArray FindAll(int scope, [MarshalAs(UnmanagedType.IUnknown)] object condition); // 3
        void FindFirstBuildCache();         // 4
        void FindAllBuildCache();           // 5
        void BuildUpdatedCache();           // 6
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId); // 7
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length { get; }                 // 0
        IUIAutomationElement GetElement(int index); // 1
    }
}
