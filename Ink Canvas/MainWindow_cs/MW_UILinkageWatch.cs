using Ink_Canvas.Helpers;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ink_Canvas
{
    /// <summary>
    /// UI 联动 - 实时识别与调试采集：
    /// <para>
    /// 1) 非调试状态：不再依赖「首笔采样」，改为实时读取当前焦点窗口；识别到关键程序
    ///    （Word / SumatraPDF）后监听其 UIA 树（结构变化事件 + 节流兜底探测），
    ///    一旦在树中找到文档区域（Word 的 Document 视口 / SumatraPDF 的画布子窗口）
    ///    就立即创建独立画布并绑定，用户无需先写一笔。
    /// 2) 调试状态：调试面板内提供「鼠标抓取元素」开关，开启后单击目标元素即用 UIA
    ///    采集该元素（自动排除本应用自身窗口），无需在批注层书写采样。
    /// </para>
    /// </summary>
    public partial class MainWindow
    {
        // ---------------- 焦点窗口实时识别（监听目标窗口树 → 找到区域即创建独立画布） ----------------

        /// <summary>正在监听 UIA 树的目标窗口句柄（Zero = 未监听）。</summary>
        private IntPtr _uiLinkageWatchedHwnd;

        /// <summary>监听窗口对应的联动目标类型。</summary>
        private UILinkageTargetKind _uiLinkageWatchedKind;

        /// <summary>监听窗口的 UIA 元素（订阅结构变化事件期间持有，用于解除订阅）。</summary>
        private AutomationElement _uiLinkageWatchWindowElement;

        /// <summary>结构变化事件处理器（解除订阅必须使用同一实例）。</summary>
        private System.Windows.Automation.StructureChangedEventHandler _uiLinkageWatchStructureHandler;

        /// <summary>树订阅请求进行中（后台取 UIA 元素），避免每拍重复发起。</summary>
        private bool _uiLinkageWatchSubscribing;

        /// <summary>目标窗口树发生变化（事件线程写入，UI 轮询读取）：下一拍立即重试区域探测。</summary>
        private volatile bool _uiLinkageWatchStructureDirty;

        /// <summary>后台区域探测进行中。</summary>
        private bool _uiLinkageWatchProbing;

        /// <summary>监听代际：切换/停止监听时递增，用于丢弃过期的异步结果。</summary>
        private int _uiLinkageWatchGeneration;

        /// <summary>下次允许区域探测的时间（节流）。</summary>
        private DateTime _uiLinkageWatchNextProbeUtc = DateTime.MinValue;

        /// <summary>下次允许重试树订阅的时间（订阅失败后的冷却，避免每拍重复跨进程订阅）。</summary>
        private DateTime _uiLinkageWatchNextSubscribeUtc = DateTime.MinValue;

        /// <summary>尚未找到区域时的重复探测间隔（结构变化事件会提前触发，无需高频轮询）。</summary>
        private static readonly TimeSpan UILinkageWatchProbeInterval = TimeSpan.FromMilliseconds(600);

        /// <summary>树结构事件订阅失败后的重试间隔（退化为轮询探测，无需频繁重订阅）。</summary>
        private static readonly TimeSpan UILinkageWatchSubscribeRetryInterval = TimeSpan.FromSeconds(5);

        /// <summary>Word 主窗口类名（其对话框等其它顶层窗口不参与联动识别）。</summary>
        private const string WordMainWindowClassName = "OpusApp";

        /// <summary>SumatraPDF 主窗口类名。</summary>
        private const string SumatraMainWindowClassName = "SUMATRA_PDF_FRAME";

        // ---------------- 调试面板：鼠标抓取元素 ----------------

        /// <summary>鼠标抓取元素是否开启（调试面板内的开关，仅调试面板可见期间有效）。</summary>
        private bool _uiLinkageGrabModeActive;

        /// <summary>抓取用低级鼠标钩子（仅在抓取期间安装）。</summary>
        private UILinkageMouseGrabHook _uiLinkageGrabHook;

        /// <summary>程序化同步抓取开关勾选状态时置位，避免与用户勾选事件互相递归。</summary>
        private bool _uiLinkageGrabToggleSyncGuard;

        /// <summary>HUD 中的抓取状态行（i18n）。</summary>
        private static string UILinkageGrabStatusText => Ink_Canvas.Properties.UILinkageStrings.DebugGrabStatus;

        /// <summary>尚未采集到组件时的 HUD 占位文案（i18n）。</summary>
        private static string UILinkageNoCaptureText => Ink_Canvas.Properties.UILinkageStrings.DebugNoCapture;

        /// <summary>
        /// 是否允许做焦点窗口实时识别：任一联动目标开启，且未处于鼠标抓取状态
        /// （抓取模式下由点击采样驱动识别，避免与实时监听互相抢占）。
        /// </summary>
        private bool UILinkageForegroundWatchAllowed => UILinkageHasAnyTargetEnabled && !_uiLinkageGrabModeActive;

        /// <summary>
        /// 每拍（250ms）的焦点窗口识别：读取当前前台窗口，属于已开启的关键程序时切换监听目标，
        /// 并在其 UIA 树中节流探测文档区域；找到区域即创建独立画布。
        /// <para>
        /// 前台是本应用自身窗口（批注层置顶但可能持有焦点，例如刚点过工具栏）时，退回
        /// 「鼠标位置后方窗口」识别一次；已有监听目标时保持当前目标，避免批注/切工具栏时丢失目标。
        /// </para>
        /// </summary>
        private void UpdateUILinkageForegroundWatch()
        {
            try
            {
                if (!UILinkageForegroundWatchAllowed)
                {
                    StopUILinkageTreeWatch();
                    return;
                }

                var selfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                uint selfPid = UILinkageWindowFinder.GetWindowProcessId(selfHwnd);

                if (TryResolveUILinkageWatchTarget(selfHwnd, selfPid, out var target, out var targetKind)
                    && target != _uiLinkageWatchedHwnd)
                {
                    StartUILinkageTreeWatch(target, targetKind);
                }

                if (_uiLinkageWatchedHwnd == IntPtr.Zero) return;

                if (!UILinkageWindowFinder.IsWindowAlive(_uiLinkageWatchedHwnd))
                {
                    StopUILinkageTreeWatch();
                    return;
                }

                // 已绑定：视口由实例自身维护（DocElement 失效时自行重找或解绑）。
                // 结构变化事件只在「尚未找到区域」期间订阅：Word 编辑期结构事件密集，
                // 绑定后继续订阅只会徒增跨进程事件开销。
                if (_uiLinkageInstances.Any(i => i.Bound && i.BoundHwnd == _uiLinkageWatchedHwnd))
                {
                    _uiLinkageWatchStructureDirty = false;
                    UnsubscribeUILinkageTree();
                    return;
                }

                SubscribeUILinkageTree();

                if (_uiLinkageWatchProbing) return;
                if (UILinkageWindowFinder.IsMinimized(_uiLinkageWatchedHwnd)) return;
                if (!_uiLinkageWatchStructureDirty && DateTime.UtcNow < _uiLinkageWatchNextProbeUtc) return;

                StartUILinkageRegionProbe();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动实时识别失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 解析本拍应监听的目标窗口：优先前台关键程序窗口（实时读焦点窗口）；
        /// 仅当尚未建立监听时，退回鼠标位置后方的关键程序窗口——批注层置顶且持有焦点时
        /// （如先操作工具栏再书写）前台永远是本应用自身窗口，需要一个兜底入口。
        /// </summary>
        private bool TryResolveUILinkageWatchTarget(
            IntPtr selfHwnd, uint selfPid, out IntPtr hwnd, out UILinkageTargetKind kind)
        {
            hwnd = IntPtr.Zero;
            kind = UILinkageTargetKind.Word;

            var foreground = UILinkageWindowFinder.GetForegroundWindowHandle();
            if (foreground != IntPtr.Zero)
            {
                uint foregroundPid = UILinkageWindowFinder.GetWindowProcessId(foreground);
                if (foregroundPid != 0 && foregroundPid != selfPid
                    && TryGetUILinkageTargetKindForWindow(foreground, out kind))
                {
                    hwnd = foreground;
                    return true;
                }
            }

            // 已有监听目标：不因鼠标位置变化而切换（多窗口切换由前台窗口变化驱动）。
            if (_uiLinkageWatchedHwnd != IntPtr.Zero) return false;

            if (!UILinkageWindowFinder.TryGetCursorPosition(out int cursorX, out int cursorY)) return false;

            var behind = UILinkageWindowFinder.FindWindowBehind(cursorX, cursorY, selfHwnd);
            if (behind == IntPtr.Zero) return false;
            if (!TryGetUILinkageTargetKindForWindow(behind, out kind)) return false;

            hwnd = behind;
            return true;
        }

        /// <summary>
        /// 判断窗口是否属于已开启联动的关键程序。仅返回「已开启对应开关」的类型，
        /// 保证关闭某个应用联动后不再对其进行任何 UIA 探测。
        /// 额外校验主窗口类名（Word=OpusApp、SumatraPDF=SUMATRA_PDF_FRAME），
        /// 避免把目标应用的对话框/子窗口当成文档窗口绑定画布。
        /// </summary>
        private static bool TryGetUILinkageTargetKindForWindow(IntPtr hwnd, out UILinkageTargetKind kind)
        {
            kind = UILinkageTargetKind.Word;
            if (hwnd == IntPtr.Zero) return false;

            uint pid = UILinkageWindowFinder.GetWindowProcessId(hwnd);
            if (pid == 0) return false;

            string processName;
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                    processName = process.ProcessName;
            }
            catch
            {
                // 进程已退出或无访问权限：视为非目标窗口。
                return false;
            }

            // 类名读取失败时（空串）退回仅按进程名判定，避免个别环境下完全识别不到。
            string className = UILinkageWindowFinder.GetWindowClassName(hwnd);

            if (UILinkageIsWordEnabled && string.Equals(processName, "WINWORD", StringComparison.OrdinalIgnoreCase))
            {
                if (className.Length > 0 && !string.Equals(className, WordMainWindowClassName, StringComparison.Ordinal))
                    return false;

                kind = UILinkageTargetKind.Word;
                return true;
            }

            if (UILinkageIsSumatraPdfEnabled
                && string.Equals(processName, "SumatraPDF", StringComparison.OrdinalIgnoreCase))
            {
                if (className.Length > 0 && !string.Equals(className, SumatraMainWindowClassName, StringComparison.Ordinal))
                    return false;

                kind = UILinkageTargetKind.SumatraPdf;
                return true;
            }

            return false;
        }

        /// <summary>把监听目标切换到给定关键程序窗口（切换前解除旧目标的树监听）。</summary>
        private void StartUILinkageTreeWatch(IntPtr hwnd, UILinkageTargetKind kind)
        {
            StopUILinkageTreeWatch();

            _uiLinkageWatchedHwnd = hwnd;
            _uiLinkageWatchedKind = kind;
            _uiLinkageWatchStructureDirty = true;
            _uiLinkageWatchNextProbeUtc = DateTime.MinValue;
            _uiLinkageWatchGeneration++;

            LogHelper.WriteLogToFile(
                $"UI联动实时识别 | 开始监听关键程序窗口（{kind}，hwnd=0x{hwnd.ToInt64():X}）的 UIA 树",
                LogHelper.LogType.Info);
        }

        /// <summary>停止焦点窗口监听（目标关闭、联动开关关闭、进入抓取模式或窗口退出时调用）。</summary>
        private void StopUILinkageTreeWatch()
        {
            UnsubscribeUILinkageTree();

            _uiLinkageWatchedHwnd = IntPtr.Zero;
            _uiLinkageWatchStructureDirty = false;
            _uiLinkageWatchSubscribing = false;
            _uiLinkageWatchProbing = false;
            _uiLinkageWatchGeneration++;
            _uiLinkageWatchNextProbeUtc = DateTime.MinValue;
            _uiLinkageWatchNextSubscribeUtc = DateTime.MinValue;
        }

        /// <summary>
        /// 订阅监听窗口的 UIA 结构变化事件（异步取元素，回 UI 线程订阅）。
        /// 已订阅或请求在途时不重复发起；事件回调只置脏标记，探测仍在轮询线程节流执行。
        /// </summary>
        private void SubscribeUILinkageTree()
        {
            if (_uiLinkageWatchWindowElement != null || _uiLinkageWatchSubscribing) return;
            if (DateTime.UtcNow < _uiLinkageWatchNextSubscribeUtc) return;

            var hwnd = _uiLinkageWatchedHwnd;
            if (hwnd == IntPtr.Zero) return;

            int generation = _uiLinkageWatchGeneration;
            _uiLinkageWatchSubscribing = true;

            Task.Run(() =>
            {
                AutomationElement windowElement = null;
                try
                {
                    windowElement = AutomationElement.FromHandle(hwnd);
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile(
                        $"UI联动实时识别 | 获取监听窗口 UIA 元素失败: {ex.Message}", LogHelper.LogType.Trace);
                }

                if (windowElement == null)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (generation != _uiLinkageWatchGeneration) return;
                        _uiLinkageWatchSubscribing = false;
                        _uiLinkageWatchNextSubscribeUtc = DateTime.UtcNow + UILinkageWatchSubscribeRetryInterval;
                        _uiLinkageWatchNextProbeUtc = DateTime.UtcNow + UILinkageWatchProbeInterval;
                    }));
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation != _uiLinkageWatchGeneration || _uiLinkageWatchedHwnd != hwnd)
                        return;

                    _uiLinkageWatchSubscribing = false;

                    // 期间已绑定成功：无需再订阅结构事件（等未绑定后按需重新订阅）。
                    if (_uiLinkageInstances.Any(i => i.Bound && i.BoundHwnd == hwnd)) return;

                    try
                    {
                        var handler = new System.Windows.Automation.StructureChangedEventHandler(
                            OnUILinkageWatchedStructureChanged);
                        System.Windows.Automation.Automation.AddStructureChangedEventHandler(
                            windowElement, TreeScope.Subtree, handler);
                        _uiLinkageWatchWindowElement = windowElement;
                        _uiLinkageWatchStructureHandler = handler;
                        _uiLinkageWatchStructureDirty = true;

                        LogHelper.WriteLogToFile(
                            $"UI联动实时识别 | 已订阅窗口树结构变化（hwnd=0x{hwnd.ToInt64():X}）",
                            LogHelper.LogType.Trace);
                    }
                    catch (Exception ex)
                    {
                        // 提供程序不支持子树结构事件等：退回节流轮询探测，并冷却后再试订阅。
                        LogHelper.WriteLogToFile(
                            $"UI联动实时识别 | 订阅窗口树失败: {ex.Message}", LogHelper.LogType.Trace);
                        _uiLinkageWatchNextSubscribeUtc = DateTime.UtcNow + UILinkageWatchSubscribeRetryInterval;
                        _uiLinkageWatchNextProbeUtc = DateTime.UtcNow + UILinkageWatchProbeInterval;
                    }
                }));
            });
        }

        /// <summary>解除结构变化事件订阅（未订阅时为空操作）。</summary>
        private void UnsubscribeUILinkageTree()
        {
            var element = _uiLinkageWatchWindowElement;
            var handler = _uiLinkageWatchStructureHandler;
            _uiLinkageWatchWindowElement = null;
            _uiLinkageWatchStructureHandler = null;

            if (element == null || handler == null) return;

            try
            {
                System.Windows.Automation.Automation.RemoveStructureChangedEventHandler(element, handler);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile(
                    $"UI联动实时识别 | 解除窗口树订阅失败: {ex.Message}", LogHelper.LogType.Trace);
            }
        }

        /// <summary>
        /// UIA 结构变化事件回调（UIA 线程）：只置脏标记，不在此做任何 UIA 读取，
        /// 实际探测由 UI 轮询接管（见 <see cref="UpdateUILinkageForegroundWatch"/>）。
        /// </summary>
        private void OnUILinkageWatchedStructureChanged(object sender, StructureChangedEventArgs e)
        {
            _uiLinkageWatchStructureDirty = true;
        }

        /// <summary>
        /// 后台探测监听窗口的文档区域：Word 找 Document 视口（需有效矩形），
        /// SumatraPDF 找画布子窗口（SUMATRA_PDF_CANVAS）。就绪时回 UI 线程创建独立画布。
        /// </summary>
        private void StartUILinkageRegionProbe()
        {
            var hwnd = _uiLinkageWatchedHwnd;
            var kind = _uiLinkageWatchedKind;
            if (hwnd == IntPtr.Zero) return;

            int generation = _uiLinkageWatchGeneration;
            _uiLinkageWatchProbing = true;

            // 「树结构已变化」信号在探测发起时消费，而不是在探测完成时清零：
            // 完成时清零会把探测期间新到的结构事件一起吞掉；而无论是否就绪都不清零，
            // 又会让 UpdateUILinkageForegroundWatch 的节流判定永久失效（每拍都重新探测，
            // 未绑定时表现为 4Hz 的跨进程 UIA 整树搜索）。
            _uiLinkageWatchStructureDirty = false;

            Task.Run(() =>
            {
                AutomationElement windowElement = null;
                AutomationElement docElement = null;
                bool ready = false;

                try
                {
                    windowElement = AutomationElement.FromHandle(hwnd);

                    if (kind == UILinkageTargetKind.SumatraPdf)
                    {
                        // SumatraPDF 的文档渲染区是原生画布子窗口，不暴露 UIA Document。
                        ready = windowElement != null
                            && UILinkageScrollReader.FindSumatraCanvas(hwnd) != IntPtr.Zero;
                    }
                    else
                    {
                        docElement = windowElement == null ? null : FindDocumentOfWindow(windowElement);
                        var rect = SafeRect(docElement);
                        ready = docElement != null && !rect.IsEmpty && rect.Width > 0 && rect.Height > 0;
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile(
                        $"UI联动区域探测失败: {ex.Message}", LogHelper.LogType.Trace);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation != _uiLinkageWatchGeneration || _uiLinkageWatchedHwnd != hwnd)
                        return; // 监听目标已切换：丢弃过期结果

                    _uiLinkageWatchProbing = false;

                    if (ready)
                    {
                        // 找到区域：走与调试采样同一条绑定路径创建独立画布。
                        BindUILinkageCanvas(docElement, hwnd, windowElement, kind);
                    }

                    // 仍未绑定（区域读取竞态 / 创建失败 / 尚未就绪）：按间隔重试。
                    if (!_uiLinkageInstances.Any(i => i.Bound && i.BoundHwnd == hwnd))
                        _uiLinkageWatchNextProbeUtc = DateTime.UtcNow + UILinkageWatchProbeInterval;
                }));
            });
        }

        /// <summary>窗口退出时清理：停止焦点窗口监听并卸载抓取鼠标钩子。</summary>
        internal void DisposeUILinkageWatching()
        {
            try { StopUILinkageTreeWatch(); }
            catch { }

            try { _uiLinkageGrabHook?.Dispose(); }
            catch { }

            _uiLinkageGrabHook = null;
            _uiLinkageGrabModeActive = false;
        }

        // ---------------- 调试面板：鼠标抓取元素 ----------------

        /// <summary>调试面板「鼠标抓取元素」开关变化。</summary>
        private void UILinkageGrabElement_Changed(object sender, RoutedEventArgs e)
        {
            if (_uiLinkageGrabToggleSyncGuard) return;
            SetUILinkageGrabMode(UILinkageGrabElementCheck?.IsChecked == true);
        }

        /// <summary>
        /// 开关鼠标抓取模式：开启时安装低级鼠标钩子（单击目标元素采集 UIA，点击不传给目标应用），
        /// 关闭或调试面板隐藏时卸载钩子。抓取期间暂停焦点窗口实时识别，避免两条识别路径互相抢占。
        /// </summary>
        private void SetUILinkageGrabMode(bool enabled)
        {
            if (enabled && !UILinkageIsDebugEnabled) enabled = false;
            if (enabled == _uiLinkageGrabModeActive)
            {
                SyncUILinkageGrabToggle();
                return;
            }

            _uiLinkageGrabModeActive = enabled;

            if (enabled)
            {
                if (_uiLinkageGrabHook == null)
                {
                    _uiLinkageGrabHook = new UILinkageMouseGrabHook
                    {
                        LeftButtonDownHandler = OnUILinkageGrabLeftButtonDown,
                        RightButtonDownHandler = OnUILinkageGrabRightButtonDown
                    };
                }

                if (!_uiLinkageGrabHook.Install())
                {
                    LogHelper.WriteLogToFile(
                        "UI联动鼠标抓取 | 钩子安装失败，抓取模式未开启", LogHelper.LogType.Warning);
                    _uiLinkageGrabModeActive = false;
                }
            }
            else
            {
                _uiLinkageGrabHook?.Uninstall();
            }

            SyncUILinkageGrabToggle();
            RefreshDebugOverlays();
        }

        /// <summary>把面板勾选状态与抓取模式同步（程序化赋值不触发递归）。</summary>
        private void SyncUILinkageGrabToggle()
        {
            var check = UILinkageGrabElementCheck;
            if (check != null && check.IsChecked != _uiLinkageGrabModeActive)
            {
                _uiLinkageGrabToggleSyncGuard = true;
                try { check.IsChecked = _uiLinkageGrabModeActive; }
                finally { _uiLinkageGrabToggleSyncGuard = false; }
            }

            // 抓取提示行只在抓取期间显示。
            if (UILinkageGrabHintText != null)
                UILinkageGrabHintText.Visibility = _uiLinkageGrabModeActive
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        /// <summary>
        /// 抓取期间左键按下（UI 线程钩子回调，必须极快返回）：
        /// 落在本应用自身界面上的点击放行；否则取该点后方的目标窗口，到后台做 UIA 采集，
        /// 并吞掉这次点击（抓取不应误触目标应用界面）。
        /// </summary>
        private bool OnUILinkageGrabLeftButtonDown(int screenX, int screenY)
        {
            try
            {
                if (!_uiLinkageGrabModeActive) return false;

                var screenPoint = new Point(screenX, screenY);
                if (IsUILinkageGrabExemptPoint(screenPoint)) return false;

                StartUILinkageGrabCapture(screenPoint);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动鼠标抓取失败: {ex.Message}", LogHelper.LogType.Warning);
                return false;
            }
        }

        /// <summary>抓取期间右键按下：退出抓取模式（同样吞掉这次点击）。</summary>
        private bool OnUILinkageGrabRightButtonDown(int screenX, int screenY)
        {
            try
            {
                if (!_uiLinkageGrabModeActive) return false;
                if (IsUILinkageGrabExemptPoint(new Point(screenX, screenY))) return false;

                // 钩子回调内不能卸载钩子：回到消息泵后再关闭抓取模式。
                Dispatcher.BeginInvoke(new Action(() => SetUILinkageGrabMode(false)), DispatcherPriority.Input);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 抓取期间的点击是否落在本应用自身界面上（调试面板、浮动栏、弹出面板等）：
        /// 是则放行该点击且不采样。墨迹层（InkCanvasGridForInkReplay 子树）不算自身界面——
        /// 其下方就是要采集的目标应用；无 WPF 内容的辅助覆盖层（如 WinRT 湿墨迹合成层）
        /// 也不算，否则整块画布都会被误判成自身界面而无法采样。
        /// </summary>
        private bool IsUILinkageGrabExemptPoint(Point screenPoint)
        {
            var selfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            // 1) 落在自身进程其它窗口的真实 WPF 界面（弹出面板/浮动栏/设置窗口等）上：放行。
            if (IsUILinkagePointOverOwnUiContent(screenPoint, selfHwnd)) return true;

            // 2) 落在主窗口内、且位于墨迹层之外的自身界面（调试面板/工具栏等）上：放行。
            try
            {
                var windowPoint = PointFromScreen(screenPoint);
                if (windowPoint.X < 0 || windowPoint.Y < 0
                    || windowPoint.X >= ActualWidth || windowPoint.Y >= ActualHeight)
                    return false;

                var hit = InputHitTest(windowPoint) as DependencyObject;
                if (hit == null) return false;
                return !IsUILinkageVisualWithin(hit, InkCanvasGridForInkReplay);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 该点是否落在自身进程其它窗口的 WPF 内容上（弹出面板、浮动栏、设置窗口等）。
        /// 只有能命中 WPF 元素的窗口才放行：批注覆盖层等无 WPF 内容的辅助窗口必须穿透到目标应用。
        /// </summary>
        private static bool IsUILinkagePointOverOwnUiContent(Point screenPoint, IntPtr selfHwnd)
        {
            var ownHwnd = UILinkageWindowFinder.FindOwnProcessWindowOverPoint(
                (int)screenPoint.X, (int)screenPoint.Y, selfHwnd);
            if (ownHwnd == IntPtr.Zero) return false;

            try
            {
                var root = System.Windows.Interop.HwndSource.FromHwnd(ownHwnd)?.RootVisual as UIElement;
                if (root == null) return false;

                var localPoint = root.PointFromScreen(screenPoint);
                return root.InputHitTest(localPoint) is DependencyObject;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>元素是否位于指定祖先的视觉/逻辑子树中（含自身）。</summary>
        private static bool IsUILinkageVisualWithin(DependencyObject node, DependencyObject ancestor)
        {
            if (node == null || ancestor == null) return false;

            try
            {
                var current = node;
                int guard = 0;
                while (current != null && guard++ < 256)
                {
                    if (ReferenceEquals(current, ancestor)) return true;
                    current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                        ? VisualTreeHelper.GetParent(current)
                        : LogicalTreeHelper.GetParent(current);
                }
            }
            catch
            {
                // 文本节点等非可视对象：视为不在该子树中。
            }

            return false;
        }

        /// <summary>
        /// 采集点击位置后方（排除自身）的目标元素：低价 Win32 取窗后交后台做 UIA 采集，
        /// 采样结果与调试面板刷新复用笔画采样同一条路径（<see cref="CaptureOnBackground"/>）。
        /// </summary>
        private void StartUILinkageGrabCapture(Point screenPoint)
        {
            var selfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            IntPtr hwnd = UILinkageWindowFinder.FindWindowBehind(
                (int)screenPoint.X, (int)screenPoint.Y, selfHwnd);

            if (hwnd == IntPtr.Zero)
            {
                LogHelper.WriteLogToFile(
                    "UI联动鼠标抓取 | 该点未找到可采集的其它进程窗口", LogHelper.LogType.Trace);
                return;
            }

            _uiLinkageCapturedHwnd = hwnd;

            bool wordEnabled = UILinkageIsWordEnabled;
            bool sumatraEnabled = UILinkageIsSumatraPdfEnabled;
            bool showTree = _uiLinkageShowTree;

            Task.Run(() => CaptureOnBackground(
                hwnd, screenPoint, selfHwnd, wordEnabled, sumatraEnabled, showTree, usePointHitTest: true));
        }
    }
}
