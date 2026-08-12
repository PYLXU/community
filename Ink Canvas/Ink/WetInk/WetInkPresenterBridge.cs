using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows = global::Windows;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// IInkDesktopHost COM 接口（inkpresenterdesktop.h，MIDL 权威 vtable 顺序）。
    /// IID = 4ce7d875-a981-4140-a1ff-ad93258e8d59；CLSID = 062584A6-F830-4BDC-A4D2-0A10AB062B1D
    /// （InprocServer32 = Windows.UI.Input.Inking.dll）。
    /// </summary>
    [ComImport, Guid("4ce7d875-a981-4140-a1ff-ad93258e8d59")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInkDesktopHost
    {
        [PreserveSig]
        int QueueWorkItem(IntPtr workItem);

        [PreserveSig]
        int CreateInkPresenter(ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CreateAndInitializeInkPresenter(IntPtr rootVisual, float width, float height, ref Guid riid, out IntPtr ppv);
    }

    /// <summary>
    /// IInkHostWorkItem COM 接口：Invoke 在 InkDesktopHost 的专属 ink 线程上执行。
    /// IID = ccda0a9a-1b78-4632-bb96-97800662e26c。
    /// </summary>
    [ComImport, Guid("ccda0a9a-1b78-4632-bb96-97800662e26c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInkHostWorkItem
    {
        [PreserveSig]
        int Invoke();
    }

    /// <summary>
    /// IInkPresenterDesktop COM 接口：SetRootVisual/SetSize 等必须在 ink 线程调用。
    /// IID = 73F3C0D9-2E8B-48F3-895E-20CBD27B723B。
    /// </summary>
    [ComImport, Guid("73F3C0D9-2E8B-48F3-895E-20CBD27B723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInkPresenterDesktop
    {
        [PreserveSig]
        int SetRootVisual(IntPtr rootVisual, IntPtr device);

        [PreserveSig]
        int SetCommitRequestHandler(IntPtr handler);

        [PreserveSig]
        int GetSize(out float width, out float height);

        [PreserveSig]
        int SetSize(float width, float height);

        [PreserveSig]
        int OnHighContrastChanged();
    }

    /// <summary>
    /// 最小 IDCompositionDevice（dcomp.dll，IID c37ea93a-e7aa-450d-b16f-9746cb0407f3）。
    /// 只声明用到的 vtable 前 8 个方法（MIDL 顺序：Commit/WaitForCommitCompletion/
    /// GetFrameStatistics/CreateTargetForHwnd/CreateVisual/CreateSurface/CreateVirtualSurface）。
    /// </summary>
    [ComImport, Guid("c37ea93a-e7aa-450d-b16f-9746cb0407f3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDCompositionDeviceMin
    {
        [PreserveSig]
        int Commit();

        [PreserveSig]
        int WaitForCommitCompletion();

        [PreserveSig]
        int GetFrameStatistics(IntPtr stats);

        [PreserveSig]
        int CreateTargetForHwnd(IntPtr hwnd, int topmost, out IntPtr target);

        [PreserveSig]
        int CreateVisual(out IntPtr visual);

        [PreserveSig]
        int CreateSurface(int w, int h, int format, int alpha, out IntPtr surface);

        [PreserveSig]
        int CreateVirtualSurface(int iw, int ih, int format, int alpha, out IntPtr vs);
    }

    /// <summary>IDCompositionTarget（IID eacdd04c-117e-4e17-88f4-d1b12b0e3d89）。</summary>
    [ComImport, Guid("eacdd04c-117e-4e17-88f4-d1b12b0e3d89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDCompositionTargetMin
    {
        [PreserveSig]
        int SetRoot(IntPtr visual);
    }

    /// <summary>
    /// WinRT InkPresenter 桥：IInkDesktopHost（COM）+ 系统 DirectComposition 设备 + ink 线程。
    ///
    /// 已实测验证的正确用法（探针全链路通过）：
    /// 1. DCompositionCreateDevice3(nullptr, IID_IDCompositionDevice) —— 必须系统 DComp 设备
    ///    （不传 DXGI 设备；Vortice 的 DCompositionCreateDevice(dxgi) 会令 SetRootVisual E_NOINTERFACE）
    /// 2. CreateTargetForHwnd(overlayHwnd, topmost) + CreateVisual + SetRoot + Commit
    /// 3. 所有 presenter 操作必须投递到 InkDesktopHost 的专属 ink 线程（QueueWorkItem +
    ///    IInkHostWorkItem.Invoke）：CreateInkPresenter → SetRootVisual(visual, nullptr) → SetSize
    ///    → 配置（InputDeviceTypes/Mode/RightDragAction/StrokesCollected 订阅）。跨线程直调
    ///    返回 RPC_E_WRONG_THREAD。
    /// 4. FromAbi 投影 InkPresenter。
    ///
    /// 湿→干：IInkDesktopHost 默认模式让 InkPresenter 自渲染干墨到 DComp 表面，StrokesCollected
    /// 后 Selected+DeleteSelected 撤掉覆盖层干墨，避免与 WPF 干层双层叠加。
    /// </summary>
    internal sealed class WetInkPresenterBridge : IDisposable
    {
        private static readonly Guid ClsidInkDesktopHost = new Guid("062584A6-F830-4BDC-A4D2-0A10AB062B1D");
        private static readonly Guid IidIInkPresenterDesktop = new Guid("73F3C0D9-2E8B-48F3-895E-20CBD27B723B");
        private static readonly Guid IidIDCompositionDevice = new Guid("c37ea93a-e7aa-450d-b16f-9746cb0407f3");

        private const int InkThreadOpTimeoutMs = 5000;

        private IInkDesktopHost _desktopHost;
        private IntPtr _dcompDevicePtr;
        private IntPtr _visualPtr;
        private IntPtr _presenterDesktopPtr;
        private InkHostWorkItem _workItem;
        private IntPtr _workItemComPtr;
        private global::Windows.UI.Input.Inking.InkPresenter _inkPresenter; // 仅在 ink 线程访问
        private float _currentWidthPx;
        private float _currentHeightPx;
        private bool _disposed;

        public event EventHandler<IReadOnlyList<global::Windows.UI.Input.Inking.InkStroke>> StrokesCollected;

        /// <summary>初始化：系统 DComp 设备 + 视觉树 + ink 线程创建/配置 presenter。失败返回 false。</summary>
        public bool Initialize(IntPtr overlayHwnd, float widthPx, float heightPx)
        {
            try
            {
                CreateDesktopHost();
                CreateSystemCompositionResources(overlayHwnd);
                CreatePresenterOnInkThread(Math.Max(1, widthPx), Math.Max(1, heightPx));
                ConfigurePresenterOnInkThread();
                return true;
            }
            catch (Exception ex)
            {
                Helpers.LogHelper.WriteLogToFile(
                    $"WetInkPresenterBridge 初始化失败: {ex}", Helpers.LogHelper.LogType.Error);
                Cleanup();
                return false;
            }
        }

        /// <summary>覆盖窗口尺寸变化时更新墨迹区域（同步等 ink 线程，尺寸基本未变则跳过）。</summary>
        public void UpdateTargetSize(float widthPx, float heightPx)
        {
            if (_disposed || _presenterDesktopPtr == IntPtr.Zero) return;

            var w = Math.Max(1, widthPx);
            var h = Math.Max(1, heightPx);
            if (Math.Abs(w - _currentWidthPx) < 1 && Math.Abs(h - _currentHeightPx) < 1) return;

            try
            {
                RunOnInkThreadSync(() =>
                {
                    var desktop = (IInkPresenterDesktop)Marshal.GetObjectForIUnknown(_presenterDesktopPtr);
                    var hr = desktop.SetSize(w, h);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                    _currentWidthPx = w;
                    _currentHeightPx = h;
                });
            }
            catch (Exception ex)
            {
                Helpers.LogHelper.WriteLogToFile(
                    $"WetInkPresenterBridge 更新尺寸失败: {ex.Message}", Helpers.LogHelper.LogType.Warning);
            }
        }

        /// <summary>同步 ICC 笔属性到 InkDrawingAttributes（ink 线程，队列保序 fire-and-forget）。</summary>
        public void UpdateDrawingAttributes(WetInkStyleSnapshot style)
        {
            RunOnInkThreadFireForget(() =>
            {
                if (_inkPresenter == null) return;
                try
                {
                    var da = new global::Windows.UI.Input.Inking.InkDrawingAttributes
                    {
                        Color = style.Color,
                        Size = new global::Windows.Foundation.Size(style.Width, style.Height),
                        FitToCurve = style.FitToCurve,
                        IgnorePressure = style.IgnorePressure,
                        DrawAsHighlighter = style.DrawAsHighlighter,
                        PenTip = style.PenTip
                    };
                    _inkPresenter.UpdateDefaultDrawingAttributes(da);
                }
                catch (Exception ex)
                {
                    Helpers.LogHelper.WriteLogToFile(
                        $"WetInkPresenterBridge 更新笔属性失败: {ex.Message}", Helpers.LogHelper.LogType.Warning);
                }
            });
        }

        public void SetInputEnabled(bool enabled)
        {
            RunOnInkThreadFireForget(() =>
            {
                if (_inkPresenter == null) return;
                try { _inkPresenter.IsInputEnabled = enabled; }
                catch { /* 忽略 */ }
            });
        }

        /// <summary>
        /// 从覆盖层移除指定笔画（干墨已进 inkCanvas.Strokes 后调用）。
        /// InkStrokeContainer 没有 DeleteStrokes(ids)，只能 Selected+DeleteSelected。
        /// </summary>
        public void RemoveStrokes(IReadOnlyList<global::Windows.UI.Input.Inking.InkStroke> strokes)
        {
            if (strokes == null || strokes.Count == 0) return;
            RunOnInkThreadFireForget(() =>
            {
                if (_inkPresenter == null) return;
                try
                {
                    foreach (var s in strokes)
                    {
                        try { s.Selected = true; } catch { }
                    }
                    _inkPresenter.StrokeContainer.DeleteSelected();
                }
                catch (Exception ex)
                {
                    Helpers.LogHelper.WriteLogToFile(
                        $"WetInkPresenterBridge 撤湿墨失败: {ex.Message}", Helpers.LogHelper.LogType.Warning);
                }
            });
        }

        /// <summary>清空覆盖层所有笔画（引擎关闭/清屏时）。</summary>
        public void ClearAll()
        {
            RunOnInkThreadFireForget(() =>
            {
                if (_inkPresenter == null) return;
                try { _inkPresenter.StrokeContainer.Clear(); }
                catch { /* 忽略 */ }
            });
        }

        // ---------------- ink 线程投递 ----------------

        /// <summary>投递到 ink 线程并同步等待完成（启动/配置/尺寸路径）。</summary>
        private void RunOnInkThreadSync(Action action)
        {
            if (_desktopHost == null) return;

            var done = new System.Threading.ManualResetEventSlim(false);
            Exception failure = null;
            _workItem.Enqueue(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
            });

            var hr = _desktopHost.QueueWorkItem(_workItemComPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            if (!done.Wait(InkThreadOpTimeoutMs))
            {
                Helpers.LogHelper.WriteLogToFile(
                    "WetInkPresenterBridge ink 线程操作超时", Helpers.LogHelper.LogType.Warning);
                return;
            }
            if (failure != null) throw failure;
        }

        /// <summary>投递到 ink 线程（队列保序，不等完成；用于高频操作）。</summary>
        private void RunOnInkThreadFireForget(Action action)
        {
            if (_desktopHost == null) return;
            try
            {
                _workItem.Enqueue(action);
                _desktopHost.QueueWorkItem(_workItemComPtr);
            }
            catch (Exception ex)
            {
                Helpers.LogHelper.WriteLogToFile(
                    $"WetInkPresenterBridge 投递 ink 线程失败: {ex.Message}", Helpers.LogHelper.LogType.Warning);
            }
        }

        // ---------------- 初始化步骤 ----------------

        private void CreateDesktopHost()
        {
            var type = Type.GetTypeFromCLSID(ClsidInkDesktopHost);
            if (type == null)
                throw new InvalidOperationException("CLSID_InkDesktopHost 不可用");
            _desktopHost = (IInkDesktopHost)Activator.CreateInstance(type);

            _workItem = new InkHostWorkItem();
            _workItemComPtr = Marshal.GetComInterfaceForObject(_workItem, typeof(IInkHostWorkItem));
        }

        private void CreateSystemCompositionResources(IntPtr overlayHwnd)
        {
            var iid = IidIDCompositionDevice;
            var hr = DCompositionCreateDevice3(IntPtr.Zero, ref iid, out _dcompDevicePtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            var device = (IDCompositionDeviceMin)Marshal.GetObjectForIUnknown(_dcompDevicePtr);

            hr = device.CreateTargetForHwnd(overlayHwnd, 1, out var targetPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            hr = device.CreateVisual(out _visualPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            var target = (IDCompositionTargetMin)Marshal.GetObjectForIUnknown(targetPtr);
            hr = target.SetRoot(_visualPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            hr = device.Commit();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        private void CreatePresenterOnInkThread(float widthPx, float heightPx)
        {
            IntPtr ppv = IntPtr.Zero;

            RunOnInkThreadSync(() =>
            {
                var riid = IidIInkPresenterDesktop;
                var hr = _desktopHost.CreateInkPresenter(ref riid, out ppv);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);

                Guid pd = IidIInkPresenterDesktop;
                Marshal.QueryInterface(ppv, ref pd, out _presenterDesktopPtr);

                var desktop = (IInkPresenterDesktop)Marshal.GetObjectForIUnknown(_presenterDesktopPtr);
                hr = desktop.SetRootVisual(_visualPtr, IntPtr.Zero);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);

                hr = desktop.SetSize(widthPx, heightPx);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);

                _currentWidthPx = widthPx;
                _currentHeightPx = heightPx;
            });

            if (ppv == IntPtr.Zero)
                throw new InvalidOperationException("CreateInkPresenter 返回空指针");

            _inkPresenter = global::WinRT.MarshalInterface<global::Windows.UI.Input.Inking.InkPresenter>.FromAbi(ppv);

            // SetRootVisual 绑定后必须 Commit 系统 DComp 设备，视觉树才真正生效。
            // DComp 设备在 UI 线程创建（MTA），不能在 ink 线程 QI —— 在 UI 线程 Commit。
            CommitSystemComposition();
        }

        private void CommitSystemComposition()
        {
            var device = (IDCompositionDeviceMin)Marshal.GetObjectForIUnknown(_dcompDevicePtr);
            var hr = device.Commit();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        private void ConfigurePresenterOnInkThread()
        {
            RunOnInkThreadSync(() =>
            {
                _inkPresenter.InputDeviceTypes =
                    global::Windows.UI.Core.CoreInputDeviceTypes.Mouse
                    | global::Windows.UI.Core.CoreInputDeviceTypes.Pen
                    | global::Windows.UI.Core.CoreInputDeviceTypes.Touch;
                _inkPresenter.InputProcessingConfiguration.Mode =
                    global::Windows.UI.Input.Inking.InkInputProcessingMode.Inking;
                _inkPresenter.InputProcessingConfiguration.RightDragAction =
                    global::Windows.UI.Input.Inking.InkInputRightDragAction.LeaveUnprocessed;
                _inkPresenter.StrokesCollected += OnStrokesCollected;
            });
        }

        // ---------------- 事件 ----------------

        /// <summary>在 ink 线程触发；CommitSink 内部会 marshal 回 UI 线程。</summary>
        private void OnStrokesCollected(
            global::Windows.UI.Input.Inking.InkPresenter sender,
            global::Windows.UI.Input.Inking.InkStrokesCollectedEventArgs args)
        {
            try
            {
                var strokes = args.Strokes;
                if (strokes != null && strokes.Count > 0)
                    StrokesCollected?.Invoke(this, strokes);
            }
            catch (Exception ex)
            {
                Helpers.LogHelper.WriteLogToFile(
                    $"WetInkPresenterBridge StrokesCollected 处理异常: {ex}", Helpers.LogHelper.LogType.Error);
            }
        }

        private void Cleanup()
        {
            if (_inkPresenter != null)
            {
                try
                {
                    _inkPresenter.StrokesCollected -= OnStrokesCollected;
                    _inkPresenter = null;
                }
                catch { _inkPresenter = null; }
            }

            if (_workItemComPtr != IntPtr.Zero) { try { Marshal.Release(_workItemComPtr); } catch { } _workItemComPtr = IntPtr.Zero; }
            _workItem = null;
            _presenterDesktopPtr = IntPtr.Zero;
            _visualPtr = IntPtr.Zero;
            if (_dcompDevicePtr != IntPtr.Zero) { try { Marshal.Release(_dcompDevicePtr); } catch { } _dcompDevicePtr = IntPtr.Zero; }
            _desktopHost = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
        }

        [DllImport("dcomp.dll", ExactSpelling = true)]
        private static extern int DCompositionCreateDevice3(IntPtr dxgiDevice, ref Guid riid, out IntPtr ppv);

        /// <summary>持久 ink 线程工作项：Invoke 在 ink 线程执行队列中的下一个 action。</summary>
        private sealed class InkHostWorkItem : IInkHostWorkItem
        {
            private readonly Queue<Action> _queue = new Queue<Action>();

            public void Enqueue(Action action)
            {
                lock (_queue) _queue.Enqueue(action);
            }

            public int Invoke()
            {
                Action action;
                lock (_queue)
                {
                    if (_queue.Count == 0) return 0;
                    action = _queue.Dequeue();
                }

                try { action(); return 0; }
                catch (Exception ex)
                {
                    Helpers.LogHelper.WriteLogToFile(
                        $"WetInkPresenterBridge ink 线程执行异常: {ex}", Helpers.LogHelper.LogType.Error);
                    return unchecked((int)0x80004005);
                }
            }
        }
    }
}
