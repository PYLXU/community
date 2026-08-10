using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// 湿墨覆盖层窗口 + 专用渲染线程。
    ///
    /// 窗口：WS_POPUP + WS_EX_TRANSPARENT/NOACTIVATE/TOOLWINDOW/NOREDIRECTIONBITMAP，
    /// WM_NCHITTEST 恒返回 HTTRANSPARENT，输入完全穿透（输入由主窗口的
    /// WetInkPointerInputSource 统一接管）。
    ///
    /// 铁律：绝不 ShowWindow/HideWindow —— DWM 合成重排会闪屏。
    /// 需要「隐藏」时把窗口挪到离屏坐标 HiddenPosition。
    /// </summary>
    internal sealed class WetInkWindowHost : IDisposable
    {
        private const string WindowClassName = "InkCanvasForClass.WetInkOverlay.v2";

        private const int WmNcHitTest = 0x0084;
        private const int WmDestroy = 0x0002;
        private const int HtTransparent = -1;

        private const int WsPopup = unchecked((int)0x80000000);
        private const int WsVisible = 0x10000000;

        private const int WsExNoActivate = 0x08000000;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExTopmost = 0x00000008;
        private const int WsExNoRedirectionBitmap = 0x00200000;

        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;

        /// <summary>「隐藏」用的离屏坐标。</summary>
        private const int HiddenPosition = -100000;

        public WetInkWindowHost(Action<Exception> fatalErrorCallback, Action<long> retiredCallback)
        {
            _fatalErrorCallback = fatalErrorCallback;
            _retiredCallback = retiredCallback;
            _mailbox = new WetInkCommandMailbox();
        }

        private readonly Action<Exception> _fatalErrorCallback;
        private readonly Action<long> _retiredCallback;
        private readonly WetInkCommandMailbox _mailbox;

        private Thread _renderThread;
        private IntPtr _hwnd;
        private ushort _classAtom;
        private WndProcDelegate _wndProc;
        private WetInkD3DRenderer _renderer;

        private volatile bool _running;
        private volatile bool _deviceReady;
        private readonly ManualResetEventSlim _initialized = new ManualResetEventSlim(false);
        private Exception _initializationError;
        private WetInkTargetSnapshot _pendingTarget;
        private bool _disposed;

        public bool IsDeviceReady => _deviceReady;
        public WetInkCommandMailbox Mailbox => _mailbox;

        /// <summary>启动渲染线程并建立覆盖层。返回 false 表示初始化失败，应回退旧管线。</summary>
        public bool Start(in WetInkTargetSnapshot target)
        {
            if (_running)
                return _deviceReady;

            _pendingTarget = target;
            _running = true;

            _renderThread = new Thread(RenderThreadMain)
            {
                Name = "ICC-WetInkRender",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            // 渲染线程持有 D3D/DComp，用 MTA 避免 COM 单线程套间的消息泵依赖。
            _renderThread.SetApartmentState(ApartmentState.MTA);
            _renderThread.Start();

            // 等待初始化结果（设备创建可能失败，需要同步得知）。
            if (!_initialized.Wait(TimeSpan.FromSeconds(5)))
            {
                Debug.WriteLine("[WetInk] render thread init timeout");
                Stop();
                return false;
            }

            if (_initializationError != null)
            {
                Debug.WriteLine($"[WetInk] render thread init failed: {_initializationError}");
                Stop();
                return false;
            }

            return _deviceReady;
        }

        public void Stop()
        {
            if (!_running)
                return;

            _running = false;
            _mailbox.Post(WetInkCommand.Simple(WetInkCommandKind.Shutdown));

            var thread = _renderThread;
            if (thread != null && thread.IsAlive)
            {
                if (!thread.Join(TimeSpan.FromSeconds(2)))
                    Debug.WriteLine("[WetInk] render thread did not exit in time");
            }

            _renderThread = null;
            _deviceReady = false;
        }

        /// <summary>更新覆盖层几何（位置/尺寸/DPI）。</summary>
        public void UpdateTarget(in WetInkTargetSnapshot target)
        {
            _pendingTarget = target;

            if (_hwnd != IntPtr.Zero && target.IsValid)
            {
                SetWindowPos(
                    _hwnd, IntPtr.Zero,
                    target.ScreenLeftPixels, target.ScreenTopPixels,
                    target.WidthPixels, target.HeightPixels,
                    SwpNoActivate | SwpNoZOrder);
            }

            _mailbox.Post(new WetInkCommand(
                WetInkCommandKind.UpdateTarget, 0,
                WetInkRibbonGeometry.Empty, default, 0, target));
        }

        /// <summary>
        /// 控制覆盖层是否在屏幕上。有真实湿墨视觉时才为 true（此时按最新 target
        /// 摆到主窗口上方），否则移到屏外。与旧系统 SetOverlayVisible 等价——
        /// 覆盖层绝不允许在「没有湿墨」的时候停留在屏幕上拦截点击。
        /// </summary>
        public void SetOverlayVisible(bool visible)
        {
            _overlayShouldBeVisible = visible;
            if (_hwnd == IntPtr.Zero || _disposed)
                return;

            if (visible && _pendingTarget.IsValid)
            {
                SetWindowPos(
                    _hwnd, IntPtr.Zero,
                    _pendingTarget.ScreenLeftPixels, _pendingTarget.ScreenTopPixels,
                    _pendingTarget.WidthPixels, _pendingTarget.HeightPixels,
                    SwpNoActivate | SwpNoZOrder);
            }
            else
            {
                ParkOffscreen();
            }
        }

        private bool _overlayShouldBeVisible;

        /// <summary>把覆盖层挪到离屏（不使用 ShowWindow，避免 DWM 闪屏）。</summary>
        public void ParkOffscreen()
        {
            if (_hwnd == IntPtr.Zero)
                return;

            SetWindowPos(
                _hwnd, IntPtr.Zero,
                HiddenPosition, HiddenPosition,
                0, 0,
                SwpNoActivate | SwpNoZOrder | SwpNoSize);
        }

        // ------------------------------------------------------------------
        // render thread
        // ------------------------------------------------------------------

        private void RenderThreadMain()
        {
            try
            {
                CreateOverlayWindow(_pendingTarget);

                _renderer = new WetInkD3DRenderer();
                _renderer.BindTarget(_hwnd, _pendingTarget);
                _deviceReady = _renderer.IsDeviceReady;
            }
            catch (Exception ex)
            {
                _initializationError = ex;
                _deviceReady = false;
                _initialized.Set();
                CleanupRenderThread();
                return;
            }

            _initialized.Set();

            try
            {
                RenderLoop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WetInk] render loop fatal: {ex}");
                _deviceReady = false;
                try { _fatalErrorCallback?.Invoke(ex); }
                catch { /* callback must not kill the thread */ }
            }
            finally
            {
                CleanupRenderThread();
            }
        }

        private void RenderLoop()
        {
            while (_running)
            {
                // 与旧系统一致：1ms 轮询而非阻塞 16ms。覆盖层窗口的 WndProc 由本线程
                // 应答（WM_NCHITTEST 等同步 SendMessage）；阻塞太长会让 SendMessage
                // 超时，鼠标表现为撞在一块实心全屏窗口上。
                _mailbox.WaitHandle.WaitOne(1);

                PumpWindowMessages();

                var commands = _mailbox.DrainAll();
                if (commands == null || commands.Count == 0)
                    continue;

                var shutdownRequested = false;
                for (var i = 0; i < commands.Count; i++)
                {
                    if (commands[i].Kind == WetInkCommandKind.Shutdown)
                    {
                        shutdownRequested = true;
                        break;
                    }
                }

                var retired = _renderer.Apply(commands);
                if (retired != null && retired.Count > 0 && _retiredCallback != null)
                {
                    for (var i = 0; i < retired.Count; i++)
                    {
                        try { _retiredCallback(retired[i]); }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WetInk] retirement callback: {ex.Message}");
                        }
                    }
                }

                if (!_renderer.IsDeviceReady)
                {
                    _deviceReady = false;
                    try { _fatalErrorCallback?.Invoke(new InvalidOperationException("D3D device lost")); }
                    catch { /* callback must not kill the thread */ }
                    return;
                }

                if (shutdownRequested)
                    return;
            }
        }

        private void PumpWindowMessages()
        {
            while (PeekMessage(out var msg, _hwnd, 0, 0, 1 /* PM_REMOVE */))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        private void CleanupRenderThread()
        {
            try { _renderer?.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[WetInk] renderer dispose: {ex.Message}"); }
            _renderer = null;

            if (_hwnd != IntPtr.Zero)
            {
                try { DestroyWindow(_hwnd); }
                catch (Exception ex) { Debug.WriteLine($"[WetInk] destroy window: {ex.Message}"); }
                _hwnd = IntPtr.Zero;
            }

            if (_classAtom != 0)
            {
                try { UnregisterClass(WindowClassName, GetModuleHandle(null)); }
                catch { /* best effort */ }
                _classAtom = 0;
            }
        }

        // ------------------------------------------------------------------
        // overlay window
        // ------------------------------------------------------------------

        private void CreateOverlayWindow(in WetInkTargetSnapshot target)
        {
            var moduleHandle = GetModuleHandle(null);
            _wndProc = OverlayWndProc;

            var windowClass = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = moduleHandle,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = WindowClassName,
                hIconSm = IntPtr.Zero
            };

            _classAtom = RegisterClassEx(ref windowClass);
            if (_classAtom == 0)
            {
                var error = Marshal.GetLastWin32Error();
                // 1410 = ERROR_CLASS_ALREADY_EXISTS：上次未清理干净，可复用。
                if (error != 1410)
                    throw new InvalidOperationException($"RegisterClassEx failed: {error}");
            }

            var exStyle = WsExNoActivate | WsExTransparent | WsExToolWindow
                          | WsExTopmost | WsExNoRedirectionBitmap;

            _hwnd = CreateWindowEx(
                exStyle,
                WindowClassName,
                string.Empty,
                WsPopup | WsVisible,
                // 旧系统先例：覆盖层始终从屏外坐标创建，只在有真实湿墨时才
                // SetOverlayVisible(true) 移到屏幕上；没有湿墨时一律在屏外。
                // 不能在创建时就用屏幕坐标，否则整屏立刻被这块全屏窗口拦截。
                HiddenPosition,
                HiddenPosition,
                Math.Max(1, target.WidthPixels),
                Math.Max(1, target.HeightPixels),
                IntPtr.Zero,
                IntPtr.Zero,
                moduleHandle,
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        private IntPtr OverlayWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WmNcHitTest:
                    // 输入完全穿透：覆盖层永不接收点击。
                    return new IntPtr(HtTransparent);
                case WmDestroy:
                    return IntPtr.Zero;
                default:
                    return DefWindowProc(hwnd, msg, wParam, lParam);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            Stop();
            _mailbox.Dispose();
            _initialized.Dispose();
        }

        // ------------------------------------------------------------------
        // interop
        // ------------------------------------------------------------------

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterClass(string className, IntPtr hInstance);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(
            out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax, uint removeMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);
    }
}