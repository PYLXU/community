using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// UI 联动调试面板「鼠标抓取元素」用的低级鼠标钩子（WH_MOUSE_LL）。
    /// <para>
    /// 抓取开启期间：左键按下时回调 <see cref="LeftButtonDownHandler"/>（参数为屏幕物理像素坐标）。
    /// 回调返回 true 表示吞掉这次点击，不再传递给目标应用——抓取时不应误触目标应用的界面
    /// （如 Word 功能区按钮）。右键按下回调 <see cref="RightButtonDownHandler"/>，供调用方退出抓取。
    /// </para>
    /// <para>
    /// 钩子回调运行在安装它的线程（本应用 UI 线程）上，系统对低级钩子有超时限制，
    /// 因此回调内只允许做极廉价的判断（命中自身界面、Win32 取窗），耗时的 UIA 采集必须
    /// 交给调用方投递到后台线程。回调抛出的异常一律被吞掉，避免影响全局鼠标输入。
    /// </para>
    /// </summary>
    internal sealed class UILinkageMouseGrabHook : IDisposable
    {
        private const int WhMouseLl = 14;

        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmLButtonDblClk = 0x0203;
        private const int WmRButtonDown = 0x0204;

        private readonly LowLevelMouseProc _hookProc;
        private IntPtr _hookHandle;

        /// <summary>左键按下是否已被吞掉：其后的抬起/双击消息需同样吞掉，避免消息配对错乱。</summary>
        private bool _swallowLeftButton;

        private bool _isDisposed;

        internal UILinkageMouseGrabHook()
        {
            _hookProc = HookCallback;
        }

        /// <summary>
        /// 左键按下回调：参数为屏幕物理像素坐标，返回 true 表示吞掉这次点击。
        /// 必须快速返回（见类型注释）。
        /// </summary>
        internal Func<int, int, bool> LeftButtonDownHandler { get; set; }

        /// <summary>
        /// 右键按下回调：参数为屏幕物理像素坐标，返回 true 表示吞掉这次点击。
        /// </summary>
        internal Func<int, int, bool> RightButtonDownHandler { get; set; }

        internal bool IsInstalled => _hookHandle != IntPtr.Zero;

        internal bool Install()
        {
            if (_isDisposed || IsInstalled) return IsInstalled;

            IntPtr moduleHandle = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
            _hookHandle = SetWindowsHookEx(WhMouseLl, _hookProc, moduleHandle, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                LogHelper.WriteLogToFile(
                    $"UI联动鼠标抓取 | 安装低级鼠标钩子失败: {new Win32Exception(error).Message} ({error})",
                    LogHelper.LogType.Error);
                return false;
            }

            _swallowLeftButton = false;
            LogHelper.WriteLogToFile("UI联动鼠标抓取 | 低级鼠标钩子已启用", LogHelper.LogType.Event);
            return true;
        }

        internal void Uninstall()
        {
            _swallowLeftButton = false;

            if (!IsInstalled) return;

            IntPtr hookHandle = _hookHandle;
            _hookHandle = IntPtr.Zero;

            if (!UnhookWindowsHookEx(hookHandle))
            {
                int error = Marshal.GetLastWin32Error();
                LogHelper.WriteLogToFile(
                    $"UI联动鼠标抓取 | 卸载低级鼠标钩子失败: {new Win32Exception(error).Message} ({error})",
                    LogHelper.LogType.Warning);
                return;
            }

            LogHelper.WriteLogToFile("UI联动鼠标抓取 | 低级鼠标钩子已禁用", LogHelper.LogType.Event);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && IsInstalled)
            {
                try
                {
                    int message = wParam.ToInt32();
                    if (message == WmLButtonDown || message == WmLButtonDblClk || message == WmRButtonDown)
                    {
                        var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                        int x = data.Point.X;
                        int y = data.Point.Y;

                        if (message == WmRButtonDown)
                        {
                            bool swallowRight = RightButtonDownHandler?.Invoke(x, y) == true;
                            if (swallowRight) return (IntPtr)1;
                        }
                        else
                        {
                            // 双击的第二次按下沿用首次的判定结果，避免抓取中途放行导致目标应用收到半截点击。
                            bool swallowLeft = message == WmLButtonDblClk && _swallowLeftButton
                                ? true
                                : LeftButtonDownHandler?.Invoke(x, y) == true;
                            _swallowLeftButton = swallowLeft;
                            if (swallowLeft) return (IntPtr)1;
                        }
                    }
                    else if (message == WmLButtonUp && _swallowLeftButton)
                    {
                        _swallowLeftButton = false;
                        return (IntPtr)1;
                    }
                }
                catch (Exception ex)
                {
                    // 钩子回调内绝不允许异常外泄：仅记录并继续传递消息。
                    try
                    {
                        LogHelper.WriteLogToFile(
                            $"UI联动鼠标抓取 | 钩子回调异常: {ex.Message}", LogHelper.LogType.Warning);
                    }
                    catch
                    {
                        // 日志本身失败时忽略。
                    }
                }
            }

            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            Uninstall();
            _isDisposed = true;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct Point32
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MsllHookStruct
        {
            public Point32 Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook,
            LowLevelMouseProc lpfn,
            IntPtr hMod,
            uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
