using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>指针事件阶段。</summary>
    internal enum WetInkPointerPhase
    {
        Down,
        Update,
        Up,
        CaptureLost
    }

    /// <summary>输入回调：由输入源在 UI 线程同步调用。</summary>
    /// <returns>true 表示引擎已消费该消息，不再交给 WPF。</returns>
    internal delegate bool WetInkPointerHandler(WetInkPointerPhase phase, WetInkPointerBatch batch);

    /// <summary>
    /// WM_POINTER + WM_INPUT 原生输入源。
    ///
    /// 两条已实证的生命周期铁律（旧系统踩过的坑，务必保持）：
    /// 1) 指针历史必须读全量（最多 MaximumHistoryEntries），缓冲不足时 API 只返回
    ///    最新若干条，较早的永久丢失 → 快速画弧会「拐角变直线」。
    /// 2) WM_INPUT 原始鼠标只用于喂 Update / 接触标志；会话的 Down/Up 一律走
    ///    成对可靠的 WM_LBUTTONDOWN / WM_LBUTTONUP，否则 Up 丢失会导致
    ///    「只能写一笔」（下一笔的 Begin 会取消上一笔）。
    /// </summary>
    internal sealed class WetInkPointerInputSource : IDisposable
    {
        // ---- window messages ----
        private const int WmPointerUpdate = 0x0245;
        private const int WmPointerDown = 0x0246;
        private const int WmPointerUp = 0x0247;
        private const int WmPointerCaptureChanged = 0x024C;
        private const int WmMouseMove = 0x0200;
        private const int WmLeftButtonDown = 0x0201;
        private const int WmLeftButtonUp = 0x0202;
        private const int WmInput = 0x00FF;

        // ---- pointer flags ----
        private const uint PointerFlagInContact = 0x00000004;
        private const uint PointerFlagSecondButton = 0x00000020;
        private const uint PointerFlagCanceled = 0x00008000;

        private const uint PenFlagBarrel = 0x00000001;
        private const uint PenMaskPressure = 0x00000001;

        /// <summary>笔/触摸提升为鼠标消息的签名（低 8 位是设备序号）。</summary>
        private const uint MouseEventFromTouchSignature = 0xFF515700;
        private const uint MouseEventSignatureMask = 0xFFFFFF00;

        /// <summary>历史条目上限。绝不可下调——旧系统截断到 128 导致永久丢段。</summary>
        private const int MaximumHistoryEntries = 4096;

        public WetInkPointerInputSource(
            HwndSource hwndSource,
            WetInkPointerHandler handler,
            Func<double> dpiScaleXProvider,
            Func<double> dpiScaleYProvider)
        {
            _hwndSource = hwndSource ?? throw new ArgumentNullException(nameof(hwndSource));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _dpiScaleX = dpiScaleXProvider ?? (() => 1.0);
            _dpiScaleY = dpiScaleYProvider ?? (() => 1.0);

            _hwndSource.AddHook(WndProc);
        }

        private readonly HwndSource _hwndSource;
        private readonly WetInkPointerHandler _handler;
        private readonly Func<double> _dpiScaleX;
        private readonly Func<double> _dpiScaleY;

        /// <summary>每个 pointerId 的帧序号，用于历史去重。</summary>
        private readonly Dictionary<uint, uint> _frameIds = new Dictionary<uint, uint>();

        /// <summary>鼠标（非指针）会话是否处于按下状态。</summary>
        private bool _mouseInContact;

        private bool _disposed;

        /// <summary>鼠标会话使用的固定 pointerId（不与真实 pointerId 冲突）。</summary>
        public const uint MousePointerId = 0xFFFF0001;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_disposed)
                return IntPtr.Zero;

            try
            {
                switch (msg)
                {
                    case WmPointerDown:
                        if (HandlePointerMessage(WetInkPointerPhase.Down, wParam))
                            handled = true;
                        break;

                    case WmPointerUpdate:
                        if (HandlePointerMessage(WetInkPointerPhase.Update, wParam))
                            handled = true;
                        break;

                    case WmPointerUp:
                        if (HandlePointerMessage(WetInkPointerPhase.Up, wParam))
                            handled = true;
                        break;

                    case WmPointerCaptureChanged:
                        HandlePointerMessage(WetInkPointerPhase.CaptureLost, wParam);
                        break;

                    case WmLeftButtonDown:
                        if (HandleMouseMessage(WetInkPointerPhase.Down, lParam))
                            handled = true;
                        break;

                    case WmMouseMove:
                        if (_mouseInContact && HandleMouseMessage(WetInkPointerPhase.Update, lParam))
                            handled = true;
                        break;

                    case WmLeftButtonUp:
                        if (HandleMouseMessage(WetInkPointerPhase.Up, lParam))
                            handled = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WetInk] input source error: {ex}");
            }

            return IntPtr.Zero;
        }

        // ------------------------------------------------------------------
        // WM_POINTER (pen / touch)
        // ------------------------------------------------------------------

        private bool HandlePointerMessage(WetInkPointerPhase phase, IntPtr wParam)
        {
            var pointerId = (uint)(wParam.ToInt64() & 0xFFFF);

            if (!GetPointerType(pointerId, out var pointerType))
                return false;

            switch (pointerType)
            {
                case PointerInputType.PT_PEN:
                    return HandlePenPointer(phase, pointerId);
                case PointerInputType.PT_TOUCH:
                    return HandleTouchPointer(phase, pointerId);
                default:
                    // 鼠标类指针交给传统鼠标消息处理，避免双份会话。
                    return false;
            }
        }

        private bool HandlePenPointer(WetInkPointerPhase phase, uint pointerId)
        {
            var entriesRequested = MaximumHistoryEntries;
            var history = new POINTER_PEN_INFO[entriesRequested];

            if (!GetPointerPenInfoHistory(pointerId, ref entriesRequested, history))
            {
                // 缓冲不足时 API 会写回所需条数；重试一次读全量。
                if (entriesRequested > 0 && entriesRequested <= MaximumHistoryEntries)
                {
                    history = new POINTER_PEN_INFO[entriesRequested];
                    if (!GetPointerPenInfoHistory(pointerId, ref entriesRequested, history))
                        return false;
                }
                else
                {
                    return false;
                }
            }

            var count = Math.Min(entriesRequested, history.Length);
            if (count <= 0)
                return false;

            var samples = new List<WetInkSample>(count);
            var frameId = NextFrameId(pointerId);
            var barrelDown = false;
            var canceled = false;

            for (var i = 0; i < count; i++)
            {
                var pen = history[i];
                var info = pen.pointerInfo;

                var flags = WetInkSampleFlags.None;
                if ((info.pointerFlags & PointerFlagInContact) != 0)
                    flags |= WetInkSampleFlags.InContact;
                if ((info.pointerFlags & PointerFlagCanceled) != 0)
                {
                    flags |= WetInkSampleFlags.Canceled;
                    canceled = true;
                }
                if ((pen.penFlags & PenFlagBarrel) != 0 ||
                    (info.pointerFlags & PointerFlagSecondButton) != 0)
                {
                    flags |= WetInkSampleFlags.BarrelButton;
                    barrelDown = true;
                }

                var hasPressure = (pen.penMask & PenMaskPressure) != 0 && pen.pressure > 0;
                var pressure = hasPressure ? pen.pressure / 1024f : 0.5f;

                var (xDip, yDip) = ToClientDip(info.ptPixelLocation);

                samples.Add(new WetInkSample(
                    pointerId,
                    WetInkInputKind.Pen,
                    xDip,
                    yDip,
                    pressure,
                    hasPressure,
                    WetInkTimestampConverter.QueryPerformanceCounterToMicroseconds(
                        (long)info.PerformanceCount),
                    frameId,
                    flags,
                    0,
                    0));
            }

            var batch = new WetInkPointerBatch(
                pointerId,
                WetInkInputKind.Pen,
                samples,
                isPromotedMouse: false,
                isSecondaryBarrelButtonDown: barrelDown,
                historyIncomplete: entriesRequested > history.Length,
                historyError: false);

            if (canceled && phase == WetInkPointerPhase.Update)
                phase = WetInkPointerPhase.CaptureLost;

            return _handler(phase, batch);
        }

        private bool HandleTouchPointer(WetInkPointerPhase phase, uint pointerId)
        {
            var entriesRequested = MaximumHistoryEntries;
            var history = new POINTER_TOUCH_INFO[entriesRequested];

            if (!GetPointerTouchInfoHistory(pointerId, ref entriesRequested, history))
            {
                if (entriesRequested > 0 && entriesRequested <= MaximumHistoryEntries)
                {
                    history = new POINTER_TOUCH_INFO[entriesRequested];
                    if (!GetPointerTouchInfoHistory(pointerId, ref entriesRequested, history))
                        return false;
                }
                else
                {
                    return false;
                }
            }

            var count = Math.Min(entriesRequested, history.Length);
            if (count <= 0)
                return false;

            var samples = new List<WetInkSample>(count);
            var frameId = NextFrameId(pointerId);
            var canceled = false;

            for (var i = 0; i < count; i++)
            {
                var touch = history[i];
                var info = touch.pointerInfo;

                var flags = WetInkSampleFlags.None;
                if ((info.pointerFlags & PointerFlagInContact) != 0)
                    flags |= WetInkSampleFlags.InContact;
                if ((info.pointerFlags & PointerFlagCanceled) != 0)
                {
                    flags |= WetInkSampleFlags.Canceled;
                    canceled = true;
                }

                // 触摸没有真压感；接触区尺寸交给分类器判手指/手掌。
                var contactWidth = Math.Max(0, touch.rcContact.right - touch.rcContact.left);
                var contactHeight = Math.Max(0, touch.rcContact.bottom - touch.rcContact.top);

                var (xDip, yDip) = ToClientDip(info.ptPixelLocation);

                samples.Add(new WetInkSample(
                    pointerId,
                    WetInkInputKind.Touch,
                    xDip,
                    yDip,
                    0.5f,
                    hasPressure: false,
                    WetInkTimestampConverter.QueryPerformanceCounterToMicroseconds(
                        (long)info.PerformanceCount),
                    frameId,
                    flags,
                    contactWidth,
                    contactHeight));
            }

            var batch = new WetInkPointerBatch(
                pointerId,
                WetInkInputKind.Touch,
                samples,
                isPromotedMouse: false,
                isSecondaryBarrelButtonDown: false,
                historyIncomplete: entriesRequested > history.Length,
                historyError: false);

            if (canceled && phase == WetInkPointerPhase.Update)
                phase = WetInkPointerPhase.CaptureLost;

            return _handler(phase, batch);
        }

        // ------------------------------------------------------------------
        // Legacy mouse (session lifecycle is ALWAYS driven from here)
        // ------------------------------------------------------------------

        private bool HandleMouseMessage(WetInkPointerPhase phase, IntPtr lParam)
        {
            // 提升自笔/触摸的鼠标消息要丢弃，否则一次落笔会产生两条会话。
            if (IsPromotedMouseMessage())
                return false;

            switch (phase)
            {
                case WetInkPointerPhase.Down:
                    _mouseInContact = true;
                    break;
                case WetInkPointerPhase.Up:
                    if (!_mouseInContact)
                        return false;
                    _mouseInContact = false;
                    break;
            }

            var x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            var y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));

            var flags = phase == WetInkPointerPhase.Up
                ? WetInkSampleFlags.None
                : WetInkSampleFlags.InContact;

            var sample = new WetInkSample(
                MousePointerId,
                WetInkInputKind.Mouse,
                x / _dpiScaleX(),
                y / _dpiScaleY(),
                0.5f,
                hasPressure: false,
                WetInkTimestampConverter.NowMicroseconds(),
                NextFrameId(MousePointerId),
                flags,
                0,
                0);

            var batch = new WetInkPointerBatch(
                MousePointerId,
                WetInkInputKind.Mouse,
                new[] { sample },
                isPromotedMouse: false,
                isSecondaryBarrelButtonDown: false,
                historyIncomplete: false,
                historyError: false);

            return _handler(phase, batch);
        }

        /// <summary>当前消息是否由笔/触摸提升而来。</summary>
        private static bool IsPromotedMouseMessage()
        {
            var extra = (uint)GetMessageExtraInfo().ToInt64();
            return (extra & MouseEventSignatureMask) == MouseEventFromTouchSignature;
        }

        private uint NextFrameId(uint pointerId)
        {
            _frameIds.TryGetValue(pointerId, out var current);
            current++;
            _frameIds[pointerId] = current;
            return current;
        }

        private (double X, double Y) ToClientDip(POINT screenPixel)
        {
            var pt = screenPixel;
            ScreenToClient(_hwndSource.Handle, ref pt);
            return (pt.x / _dpiScaleX(), pt.y / _dpiScaleY());
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                _hwndSource.RemoveHook(WndProc);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WetInk] input source dispose: {ex}");
            }

            _frameIds.Clear();
        }

        // ------------------------------------------------------------------
        // interop
        // ------------------------------------------------------------------

        private enum PointerInputType
        {
            PT_POINTER = 1,
            PT_TOUCH = 2,
            PT_PEN = 3,
            PT_MOUSE = 4,
            PT_TOUCHPAD = 5
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_INFO
        {
            public uint pointerType;
            public uint pointerId;
            public uint frameId;
            public uint pointerFlags;
            public IntPtr sourceDevice;
            public IntPtr hwndTarget;
            public POINT ptPixelLocation;
            public POINT ptHimetricLocation;
            public POINT ptPixelLocationRaw;
            public POINT ptHimetricLocationRaw;
            public uint dwTime;
            public uint historyCount;
            public int InputData;
            public uint dwKeyStates;
            public ulong PerformanceCount;
            public int ButtonChangeType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_PEN_INFO
        {
            public POINTER_INFO pointerInfo;
            public uint penFlags;
            public uint penMask;
            public uint pressure;
            public uint rotation;
            public int tiltX;
            public int tiltY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_TOUCH_INFO
        {
            public POINTER_INFO pointerInfo;
            public uint touchFlags;
            public uint touchMask;
            public RECT rcContact;
            public RECT rcContactRaw;
            public uint orientation;
            public uint pressure;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPointerType(uint pointerId, out PointerInputType pointerType);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPointerPenInfoHistory(
            uint pointerId,
            ref int entriesCount,
            [In, Out] POINTER_PEN_INFO[] penInfo);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPointerTouchInfoHistory(
            uint pointerId,
            ref int entriesCount,
            [In, Out] POINTER_TOUCH_INFO[] touchInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr GetMessageExtraInfo();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    }
}