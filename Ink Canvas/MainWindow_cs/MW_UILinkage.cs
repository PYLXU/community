using Ink_Canvas.Helpers;
using Ink_Canvas.Windows.SettingsViews.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ink_Canvas
{
    /// <summary>
    /// UI 联动：进入批注模式书写时，用墨迹「首笔位置」监听批注层后方的窗口。
    /// <para>
    /// - 目标为 Word 且开启 Word 联动时：把独立画布绑定到该 Word 窗口的 Document 视口
    ///   （带边框、带向外延伸的竖直/水平滚动条的独立画布），其墨迹独立于主画布，随 Word
    ///   滚动平移、随 Word 缩放比例重缩放（缩放读取自状态栏缩放指示）。支持多实例：
    ///   多个 Word 窗口（多文件分屏）各自拥有独立画布。
    /// - 目标为 SumatraPDF 且开启 SumatraPDF 联动时：绑定到其文档渲染画布子窗口
    ///   （SUMATRA_PDF_CANVAS，无 UIA Document/ScrollPattern），滚动读写走 Win32 滚动条
    ///   （GetScrollInfo + WM_VSCROLL/WM_HSCROLL 反馈闭环）；文件名取自窗口标题。
    /// - 首笔若落在支持联动的应用上，该笔会从主画布迁入独立画布。
    /// - 目标为其它元素时：继续监听并在画布层叠加临时高亮边框与调试面板。
    /// </para>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>组件高亮边框在画布层中的 Z 序（高于墨迹，低于调试面板）。</summary>
        private const int UILinkageHighlightZIndex = 501;

        /// <summary>独立画布在画布层中的 Z 序（高于墨迹与高亮边框，用于接收独立批注输入）。</summary>
        private const int UILinkageCanvasZIndex = 600;

        /// <summary>待迁移首笔缓存上限（绑定完成前连续书写的笔迹最多迁移最近几笔）。</summary>
        private const int UILinkagePendingStrokeLimit = 8;

        /// <summary>
        /// 待迁移首笔的有效期：实时识别下单笔写入后画布通常在一个探测周期内建立，
        /// 超时说明该笔与本次绑定无关（例如写在其它窗口上），不再迁移，避免误迁历史墨迹。
        /// </summary>
        private static readonly TimeSpan UILinkagePendingStrokeTtl = TimeSpan.FromSeconds(3);

        private static readonly TimeSpan UILinkagePollInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// 滚动读数「连续」失败多少次才判定实例失效并解绑。
        /// <para>
        /// 单次读取失败是常态（画布子窗口被重建、渲染线程尚未回写实际位置、切换显示模式），
        /// 而解绑会连带触发「下一拍重新绑定 → 绑定内同步端点校准（SB_BOTTOM + 最多 192 次
        /// 阻塞 <c>SendMessageTimeout</c>）→ 再读失败」，形成一次瞬时失败就能点燃的死循环，
        /// CPU 与交互线程同时被打满。因此必须连续失败到阈值才解绑。
        /// </para>
        /// </summary>
        private const int UILinkageScrollReadFailureLimit = 5;

        /// <summary>缩放指示元素查找冷却：缓存失效后短时间内不重复做状态栏 UIA 遍历。</summary>
        private static readonly TimeSpan UILinkageZoomFindCooldown = TimeSpan.FromSeconds(2);

        /// <summary>页面子元素查找冷却：缓存失效后短时间内不重复查找（避免高频 UIA 遍历）。</summary>
        private static readonly TimeSpan UILinkagePageFindCooldown = TimeSpan.FromSeconds(2);

        /// <summary>Word 文本区（_WwG）窗口查找冷却：句柄失效后短时间内不重复枚举子窗口。</summary>
        private static readonly TimeSpan UILinkageWordTextAreaFindCooldown = TimeSpan.FromSeconds(2);

        /// <summary>
        /// 水平轴「滚动条帧 ↔ 页面实测帧」常量偏移的稳定判据（DIP）：两次标定差值在容差内才跟随；
        /// 超出视为页面实测帧跳变（元素失效 / 被裁剪 / 重排中），保留旧标定值。
        /// </summary>
        private const double UILinkageWordHorizontalFrameToleranceDip = 8.0;

        /// <summary>横向读数诊断日志的最小间隔（仅在读数形态变化时写，且不高于该频率）。</summary>
        private static readonly TimeSpan UILinkageWordHorizontalTraceInterval = TimeSpan.FromSeconds(2);

        /// <summary>同一窗口的节流窗口：短时间内不重复做 UIA 整树遍历。</summary>
        private static readonly TimeSpan UILinkageCaptureThrottle = TimeSpan.FromMilliseconds(300);

        private DispatcherTimer _uiLinkageTimer;

        /// <summary>当前捕捉/高亮的目标组件（用于调试高亮、遮挡、上移容器）。</summary>
        private AutomationElement _uiLinkageCapturedElement;

        /// <summary>捕捉到的批注层后方顶层窗口（用于缩放检测与 Win32 滚动读取）。</summary>
        private AutomationElement _uiLinkageCapturedWindow;

        /// <summary>捕捉到的批注层后方窗口句柄（用于遮挡计算）。</summary>
        private IntPtr _uiLinkageCapturedHwnd;

        /// <summary>调试面板是否显示完整组件树。</summary>
        private bool _uiLinkageShowTree;

        /// <summary>后台计算好的「一键复制/调试面板」信息字符串缓存（避免每 250ms 重新 UIA）。</summary>
        private string _uiLinkageCachedInfo;

        /// <summary>后台计算好的完整组件树缓存。</summary>
        private string _uiLinkageCachedTree;

        // ---------------- 独立画布 / Word 绑定（多实例） ----------------

        /// <summary>所有已绑定的独立画布实例（每个 Word 顶层窗口一个）。</summary>
        private readonly List<UILinkageCanvasInstance> _uiLinkageInstances = new List<UILinkageCanvasInstance>();

        /// <summary>
        /// 待迁移首笔：触发时的笔画实例可能被收笔后处理（贝塞尔平滑/直线拉直/形状识别）
        /// 替换为新对象，因此同时记录「触发时刻主画布笔画快照」与「触发首点位置」，
        /// 迁移时在触发后新增的笔画中解析出替换后的那一笔。
        /// </summary>
        private sealed class UILinkagePendingStroke
        {
            public Stroke StrokeReference;
            public HashSet<Stroke> StrokesAtTrigger;
            public Point FirstPointInkCanvas;
            public Point FirstPointScreen;

            /// <summary>登记时刻：仅短时间内的笔迹才是「绑定尚未完成」的首笔，超时不再迁移。</summary>
            public DateTime CreatedUtc;
        }

        /// <summary>
        /// 待迁移首笔缓存：绑定完成前落在支持联动应用上的笔迹先留在主画布，
        /// 绑定完成后迁入独立画布（首笔识别要求）。
        /// </summary>
        private readonly List<UILinkagePendingStroke> _uiLinkagePendingStrokes = new List<UILinkagePendingStroke>();

        // ---------------- 节流 ----------------

        private IntPtr _lastCaptureHwnd;
        private DateTime _lastCaptureTimeUtc = DateTime.MinValue;

        /// <summary>UI 联动目标应用类型（决定视口定位与滚动读写方式）。</summary>
        internal enum UILinkageTargetKind
        {
            Word,
            SumatraPdf
        }

        /// <summary>独立画布实例：绑定一个目标应用顶层窗口及其文档视口。</summary>
        internal sealed class UILinkageCanvasInstance
        {
            public Ink_Canvas.Controls.IndependentCanvas Canvas;
            public IntPtr BoundHwnd;
            public AutomationElement WindowElement;
            public AutomationElement DocElement;
            public Rect ScreenRect = Rect.Empty;
            public string DocName;
            public bool Bound;

            /// <summary>
            /// 该实例在「同一文档名」分组内的窗口槽位（1 起；0 = 未分配/已释放）。
            /// 用作 <c>.isf</c> / <c>.state.json</c> 的持久化键后缀，必须是跨会话稳定的值
            /// —— 因此不能用 HWND（见 <see cref="GetUILinkageWindowKeySuffix"/>）。
            /// </summary>
            public int WindowSlot;

            /// <summary>联动目标类型（Word / SumatraPDF）。</summary>
            public UILinkageTargetKind Kind = UILinkageTargetKind.Word;

            /// <summary>SumatraPDF 文档渲染画布子窗口（SUMATRA_PDF_CANVAS）句柄；Word 实例为 Zero。</summary>
            public IntPtr CanvasHwnd;

            // ---- SumatraPDF 滚动单位校准（端点往返一次标定，窗口尺寸/缩放变化按比例换算） ----

            /// <summary>
            /// 校准得到的滚动条「单位/视口像素」比（竖直/水平）。SumatraPDF 写入的 nPage 与真实
            /// 视口单位数存在口径差（随文档/显示模式 1×/2× 不等），以端点停靠位置反推为准；
            /// 0 = 未校准（退回 nPage 估计）。
            /// </summary>
            public double SumatraUnitsPerPxV;
            public double SumatraUnitsPerPxH;

            // ---- 最近一次 WordSync 读数（滚动记忆 / 长截屏使用，DIP 与百分比） ----
            public double LastVpPercent = double.NaN;
            public double LastHpPercent = double.NaN;
            public double LastViewW;
            public double LastViewH;
            public double LastExtW;
            public double LastExtH;
            public double LastOffsetX;
            public double LastOffsetY;

            /// <summary>长截屏进行中：暂停 WordSync 对该实例的滚动同步，避免与截图滚动互相干扰。</summary>
            public bool IsCapturingLongScreenshot;

            /// <summary>长截屏开始时刻，供看门狗判定卡死（见 <see cref="UILinkageCaptureWatchdogTimeout"/>）。</summary>
            public DateTime CaptureStartedUtc = DateTime.MinValue;

            /// <summary>
            /// 「墨迹转形状」上下文（本画布专属的最近笔迹缓冲 / 已纠正圆列表 / 替换实现）。
            /// 每块画布一份，避免与主画布的 <c>newStrokes</c>/<c>circles</c> 串写。
            /// </summary>
            public InkToShapeTargetContext InkToShapeContext;

            // ---- 缩放支持（状态栏缩放指示 + 墨迹按比例重缩放） ----

            /// <summary>缓存的缩放指示元素（状态栏内）；读取失败后冷却重找。</summary>
            public AutomationElement ZoomElement;

            /// <summary>缩放指示元素下次允许查找的时间（冷却节流，避免高频 UIA 遍历）。</summary>
            public DateTime NextZoomFindUtc = DateTime.MinValue;

            /// <summary>最近一次读到的 Word 缩放百分比（NaN = 尚未读到）。</summary>
            public double LastZoomPercent = double.NaN;

            /// <summary>载入墨迹时按文档记忆的「保存时缩放百分比」（首次读到当前缩放时消费重缩放）。</summary>
            public double SavedZoomPercent = double.NaN;

            /// <summary>
            /// 载入的历史墨迹笔迹对象（保存时可能是另一缩放比例）。首次读到当前缩放时
            /// 只对这批笔迹按比例重缩放，避免误缩放绑定后新写的墨迹。
            /// </summary>
            public List<Stroke> LoadedInkStrokes;

            // ---- 横向页面定位（仅 Word；页面化视图居中/贴左的实测基准） ----

            /// <summary>缓存的页面子元素（Word 在 Document 下以 Custom 子元素暴露当前页，如「页面 1」）。</summary>
            public AutomationElement PageElement;

            /// <summary>页面子元素下次允许查找的时间（冷却节流）。</summary>
            public DateTime NextPageFindUtc = DateTime.MinValue;

            /// <summary>页面矩形连续读取失败计数（超过阈值才判元素失效重找；瞬时失败沿用上次读数）。</summary>
            public int PageReadFailureCount;

            /// <summary>最近一次实测的页面左缘视口位移（DIP，NaN = 尚无有效读数）。</summary>
            public double LastPageLeftDip = double.NaN;

            // ---- 横向滚动读数（仅 Word；水平滚动条优先，页面实测只用于标定绝对帧） ----

            /// <summary>
            /// Word 文本区窗口（类名 <c>_WwG</c>）句柄：横向滚动位置与范围取自其水平滚动条
            /// （<c>GetScrollInfo(SB_HORZ)</c>）。Word 的 UIA ScrollPattern 横向读数不可靠
            /// （横向常报 NoScroll/0、HorizontalViewSize 报 100），横向必须另找可靠来源。
            /// </summary>
            public IntPtr WordTextAreaHwnd;

            /// <summary>文本区窗口下次允许重找的时间（冷却节流，避免每拍枚举子窗口）。</summary>
            public DateTime NextWordTextAreaFindUtc = DateTime.MinValue;

            /// <summary>
            /// 水平轴「滚动条帧 → 页面实测帧」的常量偏移（DIP，NaN = 尚未标定）。
            /// <para>
            /// 水平<b>位置</b>以滚动条读数为准（可靠、与窗口尺寸/DPI 无关），但绝对<b>对齐</b>
            /// 沿用页面左缘实测帧：两者只差一个常量（页面居中留白、桌面边距）。标定一次后一直沿用，
            /// 避免在两种口径之间来回跳导致墨迹横移；页面实测帧发生跳变（元素失效、被裁剪、
            /// 重排中）时保留旧值 —— 位置仍由滚动条推进，不会因此停住或弹回。
            /// </para>
            /// </summary>
            public double WordHorizontalFrameOffset = double.NaN;

            /// <summary>
            /// 上一次帧标定取样时的滚动条偏移（DIP，NaN = 尚未取样）。
            /// 与 <see cref="LastWordFrameSamplePageLeftDip"/> 一起判定「页面实测是否确实跟着滚动」：
            /// 页面读数卡住（元素被裁剪/失效但仍返回旧矩形）时若还去跟随它，标定出的帧会把滚动
            /// 位移整个抵消掉 —— 那正是「墨迹不滚动」的另一种成因。
            /// </summary>
            public double LastWordFrameSampleBarOffset = double.NaN;

            /// <summary>上一次帧标定取样时的页面左缘实测值（DIP）。</summary>
            public double LastWordFrameSamplePageLeftDip = double.NaN;

            /// <summary>横向读数最近一次写诊断日志的时间与摘要（变化时才写，避免每拍刷日志）。</summary>
            public DateTime LastWordHorizontalTraceUtc = DateTime.MinValue;
            public string LastWordHorizontalTraceKey;

            // ---- 遮挡裁剪缓存 ----

            /// <summary>
            /// 上次计算遮挡裁剪时的视口矩形（屏幕物理像素；<see cref="Rect.Empty"/> = 尚未计算）。
            /// <para>
            /// 与 <see cref="LastClipOccludedRects"/> 一起作为缓存键：二者都没变就不重新构造
            /// <c>OpacityMask</c>。这块画布在目标窗口最大化时等于整屏，每 250ms 无条件下写遮挡
            /// 蒙版会让整块全屏画布重新合成（分层窗口下是软件路径），是本功能最主要的 CPU 来源。
            /// </para>
            /// </summary>
            public Rect LastClipViewportRect = Rect.Empty;

            /// <summary>上次计算裁剪时使用的 DPI 缩放（与矩形一起构成缓存键）。</summary>
            public double LastClipDpiScaleX;
            public double LastClipDpiScaleY;

            /// <summary>上次用于裁剪的遮挡矩形集合（屏幕物理像素；null = 尚未计算）。</summary>
            public List<Rect> LastClipOccludedRects;

            /// <summary>
            /// 滚动读数「连续」失败次数（成功一次即清零）。
            /// 见 <see cref="UILinkageScrollReadFailureLimit"/>：单次失败不应直接解绑。
            /// </summary>
            public int ScrollReadFailureCount;
        }

        /// <summary>按文档持久化的滚动位置（UILinkageInk/<doc>.json）。</summary>
        private sealed class UILinkageScrollState
        {
            [Newtonsoft.Json.JsonProperty("verticalPercent")]
            public double VerticalPercent { get; set; } = double.NaN;

            [Newtonsoft.Json.JsonProperty("horizontalPercent")]
            public double HorizontalPercent { get; set; } = double.NaN;

            /// <summary>保存墨迹时的 Word 缩放百分比：载入时按当前缩放/保存缩放重缩放历史墨迹。</summary>
            [Newtonsoft.Json.JsonProperty("zoomPercent")]
            public double ZoomPercent { get; set; } = double.NaN;

            /// <summary>
            /// <see cref="HorizontalPercent"/> 是否来自可信读数（Word 文本区水平滚动条）。
            /// <para>
            /// 旧版文件没有这个字段（默认 false）：当时水平百分比取自 Word 的 UIA
            /// <c>ScrollPattern.HorizontalScrollPercent</c>，而该横向读数不可靠（常报 0/NoScroll），
            /// 恢复时把 0 回写进 Word 就会把横向滚动拉回首位。因此旧值一律不参与横向恢复。
            /// </para>
            /// </summary>
            [Newtonsoft.Json.JsonProperty("horizontalPercentTrusted")]
            public bool HorizontalPercentTrusted { get; set; }

            [Newtonsoft.Json.JsonProperty("updatedAt")]
            public string UpdatedAt { get; set; }
        }

        private static bool UILinkageIsDebugEnabled
        {
            get
            {
                var s = SettingsManager.Settings?.UILinkage;
                return s != null && s.IsDebugEnabled;
            }
        }

        /// <summary>仅当 UI 联动总开关与 Word 开关同时开启时为 true（真正的 Word 联动功能）。</summary>
        private static bool UILinkageIsWordEnabled
        {
            get
            {
                var s = SettingsManager.Settings?.UILinkage;
                return s != null && s.IsEnabled && s.WordEnabled;
            }
        }

        /// <summary>仅当 UI 联动总开关与 SumatraPDF 开关同时开启时为 true。</summary>
        private static bool UILinkageIsSumatraPdfEnabled
        {
            get
            {
                var s = SettingsManager.Settings?.UILinkage;
                return s != null && s.IsEnabled && s.SumatraPdfEnabled;
            }
        }

        /// <summary>任一支持联动的应用开关是否开启（首笔缓存等通用逻辑使用）。</summary>
        private static bool UILinkageHasAnyTargetEnabled => UILinkageIsWordEnabled || UILinkageIsSumatraPdfEnabled;

        /// <summary>捕捉目标是否落在任一已绑定实例的窗口上（调试高亮随之隐藏）。</summary>
        private bool IsBoundTargetCaptured =>
            _uiLinkageCapturedHwnd != IntPtr.Zero &&
            _uiLinkageInstances.Any(i => i.Bound && i.BoundHwnd == _uiLinkageCapturedHwnd);

        private void EnsureUILinkageTimer()
        {
            if (_uiLinkageTimer != null) return;
            _uiLinkageTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = UILinkagePollInterval
            };
            _uiLinkageTimer.Tick += (_, _) =>
            {
                // 逐阶段独立兜底：一次 UIA/跨进程读取失败不应中断本轮其余阶段，
                // 更不应把异常抛给 DispatcherUnhandledException（本应用会据此判定崩溃并弹窗/重启）。
                RunUILinkageStage("实时识别", UpdateUILinkageForegroundWatch);
                RunUILinkageStage("状态同步", SyncUILinkageCanvasState);
                RunUILinkageStage("调试面板", UpdateUILinkageDebugUi);
                RunUILinkageStage("滚动同步", UpdateUILinkageScrollSync);
                RunUILinkageStage("几何跟随", UpdateUILinkageCanvasGeometry);
            };
            _uiLinkageTimer.Start();
        }

        private DateTime _uiLinkageStageErrorLogUtc = DateTime.MinValue;

        /// <summary>
        /// 执行一个 UI 联动轮询阶段并吞掉异常。异常按 5 秒节流写日志，
        /// 避免目标窗口异常时每 250ms 刷一条。
        /// </summary>
        private void RunUILinkageStage(string stageName, Action stage)
        {
            try
            {
                stage();
            }
            catch (Exception ex)
            {
                var now = DateTime.UtcNow;
                if ((now - _uiLinkageStageErrorLogUtc).TotalSeconds < 5) return;
                _uiLinkageStageErrorLogUtc = now;
                LogHelper.WriteLogToFile(
                    $"UI联动轮询阶段「{stageName}」异常（已跳过本轮该阶段）: {ex.Message}",
                    LogHelper.LogType.Warning);
            }
        }

        /// <summary>轮询刷新调试面板与高亮边框：仅做廉价的位置/信息读取，不做整树遍历。</summary>
        private void UpdateUILinkageDebugUi()
        {
            if (UILinkageDebugHud == null || UILinkageDebugHighlight == null) return;

            if (!UILinkageIsDebugEnabled)
            {
                // 面板隐藏时同步退出鼠标抓取（钩子只在抓取期间安装）。
                SetUILinkageGrabMode(false);
                UILinkageDebugHud.Visibility = Visibility.Collapsed;
                UILinkageDebugHighlight.Visibility = Visibility.Collapsed;
                CollapseUILinkageOcclusion();
                return;
            }

            UILinkageDebugHud.Visibility = Visibility.Visible;
            RefreshUILinkageHighlight();
            RefreshUILinkageOcclusion();
            RefreshUILinkageHudText();
        }

        /// <summary>
        /// 墨迹提交后处理：调试状态下首笔位置即采集点，定位批注层后方窗口内命中的组件。
        /// 非调试状态的识别已改为「实时读取当前焦点窗口 + 监听其树」（见 MW_UILinkageWatch.cs），
        /// 这里只登记待迁移首笔，不再做整树采样。
        /// 只同步做代价极低的 Win32 取窗，昂贵的 UIA 整树遍历放到后台线程。
        /// </summary>
        internal void HandleUILinkageDebugAfterStroke(Stroke stroke)
        {
            try
            {
                bool debug = UILinkageIsDebugEnabled;
                if (!debug && !UILinkageHasAnyTargetEnabled) return;
                if (stroke == null || stroke.StylusPoints.Count == 0) return;
                if (inkCanvas == null) return;

                var screenPt = inkCanvas.PointToScreen(stroke.StylusPoints[0].ToPoint());
                var selfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

                // 已绑定独立画布且首点落在其视口内：由独立画布处理，不视为监听起始。
                if (AnyUILinkageCanvasContains(screenPt))
                    return;

                // 快速 Win32：仅取首点后方顶层窗口句柄（避免同步 UIA 整树遍历阻塞 UI）。
                IntPtr hwnd = UILinkageWindowFinder.FindWindowBehind((int)screenPt.X, (int)screenPt.Y, selfHwnd);

                // 记录待迁移首笔：绑定完成后若落在独立画布视口内则迁入（首笔识别要求）。
                // 触发时的实例可能被收笔后处理替换，同时记录主画布笔画快照与首点位置。
                if (UILinkageHasAnyTargetEnabled && hwnd != IntPtr.Zero)
                {
                    _uiLinkagePendingStrokes.Add(new UILinkagePendingStroke
                    {
                        StrokeReference = stroke,
                        StrokesAtTrigger = new HashSet<Stroke>(inkCanvas.Strokes),
                        FirstPointInkCanvas = stroke.StylusPoints[0].ToPoint(),
                        FirstPointScreen = screenPt,
                        CreatedUtc = DateTime.UtcNow
                    });
                    while (_uiLinkagePendingStrokes.Count > UILinkagePendingStrokeLimit)
                        _uiLinkagePendingStrokes.RemoveAt(0);
                }

                // 非调试状态：识别由焦点窗口实时监听负责，不做整树采样。
                if (!debug) return;

                // 节流：同一窗口短时间内不重复遍历。
                if (hwnd == _lastCaptureHwnd && (DateTime.UtcNow - _lastCaptureTimeUtc) < UILinkageCaptureThrottle)
                {
                    _uiLinkageCapturedHwnd = hwnd;
                    return;
                }
                _lastCaptureHwnd = hwnd;
                _lastCaptureTimeUtc = DateTime.UtcNow;
                _uiLinkageCapturedHwnd = hwnd;

                if (hwnd == IntPtr.Zero)
                {
                    _uiLinkageCapturedWindow = null;
                    _uiLinkageCapturedElement = null;
                    _uiLinkageCachedInfo = null;
                    _uiLinkageCachedTree = null;
                    RefreshDebugOverlays();
                    return;
                }

                var pt = screenPt;
                var h = hwnd;
                bool wordEnabled = UILinkageIsWordEnabled;
                bool sumatraEnabled = UILinkageIsSumatraPdfEnabled;
                bool showTree = _uiLinkageShowTree;

                Task.Run(() => CaptureOnBackground(h, pt, selfHwnd, wordEnabled, sumatraEnabled, showTree));
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动组件捕捉失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 后台线程：UIA 整树遍历 + 组件定位 + 信息字符串生成，结果经 Dispatcher 回到 UI。
        /// <paramref name="usePointHitTest"/> 为 true（鼠标抓取）时优先用 UIA 命中测试取该点组件，
        /// 并排除自身进程元素；失败时退回在目标窗口子树内定位。
        /// </summary>
        private void CaptureOnBackground(
            IntPtr hwnd, Point screenPt, IntPtr selfHwnd, bool wordEnabled, bool sumatraEnabled, bool showTree,
            bool usePointHitTest = false)
        {
            try
            {
                var windowEl = AutomationElement.FromHandle(hwnd);
                var element = usePointHitTest
                    ? FindElementAtPointExcludingSelf(screenPt, selfHwnd) ?? FindDeepestContaining(windowEl, screenPt)
                    : FindDeepestContaining(windowEl, screenPt);

                bool isWord = wordEnabled && IsWordWindow(windowEl);
                bool isSumatra = sumatraEnabled && IsSumatraPdfWindow(windowEl);
                AutomationElement docEl = isWord ? FindDocumentOfWindow(windowEl) : null;

                string info = BuildDetailedInfo(element, windowEl);
                string tree = (showTree && element != null) ? BuildComponentTree(element, 6) : null;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _uiLinkageCapturedWindow = windowEl;
                    _uiLinkageCapturedElement = element;
                    _uiLinkageCapturedHwnd = hwnd;
                    _uiLinkageCachedInfo = info;
                    _uiLinkageCachedTree = tree;

                    if (isWord && docEl != null && UILinkageIsWordEnabled)
                        BindUILinkageCanvas(docEl, hwnd, windowEl, UILinkageTargetKind.Word);
                    else if (isSumatra && UILinkageIsSumatraPdfEnabled)
                        BindUILinkageCanvas(null, hwnd, windowEl, UILinkageTargetKind.SumatraPdf);

                    // 关闭的联动目标：解除对应类型实例的绑定（保留仍开启类型的实例）。
                    if (!UILinkageIsWordEnabled) UnbindUILinkageCanvasesOfKind(UILinkageTargetKind.Word);
                    if (!UILinkageIsSumatraPdfEnabled) UnbindUILinkageCanvasesOfKind(UILinkageTargetKind.SumatraPdf);

                    // 本轮采集已处理完毕，清掉未迁移的待迁移笔迹（留在主画布）。
                    _uiLinkagePendingStrokes.Clear();

                    RefreshDebugOverlays();
                }));
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动后台采集失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        private void RefreshDebugOverlays()
        {
            RefreshUILinkageHighlight();
            RefreshUILinkageOcclusion();
            RefreshUILinkageHudText();
        }

        private void RefreshUILinkageHighlight()
        {
            if (UILinkageDebugHighlightText == null) return;

            // 绑定到 Word 时不再显示蓝色监听边框。
            if (IsBoundTargetCaptured)
            {
                UILinkageDebugHighlight.Visibility = Visibility.Collapsed;
                return;
            }

            var el = _uiLinkageCapturedElement;
            if (el != null)
            {
                try
                {
                    var br = el.Current.BoundingRectangle;
                    if (!br.IsEmpty && br.Width > 0 && br.Height > 0)
                    {
                        var dpi = VisualTreeHelper.GetDpi(this);
                        var topLeft = PointFromScreen(br.TopLeft);

                        UILinkageDebugHighlight.Margin = new Thickness(topLeft.X, topLeft.Y, 0, 0);
                        UILinkageDebugHighlight.Width = br.Width / dpi.DpiScaleX;
                        UILinkageDebugHighlight.Height = br.Height / dpi.DpiScaleY;
                        UILinkageDebugHighlight.Visibility = Visibility.Visible;

                        UILinkageDebugHighlightText.Text =
                            $"{br.Width:F0}×{br.Height:F0}  @({topLeft.X:F0},{topLeft.Y:F0})  {GetScrollText(el)}";
                        return;
                    }
                }
                catch (ElementNotAvailableException)
                {
                    _uiLinkageCapturedElement = null;
                }
                catch
                {
                    // 忽略瞬时读取异常，隐藏高亮。
                }
            }

            UILinkageDebugHighlight.Visibility = Visibility.Collapsed;
        }

        private void RefreshUILinkageOcclusion()
        {
            if (UILinkageDebugOcclusionLayer == null) return;
            UILinkageDebugOcclusionLayer.Children.Clear();

            var el = _uiLinkageCapturedElement;
            if (el == null || _uiLinkageCapturedHwnd == IntPtr.Zero || IsBoundTargetCaptured)
            {
                UILinkageDebugOcclusionLayer.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var br = el.Current.BoundingRectangle;
                if (br.IsEmpty || br.Width <= 0 || br.Height <= 0)
                {
                    UILinkageDebugOcclusionLayer.Visibility = Visibility.Collapsed;
                    return;
                }

                var selfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var occluded = UILinkageWindowFinder.GetOccludedRects(_uiLinkageCapturedHwnd, selfHwnd, br);
                if (occluded.Count == 0)
                {
                    UILinkageDebugOcclusionLayer.Visibility = Visibility.Collapsed;
                    return;
                }

                var dpi = VisualTreeHelper.GetDpi(this);
                foreach (var rect in occluded)
                {
                    var topLeft = PointFromScreen(rect.TopLeft);
                    var frame = new Border
                    {
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                        BorderThickness = new Thickness(2),
                        Width = rect.Width / dpi.DpiScaleX,
                        Height = rect.Height / dpi.DpiScaleY,
                        IsHitTestVisible = false,
                        Background = Brushes.Transparent
                    };
                    System.Windows.Controls.Canvas.SetLeft(frame, topLeft.X);
                    System.Windows.Controls.Canvas.SetTop(frame, topLeft.Y);
                    UILinkageDebugOcclusionLayer.Children.Add(frame);
                }

                UILinkageDebugOcclusionLayer.Visibility = Visibility.Visible;
            }
            catch (ElementNotAvailableException)
            {
                _uiLinkageCapturedElement = null;
                UILinkageDebugOcclusionLayer.Visibility = Visibility.Collapsed;
            }
            catch
            {
                UILinkageDebugOcclusionLayer.Visibility = Visibility.Collapsed;
            }
        }

        private void CollapseUILinkageOcclusion()
        {
            if (UILinkageDebugOcclusionLayer == null) return;
            UILinkageDebugOcclusionLayer.Children.Clear();
            UILinkageDebugOcclusionLayer.Visibility = Visibility.Collapsed;
        }

        private void RefreshUILinkageHudText()
        {
            if (UILinkageDebugHudText == null) return;

            var sb = new StringBuilder();

            // 抓取模式状态行：提示当前处于「单击采样」状态与退出方式。
            if (_uiLinkageGrabModeActive)
            {
                sb.AppendLine(UILinkageGrabStatusText);
                sb.AppendLine();
            }

            sb.Append(string.IsNullOrEmpty(_uiLinkageCachedInfo)
                ? UILinkageNoCaptureText
                : _uiLinkageCachedInfo);

            if (_uiLinkageShowTree && _uiLinkageCachedTree != null)
            {
                sb.AppendLine();
                sb.AppendLine("=== 完整组件树 ===");
                sb.Append(_uiLinkageCachedTree);
            }

            UILinkageDebugHudText.Text = sb.ToString();
        }

        /// <summary>沿祖先链查找首个支持 <see cref="ScrollPattern"/> 的元素并返回滚动位置描述。</summary>
        private static string GetScrollText(AutomationElement el)
        {
            var current = el;
            int guard = 0;
            while (current != null && guard++ < 32)
            {
                try
                {
                    if (current.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && p is ScrollPattern sp)
                    {
                        double h = sp.Current.HorizontalScrollPercent;
                        double v = sp.Current.VerticalScrollPercent;
                        if (double.IsNaN(h)) h = -1;
                        if (double.IsNaN(v)) v = -1;
                        return $"滚动 H:{h:F0}% V:{v:F0}%";
                    }
                }
                catch (ElementNotAvailableException)
                {
                    break;
                }
                catch
                {
                    // 继续向上查找。
                }

                current = TreeWalker.ControlViewWalker.GetParent(current);
            }

            return "滚动 —";
        }

        private static string BuildComponentTree(AutomationElement root, int maxDepth)
        {
            var sb = new StringBuilder();
            int budget = 300;
            AppendNode(sb, TreeWalker.ControlViewWalker, root, 0, maxDepth, ref budget);
            return sb.ToString();
        }

        private static void AppendNode(
            StringBuilder sb, TreeWalker walker, AutomationElement el, int depth, int maxDepth, ref int budget)
        {
            if (el == null || depth > maxDepth || budget <= 0) return;

            try
            {
                budget--;
                string type = el.Current.ControlType?.ProgrammaticName ?? "?";
                int dot = type.LastIndexOf('.');
                if (dot >= 0 && dot + 1 < type.Length) type = type.Substring(dot + 1);
                string name = el.Current.Name ?? "";
                sb.AppendLine(new string(' ', depth * 2) + $"[{type}] {name}");
            }
            catch (ElementNotAvailableException)
            {
                return;
            }
            catch
            {
                return;
            }

            var child = walker.GetFirstChild(el);
            while (child != null && budget > 0)
            {
                AppendNode(sb, walker, child, depth + 1, maxDepth, ref budget);
                child = walker.GetNextSibling(child);
            }
        }

        /// <summary>在目标窗口 UIA 树中查找包含给定屏幕点的最深层组件（仅后台线程调用）。</summary>
        private static AutomationElement FindDeepestContaining(AutomationElement root, Point screenPt)
        {
            if (root == null) return null;

            var walker = TreeWalker.ControlViewWalker;
            var best = root;
            var stack = new Stack<AutomationElement>();

            var child = walker.GetFirstChild(root);
            while (child != null)
            {
                stack.Push(child);
                child = walker.GetNextSibling(child);
            }

            while (stack.Count > 0)
            {
                var el = stack.Pop();
                try
                {
                    var br = el.Current.BoundingRectangle;
                    if (!br.IsEmpty && br.Contains(screenPt))
                    {
                        best = el;
                        var c = walker.GetFirstChild(el);
                        while (c != null)
                        {
                            stack.Push(c);
                            c = walker.GetNextSibling(c);
                        }
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // 组件可能刚被移除，跳过。
                }
                catch
                {
                    // 忽略单个元素异常，继续。
                }
            }

            return best;
        }

        /// <summary>
        /// 用 UIA 命中测试取该屏幕点下最深的组件，并排除自身进程窗口
        /// （鼠标抓取时点击可能先落在本应用批注层上）。命中自身或读取失败返回 null，
        /// 由调用方退回在目标窗口子树内定位。
        /// </summary>
        private static AutomationElement FindElementAtPointExcludingSelf(Point screenPt, IntPtr selfHwnd)
        {
            try
            {
                uint selfPid = UILinkageWindowFinder.GetWindowProcessId(selfHwnd);
                var element = AutomationElement.FromPoint(screenPt);
                if (element == null) return null;

                int pid = element.Current.ProcessId;
                if (selfPid != 0 && (uint)pid == selfPid) return null;

                return element;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeRead(Func<string> read)
        {
            try { return read(); }
            catch { return null; }
        }

        private void UILinkageMoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_uiLinkageCapturedElement == null) return;
            try
            {
                var parent = TreeWalker.ControlViewWalker.GetParent(_uiLinkageCapturedElement);
                if (parent != null && parent != AutomationElement.RootElement)
                    _uiLinkageCapturedElement = parent;
            }
            catch (ElementNotAvailableException)
            {
                _uiLinkageCapturedElement = null;
            }

            RecomputeUILinkageCaches();
            RefreshUILinkageHighlight();
            RefreshUILinkageHudText();
        }

        private void UILinkageShowTree_Changed(object sender, RoutedEventArgs e)
        {
            _uiLinkageShowTree = UILinkageShowTreeCheck?.IsChecked == true;
            if (_uiLinkageShowTree && _uiLinkageCapturedElement != null)
                _uiLinkageCachedTree = BuildComponentTree(_uiLinkageCapturedElement, 6);
            RefreshUILinkageHudText();
        }

        private void UILinkageCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(BuildDetailedInfo(_uiLinkageCapturedElement, _uiLinkageCapturedWindow));
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动复制信息失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        private void RecomputeUILinkageCaches()
        {
            if (_uiLinkageCapturedElement == null)
            {
                _uiLinkageCachedInfo = null;
                _uiLinkageCachedTree = null;
                return;
            }

            _uiLinkageCachedInfo = BuildDetailedInfo(_uiLinkageCapturedElement, _uiLinkageCapturedWindow);
            _uiLinkageCachedTree = _uiLinkageShowTree ? BuildComponentTree(_uiLinkageCapturedElement, 6) : null;
        }

        private static string BuildDetailedInfo(AutomationElement el, AutomationElement windowEl)
        {
            if (el == null)
                return UILinkageNoCaptureText;

            var sb = new StringBuilder();
            sb.AppendLine("=== UI 联动采样信息 ===");

            // 1) 祖先链（Document → Custom → Edit），附屏幕物理像素 Rect。
            var chain = new List<AutomationElement>();
            var cur = el;
            int guard = 0;
            while (cur != null && guard++ < 40)
            {
                chain.Add(cur);
                cur = TreeWalker.ControlViewWalker.GetParent(cur);
            }

            AutomationElement docEl = null, customEl = null, editEl = null;
            sb.AppendLine("[组件祖先链]");
            for (int i = 0; i < chain.Count; i++)
            {
                var c = chain[i];
                string type = ShortType(c);
                string name = SafeRead(() => c.Current.Name) ?? "";
                if (docEl == null && type == "Document") docEl = c;
                if (customEl == null && type == "Custom") customEl = c;
                if (editEl == null && type == "Edit") editEl = c;
                var r = SafeRect(c);
                sb.AppendLine(new string(' ', i * 2) + $"[{type}] {name}  Rect=({r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0})");
            }

            sb.AppendLine($"[结构判定] Document={(docEl != null ? "是" : "否")} Custom={(customEl != null ? "是" : "否")} Edit={(editEl != null ? "是" : "否")}");

            // 2) Document 视口 ScrollPattern 与总尺寸估算。
            if (docEl != null)
                AppendViewportInfo(sb, docEl);
            else
                sb.AppendLine("[视口] 祖先链中未找到 Document 组件");

            // 3) Word 文本区（_WwG）滚动行信息。
            AppendWordScrollInfo(sb, windowEl);

            // 3.5) SumatraPDF 画布滚动信息（仅 SumatraPDF 窗口输出）。
            AppendSumatraScrollInfo(sb, windowEl);

            // 4) 视口缩放（状态栏百分比指示，尽力检测）。
            sb.AppendLine($"[视口缩放] {DetectZoom(windowEl)}");

            return sb.ToString();
        }

        private static void AppendViewportInfo(StringBuilder sb, AutomationElement docEl)
        {
            var r = SafeRect(docEl);
            sb.AppendLine($"[Document 视口 Rect] ({r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0})");

            try
            {
                if (docEl.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && p is ScrollPattern sp)
                {
                    string Fmt(double v) => (double.IsNaN(v) || v < 0) ? "—" : v.ToString("F0");
                    double hs = sp.Current.HorizontalViewSize;
                    double vs = sp.Current.VerticalViewSize;
                    double hp = sp.Current.HorizontalScrollPercent;
                    double vp = sp.Current.VerticalScrollPercent;

                    sb.AppendLine($"[视口 ViewSize] H:{Fmt(hs)}% V:{Fmt(vs)}%");
                    sb.AppendLine($"[视口 滚动位置] H:{Fmt(hp)}% V:{Fmt(vp)}%");

                    if (r.Width > 0 && hs > 0 && hs < 100)
                        sb.AppendLine($"[页面总宽度(估算)] {r.Width * 100.0 / hs:F0} px");
                    if (r.Height > 0 && vs > 0 && vs < 100)
                        sb.AppendLine($"[页面总高度(估算)] {r.Height * 100.0 / vs:F0} px");
                    return;
                }
            }
            catch { }

            sb.AppendLine("[视口 ScrollPattern] 不支持");
        }

        private static void AppendWordScrollInfo(StringBuilder sb, AutomationElement windowEl)
        {
            IntPtr hwnd = IntPtr.Zero;
            try { hwnd = (IntPtr)windowEl.Current.NativeWindowHandle; } catch { }

            if (hwnd == IntPtr.Zero)
            {
                sb.AppendLine("[Word 文本区滚动] 不可用");
                return;
            }

            var textArea = UILinkageScrollReader.FindWordTextArea(hwnd);
            if (textArea == IntPtr.Zero)
            {
                sb.AppendLine("[Word 文本区滚动] 未找到 _WwG 窗口");
                return;
            }

            if (UILinkageScrollReader.TryGetScroll(textArea, UILinkageScrollReader.SB_VERT, out var v))
                sb.AppendLine($"[Word 竖直滚动] 最小={v.Min} 总行={v.Max} 可见行={v.Page} 位置={v.Pos}");

            // 横向：连通／同步用的就是下面这一路读数（Word 的 ScrollPattern 横向不可靠，
            // 上面的 [视口 滚动位置] H 常常是 —/0，而文档其实宽于视口）。
            if (UILinkageScrollReader.TryGetWordHorizontalScroll(textArea, out var h))
                sb.AppendLine($"[Word 水平滚动] 位置={h.Pos} 单位总数={h.Total} 视口单位={h.Page} "
                    + $"可滚动上限={h.MaxPos} → {h.Pos * 100.0 / Math.Max(1, h.MaxPos):F1}%");
            else if (UILinkageScrollReader.TryGetScroll(textArea, UILinkageScrollReader.SB_HORZ, out var h0))
                sb.AppendLine($"[Word 水平滚动] 不可横向滚动（最小={h0.Min} 最大={h0.Max} 可见={h0.Page} 位置={h0.Pos}）");
            else
                sb.AppendLine("[Word 水平滚动] 读取失败");
        }

        /// <summary>
        /// SumatraPDF 画布滚动信息：文档渲染区为画布子窗口（SUMATRA_PDF_CANVAS），
        /// 无 UIA Document/ScrollPattern，滚动状态直接读其 Win32 滚动条。
        /// 非 SumatraPDF 窗口静默跳过（避免对其它应用输出噪声行）。
        /// </summary>
        private static void AppendSumatraScrollInfo(StringBuilder sb, AutomationElement windowEl)
        {
            IntPtr hwnd = IntPtr.Zero;
            try { hwnd = (IntPtr)windowEl.Current.NativeWindowHandle; } catch { }
            if (hwnd == IntPtr.Zero) return;

            var canvas = UILinkageScrollReader.FindSumatraCanvas(hwnd);
            if (canvas == IntPtr.Zero) return;

            if (UILinkageScrollReader.TryGetSumatraScrollPercents(canvas, 0, 0, out var vp, out var hp, out var vs, out var hs, out _))
                sb.AppendLine($"[SumatraPDF 画布滚动] V:{vp:F1}%（视口 {vs:F0}%）  H:{hp:F1}%（视口 {hs:F0}%）");
            else
                sb.AppendLine("[SumatraPDF 画布滚动] 读取失败");
        }

        private static string DetectZoom(AutomationElement windowEl)
        {
            if (windowEl == null) return "未检测到";
            try
            {
                // 优先：状态栏（Word 底部缩放指示位于状态栏内）。
                var statusBar = windowEl.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.StatusBar));

                if (statusBar != null)
                {
                    string zoom = ExtractZoomFromSubtree(statusBar);
                    if (zoom != null) return zoom;
                }
                else
                {
                    string zoom = ExtractZoomFromSubtree(windowEl);
                    if (zoom != null) return zoom;
                }

                return "未检测到";
            }
            catch { return "未检测到"; }
        }

        /// <summary>
        /// 在元素子树中 BFS 提取缩放百分比：依次尝试 ValuePattern、RangeValuePattern（缩放滑块）、
        /// Name 与 LegacyIAccessible 的 Name/Description。找不到返回 null。
        /// </summary>
        private static string ExtractZoomFromSubtree(AutomationElement root)
        {
            if (root == null) return null;

            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(root);

            int visited = 0;
            while (queue.Count > 0 && visited++ < 800)
            {
                var el = queue.Dequeue();
                try
                {
                    string type = el.Current.ControlType?.ProgrammaticName ?? "";

                    // 1) ValuePattern 值文本。
                    if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern vpp)
                    {
                        if (TryMatchZoom(vpp.Current.Value, out var z)) return z;
                    }

                    // 2) 缩放滑块（RangeValuePattern），范围限制避免误报滚动条。
                    bool isSlider = type.EndsWith(".Slider", StringComparison.Ordinal);
                    if (isSlider && el.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rp) && rp is RangeValuePattern rpp)
                    {
                        double val = rpp.Current.Value;
                        if (val >= 1 && val <= 800)
                            return $"{val:F0}%";
                    }

                    // 3) Name（形如 NNN%）。
                    if (TryMatchZoom(el.Current.Name, out var z2)) return z2;
                }
                catch
                {
                    // 忽略单个元素异常。
                }

                var n = walker.GetFirstChild(el);
                while (n != null) { queue.Enqueue(n); n = walker.GetNextSibling(n); }
            }

            return null;
        }

        private static bool TryMatchZoom(string text, out string zoom)
        {
            zoom = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"(\d{1,4})\s*%");
            if (m.Success)
            {
                zoom = m.Groups[1].Value + "%";
                return true;
            }
            return false;
        }

        private static string ShortType(AutomationElement el)
        {
            string type = SafeRead(() => el.Current.ControlType?.ProgrammaticName);
            if (string.IsNullOrEmpty(type)) return "?";
            int dot = type.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < type.Length) type = type.Substring(dot + 1);
            return type;
        }

        private static Rect SafeRect(AutomationElement el)
        {
            try { return el.Current.BoundingRectangle; }
            catch { return Rect.Empty; }
        }

        #region 应用联动（Word / SumatraPDF 独立画布，多实例）

        /// <summary>判断给定窗口元素是否属于 Word 进程（WINWORD）。</summary>
        private static bool IsWordWindow(AutomationElement windowEl)
        {
            if (windowEl == null) return false;
            try
            {
                int pid = windowEl.Current.ProcessId;
                var p = Process.GetProcessById(pid);
                return string.Equals(p.ProcessName, "WINWORD", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>判断给定窗口元素是否属于 SumatraPDF 进程。</summary>
        private static bool IsSumatraPdfWindow(AutomationElement windowEl)
        {
            if (windowEl == null) return false;
            try
            {
                int pid = windowEl.Current.ProcessId;
                var p = Process.GetProcessById(pid);
                return string.Equals(p.ProcessName, "SumatraPDF", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 读取实例视口的屏幕物理像素矩形。SumatraPDF 用画布子窗口矩形（原生子窗口，
        /// 直接读 Win32 矩形避免 UIA 往返）；Word 用 Document 元素矩形。
        /// </summary>
        private static Rect GetUILinkageViewportRect(UILinkageCanvasInstance inst)
            => inst == null
                ? Rect.Empty
                : GetUILinkageViewportRect(inst.Kind, inst.CanvasHwnd, inst.DocElement);

        private static Rect GetUILinkageViewportRect(UILinkageTargetKind kind, IntPtr canvasHwnd, AutomationElement docEl)
        {
            if (kind == UILinkageTargetKind.SumatraPdf && canvasHwnd != IntPtr.Zero
                && UILinkageWindowFinder.TryGetWindowRect(canvasHwnd, out var rect))
                return rect;
            return SafeRect(docEl);
        }

        /// <summary>
        /// 重新解析实例的视口（DocElement 失效后调用）：
        /// SumatraPDF 重找画布子窗口，Word 重找 Document 元素。成功返回 true。
        /// </summary>
        private bool EnsureUILinkageViewport(UILinkageCanvasInstance inst)
        {
            if (inst?.WindowElement == null) return false;

            if (inst.Kind == UILinkageTargetKind.SumatraPdf)
            {
                // 换文档/显示模式切换可能重建画布子窗口：重找并刷新句柄与元素。
                var canvasHwnd = UILinkageScrollReader.FindSumatraCanvas(inst.BoundHwnd);
                if (canvasHwnd == IntPtr.Zero) return false;
                inst.CanvasHwnd = canvasHwnd;
                inst.DocElement = AutomationElement.FromHandle(canvasHwnd);
                return inst.DocElement != null;
            }

            inst.DocElement = FindDocumentOfWindow(inst.WindowElement);
            if (inst.DocElement != null)
            {
                // Document 元素重建过：其页面子元素缓存一并失效。
                inst.PageElement = null;
                inst.PageReadFailureCount = 0;
            }
            return inst.DocElement != null;
        }

        /// <summary>按实例类型解绑（对应应用的联动开关关闭时调用）。</summary>
        private void UnbindUILinkageCanvasesOfKind(UILinkageTargetKind kind, bool captureLongScreenshot = true)
        {
            foreach (var inst in _uiLinkageInstances.Where(i => i.Kind == kind).ToList())
                UnbindUILinkageInstance(inst, captureLongScreenshot);
        }

        /// <summary>
        /// 校准 SumatraPDF 滚动条单位（绑定/换文档时一次，画布展示前进行）：
        /// 端点往返标定「真实视口单位数/视口像素」比，修正 nPage 口径差；
        /// 失败时保持 0（各处退回 nPage 估计）。
        /// </summary>
        private void CalibrateUILinkageSumatraScroll(UILinkageCanvasInstance inst, Rect viewportRect)
        {
            inst.SumatraUnitsPerPxV = 0;
            inst.SumatraUnitsPerPxH = 0;
            if (inst?.CanvasHwnd != IntPtr.Zero
                && !viewportRect.IsEmpty && viewportRect.Width > 0 && viewportRect.Height > 0
                && UILinkageScrollReader.TryCalibrateSumatraViewportUnits(
                    inst.CanvasHwnd, viewportRect.Width, viewportRect.Height,
                    out var perPxV, out var perPxH))
            {
                inst.SumatraUnitsPerPxV = perPxV;
                inst.SumatraUnitsPerPxH = perPxH;
            }
        }

        /// <summary>
        /// 计算 SumatraPDF 当前有效视口单位数（校准值按当前视口像素尺寸等比换算）。
        /// 未校准返回 0（调用方退回 nPage 估计）。
        /// </summary>
        private static void GetUILinkageSumatraEffUnits(
            UILinkageCanvasInstance inst, Rect viewportRect, out double effUnitsV, out double effUnitsH)
        {
            effUnitsV = inst?.SumatraUnitsPerPxV > 0 && !viewportRect.IsEmpty && viewportRect.Height > 0
                ? inst.SumatraUnitsPerPxV * viewportRect.Height
                : 0;
            effUnitsH = inst?.SumatraUnitsPerPxH > 0 && !viewportRect.IsEmpty && viewportRect.Width > 0
                ? inst.SumatraUnitsPerPxH * viewportRect.Width
                : 0;
        }

        /// <summary>按窗口直接查找首个 Document 元素。</summary>
        private static AutomationElement FindDocumentOfWindow(AutomationElement windowEl)
        {
            if (windowEl == null) return null;
            try
            {
                return windowEl.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            }
            catch { return null; }
        }

        /// <summary>读取 Document 元素的 ScrollPattern：滚动百分比与视口 ViewSize。</summary>
        private static bool ReadWordScroll(
            AutomationElement docEl, out double vp, out double hp, out double vs, out double hs)
        {
            vp = hp = vs = hs = double.NaN;
            try
            {
                if (docEl.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && p is ScrollPattern sp)
                {
                    vp = sp.Current.VerticalScrollPercent;
                    hp = sp.Current.HorizontalScrollPercent;
                    vs = sp.Current.VerticalViewSize;
                    hs = sp.Current.HorizontalViewSize;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>创建并挂载一块独立画布（无内部填充，滚动条向外延伸）。</summary>
        private Ink_Canvas.Controls.IndependentCanvas CreateUILinkageCanvas()
        {
            var canvas = new Ink_Canvas.Controls.IndependentCanvas
            {
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                BorderStroke = new SolidColorBrush(Color.FromArgb(0x88, 0x25, 0x63, 0xEB)),
                BorderStrokeThickness = new Thickness(2),
                ShowVerticalScrollBar = true,
                ShowHorizontalScrollBar = true
            };
            Panel.SetZIndex(canvas, UILinkageCanvasZIndex);
            // 收笔后处理（压感归一 + 平滑）挂在独立画布自己的笔迹集合上，
            // 复用宿主现成服务，不经过主画布的 ProcessCommittedStroke。
            canvas.InkSurface.StrokeCollected += IndependentCanvas_StrokeCollected;
            InkCanvasGridForInkReplay?.Children.Add(canvas);
            return canvas;
        }

        /// <summary>
        /// 把独立画布实例绑定到目标应用视口（Word Document / SumatraPDF 画布子窗口）。
        /// - 新窗口：创建新实例（支持多文件分屏），加载该文档历史墨迹并显示跟随提示。
        /// - 同窗口换文档：保存旧文档墨迹后清空重载，避免串笔迹。
        /// - 同窗口同文档：仅刷新几何，不动墨迹。
        /// SumatraPDF 的 docEl 传 null：内部按画布子窗口解析视口。
        /// </summary>
        private void BindUILinkageCanvas(
            AutomationElement docEl, IntPtr hwnd, AutomationElement windowEl, UILinkageTargetKind kind)
        {
            IntPtr canvasHwnd = IntPtr.Zero;
            if (kind == UILinkageTargetKind.SumatraPdf)
            {
                // SumatraPDF 无 UIA Document 元素，文档渲染区即画布子窗口（SUMATRA_PDF_CANVAS），
                // 以该子窗口为视口、以窗口标题提取文档名。
                canvasHwnd = UILinkageScrollReader.FindSumatraCanvas(hwnd);
                if (canvasHwnd == IntPtr.Zero) return;
                docEl = AutomationElement.FromHandle(canvasHwnd);
            }

            var r = GetUILinkageViewportRect(kind, canvasHwnd, docEl);
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return;

            var inst = _uiLinkageInstances.FirstOrDefault(i => i.BoundHwnd == hwnd);
            bool created = inst == null;

            if (created)
            {
                inst = new UILinkageCanvasInstance
                {
                    Canvas = CreateUILinkageCanvas(),
                    BoundHwnd = hwnd,
                    Bound = true,
                    Kind = kind,
                    CanvasHwnd = canvasHwnd
                };
                var captured = inst;
                inst.Canvas.OnUserScroll = (o, v) => OnUILinkageCanvasUserScroll(captured, o, v);
                inst.Canvas.OnScrollStepRequested = (o, d) => OnUILinkageCanvasScrollStep(captured, o, d);
                // 墨迹活动归属跟踪：新画布从「未被批注过」开始，避免 Ctrl+Z 被路由到空白画布。
                AttachUILinkageInkActivityTracking(inst.Canvas);
                ResetUILinkageInkActivity();
                _uiLinkageInstances.Add(inst);
            }
            else
            {
                // 复用实例：类型不会变化，但刷新画布子窗口句柄（SumatraPDF 可能重建）。
                inst.Kind = kind;
                if (canvasHwnd != IntPtr.Zero) inst.CanvasHwnd = canvasHwnd;
            }

            string docName = TryComputeUILinkageDocName(windowEl, out var detectedName)
                ? detectedName
                : (inst.DocName ?? "Untitled");

            if (created)
            {
                inst.DocName = docName;
                // 持久化键的窗口槽位必须在 LoadUILinkageInk 之前定下来（载入路径要用它派生文件名）。
                AssignUILinkageWindowSlot(inst);
                // SumatraPDF：先做一次滚动单位端点校准（画布尚未展示，端点往返不可见）。
                if (kind == UILinkageTargetKind.SumatraPdf)
                    CalibrateUILinkageSumatraScroll(inst, r);
                inst.Canvas.BeginScrollSession();
                LoadUILinkageInk(inst);
                inst.Canvas.ResetInkHistory();
                inst.Canvas.ShowFollowNotice();
                RestoreUILinkageScrollPosition(inst);
            }
            else if (!string.Equals(inst.DocName, docName, StringComparison.Ordinal))
            {
                // 同一窗口切换了文档：保存旧文档墨迹与滚动位置后清空重载，避免串笔迹。
                SaveUILinkageScrollPosition(inst);
                SaveUILinkageInk(inst);
                inst.Canvas.Strokes.Clear();
                // 换文档 = 释放旧文档的槽位，再为新文档重新分配（否则旧槽位会被本实例一直占着，
                // 导致同文档的另一个窗口被挤到更高的槽位、键随开关顺序漂移）。
                inst.WindowSlot = 0;
                inst.DocName = docName;
                AssignUILinkageWindowSlot(inst);
                // 新文档内容坐标系与旧帧无关：重置滚动帧，载入的内容锚定墨迹才能对齐。
                inst.Canvas.ResetScrollFrame();
                // 横向帧标定同理失效（页面版式/留白随文档变化）。
                ResetUILinkageWordHorizontalFrame(inst);
                // 形状识别缓冲绑定的是旧文档的笔迹引用与坐标，必须一并清空。
                ResetUILinkageInkToShapeBuffer(inst);
                // SumatraPDF：新文档布局不同，重新校准滚动单位。
                if (kind == UILinkageTargetKind.SumatraPdf)
                    CalibrateUILinkageSumatraScroll(inst, r);
                LoadUILinkageInk(inst);
                inst.Canvas.ResetInkHistory();
                RestoreUILinkageScrollPosition(inst);
            }

            inst.WindowElement = windowEl;
            inst.DocElement = docEl;
            UpdateUILinkageInstanceGeometry(inst, r);

            // 首笔识别：把绑定完成前落在该视口内的笔迹从主画布迁入独立画布。
            MovePendingStrokesInto(inst);

            SyncUILinkageCanvasState();
        }

        /// <summary>
        /// 解除单个实例绑定：记忆滚动位置、保存墨迹并移除画布。
        /// <paramref name="captureLongScreenshot"/> 为 true 且长截屏启动成功时，
        /// 画布保留在屏幕上（墨迹仍可见供滚动截屏），由截图完成回调移除。
        /// </summary>
        private void UnbindUILinkageInstance(UILinkageCanvasInstance inst, bool captureLongScreenshot = true)
        {
            if (inst == null) return;

            if (inst.Bound)
            {
                SaveUILinkageScrollPosition(inst);
                inst.Canvas?.FlushScrollAnimation();
                SaveUILinkageInk(inst);
                inst.Bound = false;
                // 释放文档内窗口槽位：下一个打开同名文档的窗口会补进这个空位，
                // 键不会因为「谁先谁后」而漂移。
                inst.WindowSlot = 0;
                _uiLinkageInstances.Remove(inst);

                if (captureLongScreenshot && StartUILinkageLongScreenshotCapture(inst))
                    return;
            }

            if (inst.Canvas != null)
            {
                inst.Canvas.Visibility = Visibility.Collapsed;
                InkCanvasGridForInkReplay?.Children.Remove(inst.Canvas);
                // 解绑后这块画布不再被使用：解除撤销历史对每条笔迹 StylusPointsChanged 的订阅。
                // 必须放在长截屏分支之后 —— 截图期间画布还留在屏幕上，历史仍要被保留。
                inst.Canvas.ReleaseSubscriptions();
            }
        }

        /// <summary>解除全部实例绑定（Word 联动关闭、窗口关闭时调用）。</summary>
        private void UnbindAllUILinkageCanvases(bool captureLongScreenshot = true)
        {
            if (_uiLinkageInstances.Count == 0) return;
            foreach (var inst in _uiLinkageInstances.ToList())
                UnbindUILinkageInstance(inst, captureLongScreenshot);
            _uiLinkagePendingStrokes.Clear();
        }

        /// <summary>
        /// 把绑定前落在该实例视口内的待迁移笔迹从主画布迁入独立画布。
        /// 触发那一笔可能已被收笔后处理（平滑/拉直/形状识别）替换为新实例：
        /// 在「触发后新增」的笔画中优先解析外接框覆盖触发首点的最新一笔；
        /// 未被替换时直接使用原实例。
        /// </summary>
        private void MovePendingStrokesInto(UILinkageCanvasInstance inst)
        {
            if (inst?.Canvas == null || _uiLinkagePendingStrokes.Count == 0) return;

            // 先把未烘焙的滚动位移落进笔迹数据，保证 AppliedScrollOffset 与视觉一致。
            inst.Canvas.FlushScrollAnimation();

            // 坐标换算（见 IndependentCanvas 的坐标不变式）：
            //   独立画布笔迹的数据坐标 d = 视口坐标 p - S（S = 已应用的滚动偏移），
            //   而主画布坐标到视口坐标只差画布 Margin。故 d = p - Margin - S。
            // 少减 S 会让迁入的笔迹整体偏移一个文档滚动距离。
            var margin = inst.Canvas.Margin;
            var scrollOffset = inst.Canvas.AppliedScrollOffset;
            var toLocalMatrix = new Matrix(1, 0, 0, 1,
                -margin.Left - scrollOffset.X, -margin.Top - scrollOffset.Y);
            var nowUtc = DateTime.UtcNow;

            foreach (var pending in _uiLinkagePendingStrokes)
            {
                try
                {
                    if (pending?.StrokesAtTrigger == null || inkCanvas == null) continue;

                    // 超时的登记笔迹与本次绑定无关（实时识别下画布建立通常只需一个探测周期）。
                    if (nowUtc - pending.CreatedUtc > UILinkagePendingStrokeTtl) continue;

                    // 在触发后新增的笔画里解析替换后的那一笔（外接框覆盖触发首点、距离最近者）。
                    Stroke target = null;
                    double bestDistance = double.MaxValue;
                    foreach (Stroke s in inkCanvas.Strokes)
                    {
                        if (pending.StrokesAtTrigger.Contains(s)) continue;

                        var b = s.GetBounds();
                        double dist = 0;
                        if (!b.Contains(pending.FirstPointInkCanvas))
                        {
                            double dx = Math.Max(Math.Max(b.Left - pending.FirstPointInkCanvas.X, 0),
                                pending.FirstPointInkCanvas.X - b.Right);
                            double dy = Math.Max(Math.Max(b.Top - pending.FirstPointInkCanvas.Y, 0),
                                pending.FirstPointInkCanvas.Y - b.Bottom);
                            dist = dx * dx + dy * dy;
                            if (dist > 64 * 64) continue; // 距触发首点过远，视为无关的新笔
                        }

                        if (target == null || dist <= bestDistance)
                        {
                            bestDistance = dist;
                            target = s;
                        }
                    }

                    // 未被替换：原实例仍在主画布，直接使用。
                    if (target == null
                        && pending.StrokeReference != null
                        && inkCanvas.Strokes.Contains(pending.StrokeReference))
                    {
                        target = pending.StrokeReference;
                    }

                    if (target == null || target.StylusPoints.Count == 0) continue;

                    var screenPt = inkCanvas.PointToScreen(target.StylusPoints[0].ToPoint());
                    if (!inst.ScreenRect.Contains(screenPt)) continue;

                    // 迁入独立画布用「克隆」而不是搬运同一个对象：同一 Stroke 被两个
                    // StrokeCollection 同时持有是 WPF 允许的（不抛异常），但主画布 TimeMachine
                    // 里那条「删除」历史仍引用原对象，之后在主画布撤销会把原对象重新加回主画布，
                    // 于是同一笔被渲染两次、两个撤销栈互相争抢。克隆让两侧彻底解耦。
                    var migrated = target.Clone();
                    migrated.Transform(toLocalMatrix, false);

                    // 迁移对主画布而言只是「这一笔从此归独立画布管」，不是用户擦除，
                    // 因此必须在 CodeInput 抑制下移除：否则
                    //   ① TimeMachine 会记一条可撤销的「擦除」，主画布 Ctrl+Z / 工具栏撤销
                    //      会把原笔迹贴回主画布 —— 而独立画布上的克隆还在，一笔两画布；
                    //   ② 若此刻正处橡皮擦模式，这条删除还会被 MW_TimeMachine 的
                    //      AddedStroke/ReplacedStroke 批处理吞掉（:749-756），
                    //      在抬笔时被误提交成一次「用户擦除」。
                    // CodeInput 分支在 StrokesOnStrokesChanged 早退（MW_TimeMachine.cs:739），
                    // 只是不写历史，笔迹集合本身照常变更。
                    // 独立画布自己的撤销栈挂在独立画布集合上，不受此处影响，迁移仍可撤销。
                    var previousCommitType = _currentCommitType;
                    _currentCommitType = CommitReason.CodeInput;
                    try
                    {
                        inkCanvas.Strokes.Remove(target);
                    }
                    finally
                    {
                        _currentCommitType = previousCommitType;
                    }

                    // 独立画布历史自动记录新增，可在独立画布内撤销。
                    inst.Canvas.Strokes.Add(migrated);
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"UI联动首笔迁移失败: {ex.Message}", LogHelper.LogType.Warning);
                }
            }

            _uiLinkagePendingStrokes.Clear();
        }

        /// <summary>任一已绑定且可见实例的视口是否包含该屏幕物理像素点。</summary>
        private bool AnyUILinkageCanvasContains(Point screenPt)
        {
            foreach (var inst in _uiLinkageInstances)
            {
                if (inst.Bound && inst.Canvas != null
                    && inst.Canvas.Visibility == Visibility.Visible
                    && inst.ScreenRect.Contains(screenPt))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 按主画布坐标查找该点所在的可见绑定实例。
        /// <para>
        /// 多个实例的视口可能重叠（两个 Word 窗口互相遮挡），此时必须返回 <b>Z 序最高</b>
        /// 的那个 —— 即后加入画布层的实例。所有联动画布的 <c>Panel.ZIndex</c> 都是
        /// <see cref="UILinkageCanvasZIndex"/>（600）且相等，因此「最后创建/最后加入」就是
        /// 实际显示在最上层的那个。历史实现取 <c>FirstOrDefault</c>（最早创建的那个），
        /// 会把输入、擦除、撤销都投给被遮挡在下面的文档 —— 跨文档串写。
        /// </para>
        /// </summary>
        internal bool TryGetUILinkageInstanceAt(Point inkCanvasPoint, out UILinkageCanvasInstance instance)
        {
            instance = null;
            if (inkCanvas == null) return false;
            try
            {
                var screenPt = inkCanvas.PointToScreen(inkCanvasPoint);
                // 逆序遍历 = 从最上层往下找，命中即返回。
                for (var i = _uiLinkageInstances.Count - 1; i >= 0; i--)
                {
                    var candidate = _uiLinkageInstances[i];
                    if (candidate?.Bound != true || candidate.Canvas == null) continue;
                    if (candidate.Canvas.Visibility != Visibility.Visible) continue;
                    if (!candidate.ScreenRect.Contains(screenPt)) continue;
                    instance = candidate;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>所有可见且已绑定的实例（清空按钮等批量操作用）。</summary>
        internal IEnumerable<UILinkageCanvasInstance> GetVisibleBoundUILinkageInstances()
            => _uiLinkageInstances.Where(i =>
                i.Bound && i.Canvas != null && i.Canvas.Visibility == Visibility.Visible);

        /// <summary>
        /// 是否存在可见的联动画布墨迹。
        /// 清空按钮需要据此判断「该不该走清屏流程」：主画布为空但 Word 上有批注时，
        /// 若只看主画布就不会清到联动墨迹。
        /// </summary>
        internal bool HasVisibleUILinkageInk()
            => GetVisibleBoundUILinkageInstances().Any(i => i.Canvas.Strokes.Count > 0);

        /// <summary>
        /// 把主画布(inkCanvas)坐标点换算为指定实例独立画布的「笔迹数据坐标」，
        /// 可直接用于对该实例 StrokeCollection 的命中测试与几何运算。
        /// <para>
        /// 独立画布与主画布同处 InkCanvasGridForInkReplay 且无渲染变换，视口坐标 = 主坐标 - Margin；
        /// 但滚动同步是靠把整个笔迹集合 Transform 平移实现的，数据坐标相对视口又偏移了
        /// -S（S = <see cref="Ink_Canvas.Controls.IndependentCanvas.AppliedScrollOffset"/>）。
        /// 故 d = 主坐标 - Margin - S。少减 S 会让橡皮擦在错误位置下刀。
        /// </para>
        /// </summary>
        internal bool TryGetUILinkageLocalPoint(Point inkCanvasPoint, out Point localPoint)
            => TryGetUILinkageLocalPoint(inkCanvasPoint, null, out localPoint);

        internal bool TryGetUILinkageLocalPoint(
            Point inkCanvasPoint, UILinkageCanvasInstance instance, out Point localPoint)
        {
            localPoint = default;
            if (instance == null && !TryGetUILinkageInstanceAt(inkCanvasPoint, out instance)) return false;
            if (instance?.Canvas == null || instance.Canvas.Visibility != Visibility.Visible) return false;

            // 显式传入实例时也必须校验点位确实落在它的视口内：橡皮擦在一段拖拽中会复用
            // 已解析的实例，若不校验，拖回白板后仍会按该实例换算并擦除 Word 上的批注。
            try
            {
                var screenPt = inkCanvas.PointToScreen(inkCanvasPoint);
                if (!instance.ScreenRect.Contains(screenPt)) return false;
            }
            catch
            {
                return false;
            }

            // 先把未烘焙的滚动位移落进笔迹数据，否则本次换算会与正在动画中的偏移错开。
            instance.Canvas.FlushScrollAnimation();

            var margin = instance.Canvas.Margin;
            var scrollOffset = instance.Canvas.AppliedScrollOffset;
            localPoint = new Point(
                inkCanvasPoint.X - margin.Left - scrollOffset.X,
                inkCanvasPoint.Y - margin.Top - scrollOffset.Y);
            return true;
        }

        /// <summary>
        /// 逻辑工具 → 独立画布 WPF 编辑模式。
        /// 原生湿墨迹管线激活时逻辑笔的物理 EditingMode 为 None（见 EnsureNativePenPhysicalEditingMode），
        /// 不能直接照抄物理模式；鼠标/图形/漫游模式下独立画布保持 None（不接收输入）。
        /// </summary>
        private InkCanvasEditingMode GetUILinkageEffectiveEditingMode()
        {
            if (inkCanvas == null) return InkCanvasEditingMode.None;

            switch (_currentToolMode)
            {
                case "pen":
                case "color":
                    return InkCanvasEditingMode.Ink;
                case "eraser":
                    return InkCanvasEditingMode.EraseByPoint;
                case "eraserByStrokes":
                    return InkCanvasEditingMode.EraseByStroke;
                case "select":
                    return InkCanvasEditingMode.Select;
                default:
                    // cursor / shape / roaming 等：独立画布不接收输入。
                    return InkCanvasEditingMode.None;
            }
        }

        /// <summary>
        /// 卫星独立画布与主画布共享批注栏状态（编辑模式 + 笔属性），但不共享墨迹。
        /// <para>
        /// 「卫星画布」包含两类：UI 联动批注层（Word / SumatraPDF）与内置图片查看器画布。
        /// 两者都由 <see cref="IndependentCanvasStateSync"/> 这一份实现驱动，
        /// 因此「主工具栏批注时可以在卫星画布上书写」的行为完全一致。
        /// </para>
        /// </summary>
        private void SyncUILinkageCanvasState()
        {
            // 交互判定必须用逻辑工具（_currentToolMode）：原生笔模式物理 EditingMode 是 None，
            // 若按物理模式判定会把卫星画布错误禁用，书写落到主画布。
            var effectiveMode = GetUILinkageEffectiveEditingMode();
            bool interactive = effectiveMode != InkCanvasEditingMode.None;

            var context = BuildIndependentCanvasSyncContext(effectiveMode, interactive);

            foreach (var inst in _uiLinkageInstances)
            {
                if (inst?.Canvas == null) continue;
                IndependentCanvasStateSync.Apply(inst.Canvas, context);
            }

            // 内置图片查看器：每张图片一块独立窗口里的画布，同样跟随主工具栏批注状态。
            foreach (var canvas in Ink_Canvas.Windows.ImageViewerManager.OpenCanvases)
                IndependentCanvasStateSync.Apply(canvas, context);

            // 查看器底栏的「墨迹书写」按钮高亮（幂等，值未变时不做事）。
            IndependentCanvasStateSync.NotifyAnnotationState(interactive);
        }

        /// <summary>
        /// 立即把主工具栏的批注状态推给所有卫星独立画布（UI 联动批注层 + 内置图片查看器画布）。
        /// <para>
        /// 常规路径是 250ms 轮询（<c>EnsureUILinkageTimer</c> 的「状态同步」阶段），
        /// 但工具切换、查看器底栏点「墨迹书写」之后必须立刻生效，否则用户会看到
        /// 「点了批注却还要等一下才能写」的迟滞。
        /// </para>
        /// </summary>
        internal void SyncSatelliteCanvasStateNow()
        {
            RunUILinkageStage("状态同步(立即)", SyncUILinkageCanvasState);
        }

        /// <summary>采集主画布侧的批注状态快照，供各卫星画布复用（避免每块画布各算一遍）。</summary>
        private IndependentCanvasStateSync.Context BuildIndependentCanvasSyncContext(
            InkCanvasEditingMode effectiveMode, bool interactive)
        {
            var context = new IndependentCanvasStateSync.Context
            {
                EffectiveMode = effectiveMode,
                Interactive = interactive,
                EraserShape = inkCanvas?.EraserShape,
                Attributes = inkCanvas?.DefaultDrawingAttributes,
                // 手掌擦：与主画布共用同一套接触面积判定与设置（见 BuildPalmEraserPolicy）。
                // 判定放在宿主这一侧（internal 的 PalmEraserPolicy / PalmEraserCalculator 不出圈），
                // 卫星画布只拿到一个「宽高 in、擦头宽度 out」的原始类型委托。
                // 闭包捕获画布本身，每次判定实时读取 canvas.IsPalmErasing，因此只需创建一次。
                PalmClassifierFactory = canvas =>
                    (double contactW, double contactH, out double eraserWidth) =>
                    {
                        eraserWidth = 0;
                        var evaluation = Ink_Canvas.Ink.PalmEraserCalculator.Evaluate(
                            contactW, contactH, BuildPalmEraserPolicy(canvas.IsPalmErasing));
                        if (!evaluation.ActivatesEraser) return false;
                        eraserWidth = evaluation.EraserWidthDip;
                        return true;
                    }
            };

            return context;
        }

        /// <summary>
        /// 轮询各实例视口滚动（Word 走 ScrollPattern，SumatraPDF 走 Win32 滚动条读数），
        /// 调用独立画布 SetScrollInfo 平移其墨迹并驱动滚动条。
        /// 窗口关闭/视口失效时解除该实例绑定。
        /// </summary>
        private void UpdateUILinkageScrollSync()
        {
            // 关闭的联动目标：解除对应类型实例的绑定。
            bool wordEnabled = UILinkageIsWordEnabled;
            bool sumatraEnabled = UILinkageIsSumatraPdfEnabled;
            if (!wordEnabled) UnbindUILinkageCanvasesOfKind(UILinkageTargetKind.Word);
            if (!sumatraEnabled) UnbindUILinkageCanvasesOfKind(UILinkageTargetKind.SumatraPdf);
            if (!wordEnabled && !sumatraEnabled)
            {
                _uiLinkagePendingStrokes.Clear();
                return;
            }

            // 白板模式：白板背景覆盖整个工作区，UI 联动层隐藏（几何跟随阶段负责隐藏画布）。
            // 此时目标窗口不可见、滚动位置也不会被用户改动，跳过滚动同步可避免读到的
            // 瞬时垃圾偏移把隐藏画布的墨迹推飞；退出白板后下一拍自然重新对齐。
            if (IsWhiteboardMode) return;

            foreach (var inst in _uiLinkageInstances.ToList())
            {
                if (inst?.Canvas == null) continue;

                if (!UILinkageWindowFinder.IsWindowAlive(inst.BoundHwnd))
                {
                    UnbindUILinkageInstance(inst);
                    continue;
                }

                // 窗口最小化：跳过滚动同步（几何刷新会隐藏画布；最小化时 UIA/窗口矩形是
                // 离屏垃圾值，用它驱动偏移会把墨迹推飞，恢复后首拍重新对齐即可）。
                if (UILinkageWindowFinder.IsMinimized(inst.BoundHwnd)) continue;

                // 长截屏进行中：跳过滚动同步，避免与截图用的程序性滚动互相干扰。
                if (inst.IsCapturingLongScreenshot) continue;

                var r = GetUILinkageViewportRect(inst);
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;

                double vp, hp, vs, hs;
                double sumatraZoomMetric = double.NaN;
                if (inst.Kind == UILinkageTargetKind.SumatraPdf)
                {
                    GetUILinkageSumatraEffUnits(inst, r, out var effV, out var effH);
                    if (!UILinkageScrollReader.TryGetSumatraScrollPercents(
                            inst.CanvasHwnd, effV, effH, out vp, out hp, out vs, out hs, out var pageUnitsV))
                    {
                        // 画布子窗口可能被重建（换文档/显示模式切换）：重找后再读一次。
                        // 两次都读不到也不再当场解绑：由下方的「连续失败计数」决定（见注释）。
                        if (EnsureUILinkageViewport(inst))
                        {
                            GetUILinkageSumatraEffUnits(inst, r, out effV, out effH);
                            UILinkageScrollReader.TryGetSumatraScrollPercents(
                                inst.CanvasHwnd, effV, effH, out vp, out hp, out vs, out hs, out pageUnitsV);
                        }
                    }

                    // 缩放度量：nPage（视口占用滚动条单位数）与视口像素高度之比随缩放线性变化；
                    // 窗口/侧栏尺寸变化时两者同步缩放、比值不变，因此可作为缩放变化检测。
                    if (pageUnitsV > 0) sumatraZoomMetric = pageUnitsV / r.Height * 100.0;
                }
                else
                {
                    if (!ReadWordScroll(inst.DocElement, out vp, out hp, out vs, out hs)
                        && EnsureUILinkageViewport(inst))
                    {
                        ReadWordScroll(inst.DocElement, out vp, out hp, out vs, out hs);
                    }
                }

                // 读数失败（竖直轴为 NaN）：累计「连续」失败次数，达到上限才判定实例失效并解绑。
                // 单次失败是常态（画布子窗口被重建、渲染线程尚未回写实际位置、切换显示模式），
                // 历史实现单次失败即解绑，于是「解绑 → 下一拍重新绑定 → 绑定内同步端点校准
                // （SB_BOTTOM + 最多 192 次阻塞 SendMessageTimeout，且发生在 UI 线程）→ 再读失败」
                // 形成死循环：CPU 与交互线程同时被打满，PDF 还会被反复甩到文档末尾再拉回。
                if (double.IsNaN(vp))
                {
                    if (++inst.ScrollReadFailureCount < UILinkageScrollReadFailureLimit) continue;
                    UnbindUILinkageInstance(inst, captureLongScreenshot: false);
                    continue;
                }

                inst.ScrollReadFailureCount = 0;

                // 读数瞬时无效（NoScroll=-1 / 越界 / ∞）：缩放、重排瞬间 Word/PDF 常返回
                // NoScroll 或离屏垃圾值，沿用会算出错误的 extH/offY，把整块墨迹瞬移甚至推离
                // 视口（画面表现即「笔迹突然消失」）。这是常态瞬态，保持上一拍偏移不动即可，
                // 等下一拍好读数，不累计连续失败次数（那套只用于判定窗口/视口真正失效）。
                if (!IsUILinkageValidPercent(vp) || !IsUILinkageValidPercent(vs))
                    continue;

                var dpi = VisualTreeHelper.GetDpi(this);
                double viewW = r.Width / dpi.DpiScaleX;
                double viewH = r.Height / dpi.DpiScaleY;

                // 水平轴（Word）：以文本区（_WwG）水平滚动条为准。Word 的 ScrollPattern 横向读数
                // 不可靠（文档宽于视口时也常报 NoScroll/0、ViewSize 报 100），沿用它推算会让横向偏移
                // 恒为 0 —— 画面表现即「画布横向滚动条弹回首位、墨迹不随横向滚动」。
                bool wordHFromBar = false;
                double barOffsetX = 0, barExtW = 0, barHpPercent = double.NaN;
                if (inst.Kind == UILinkageTargetKind.Word)
                {
                    wordHFromBar = TryReadUILinkageWordHorizontal(
                        inst, r, dpi, viewW, out barOffsetX, out barExtW, out barHpPercent);
                }

                double extH = viewH * (vs > 0 && vs < 100 ? 100.0 / vs : 1.0);
                double offY = (vp / 100.0) * Math.Max(0, extH - viewH);

                double extW, offsetX, hpPercent;
                if (wordHFromBar)
                {
                    // 滚动条读数自带位置与范围（同源同口径），不再叠加页面实测，避免两种口径互相打架。
                    extW = Math.Max(viewW, barExtW);
                    offsetX = barOffsetX;
                    hpPercent = barHpPercent;
                }
                else if (inst.Kind == UILinkageTargetKind.Word
                    && TryReadUILinkagePageLeftDip(inst, r, dpi, out double pageLeftDip))
                {
                    // 页面化视图：页宽适配视口时页面水平居中，居中留白只能由页面实测表达。
                    extW = viewW * (hs > 0 && hs < 100 ? 100.0 / hs : 1.0);
                    offsetX = -pageLeftDip;
                    // 这一路没有可信的横向百分比（ScrollPattern 横向不可靠）→ 保持 NaN，
                    // 滚动记忆据此判定「无可信读数」，绝不写成 0（0 会把 Word 横向拉回最左）。
                    hpPercent = double.NaN;
                }
                else if (inst.Kind == UILinkageTargetKind.SumatraPdf && IsUILinkageValidPercent(hp))
                {
                    // 仅 SumatraPDF 用百分比推横向偏移（其百分比由 Win32 滚动条换算，可靠）。
                    // Word 不再走这一路：其 ScrollPattern 横向读数不可靠（常报 NoScroll/0），
                    // 用它推偏移正是「横向偏移恒为 0」的成因。
                    extW = viewW * (hs > 0 && hs < 100 ? 100.0 / hs : 1.0);
                    offsetX = hp / 100.0 * Math.Max(0, extW - viewW);
                    hpPercent = hp;
                }
                else
                {
                    // 读数不可用：保持上一拍已应用的口径（停住），而不是塌回 0（弹回首位）。
                    extW = Math.Max(viewW, inst.LastExtW);
                    offsetX = inst.LastOffsetX;
                    hpPercent = double.NaN;
                }

                // 缩放支持：读缩放指示，缩放变化时把墨迹按比例重缩放（锚定文档内容）。
                // 必须在本轮 SetScrollInfo 之前执行，缩放锚点依赖当前已应用偏移。
                // Word 读状态栏缩放指示；SumatraPDF 无 UIA 暴露的缩放指示，
                // 用 nPage/视口像素比作度量（仅比值有意义）。
                if (inst.Kind == UILinkageTargetKind.Word)
                {
                    // 缩放变化会改变页面居中留白（跨过「有无横向滚动条」的边界时尤其明显）：
                    // 横向帧标定随之失效，重新标定（下一拍从当前读数取首帧）。
                    if (!double.IsNaN(HandleUILinkageZoomChange(inst, ReadUILinkageZoom(inst))))
                        ResetUILinkageWordHorizontalFrame(inst);
                }
                else
                {
                    // 缩放变化：滚动条单位/像素比同步按比例递推（免重新端点校准）。
                    // 首次读到度量时仅消费按文档记忆的历史缩放（重缩放载入墨迹），
                    // 校准比本就是当前缩放下的值，无需调整。
                    double zoomScale = HandleUILinkageZoomChange(inst, sumatraZoomMetric);
                    if (!double.IsNaN(zoomScale))
                    {
                        inst.SumatraUnitsPerPxV *= zoomScale;
                        inst.SumatraUnitsPerPxH *= zoomScale;
                    }
                }

                // 缓存本次读数，供滚动记忆与长截屏使用（LastOffsetX 为实际下发的有效偏移）。
                inst.LastVpPercent = vp;
                inst.LastHpPercent = hpPercent;
                inst.LastViewW = viewW;
                inst.LastViewH = viewH;
                inst.LastExtW = extW;
                inst.LastExtH = extH;
                inst.LastOffsetX = offsetX;
                inst.LastOffsetY = offY;

                inst.Canvas.SetScrollInfo(
                    new Size(viewW, viewH), new Size(extW, extH), new Vector(offsetX, offY), animate: true);
            }
        }

        // ---------------- 缩放支持（状态栏缩放读取 + 墨迹按比例重缩放） ----------------

        /// <summary>
        /// 读取当前 Word 缩放百分比（状态栏缩放指示）。缓存元素后每次只做一次廉价属性读取；
        /// 缓存失效后按冷却重找。读不到返回 NaN。
        /// </summary>
        private double ReadUILinkageZoom(UILinkageCanvasInstance inst)
        {
            if (inst.ZoomElement != null)
            {
                if (UILinkageZoomReader.TryReadZoom(inst.ZoomElement, out double cached))
                    return cached;

                // 缓存失效（状态栏重建等）：失效后冷却重找。
                inst.ZoomElement = null;
                inst.NextZoomFindUtc = DateTime.UtcNow + UILinkageZoomFindCooldown;
                return double.NaN;
            }

            if (DateTime.UtcNow < inst.NextZoomFindUtc) return double.NaN;
            inst.NextZoomFindUtc = DateTime.UtcNow + UILinkageZoomFindCooldown;

            inst.ZoomElement = UILinkageZoomReader.FindZoomElement(inst.WindowElement);
            if (inst.ZoomElement != null && UILinkageZoomReader.TryReadZoom(inst.ZoomElement, out double found))
                return found;
            return double.NaN;
        }

        /// <summary>
        /// 处理缩放读数：首次读到时消费按文档记忆的历史缩放（重缩放载入墨迹）；
        /// 之后检测到缩放变化时把全部墨迹按比例重缩放，保持墨迹锚定文档内容。
        /// 返回本次应用的缩放系数（未应用变化返回 NaN，供调用方联动其它按缩放递推的状态）。
        /// </summary>
        private double HandleUILinkageZoomChange(UILinkageCanvasInstance inst, double zoomPercent)
        {
            if (double.IsNaN(zoomPercent) || zoomPercent <= 0) return double.NaN;

            double previous = inst.LastZoomPercent;
            inst.LastZoomPercent = zoomPercent;

            if (double.IsNaN(previous) || previous <= 0)
            {
                ConsumeUILinkageSavedZoom(inst);
                return double.NaN;
            }

            // 忽略 1% 以内的抖动（百分比文本为整数读数，滑块读数可能带小数）；
            // 连续小变化由 LastZoomPercent 逐次推进，不会漏掉累计变化。
            if (Math.Abs(zoomPercent - previous) < 1.0) return double.NaN;

            double scale = zoomPercent / previous;
            ApplyUILinkageZoomScale(inst, scale);
            return scale;
        }

        /// <summary>
        /// 消费按文档记忆的历史缩放：载入墨迹保存于另一缩放比例时，首次读到当前缩放
        /// 即把这批载入墨迹按当前/保存比例重缩放（绑定后新写的墨迹已是当前比例，不参与）。
        /// </summary>
        private void ConsumeUILinkageSavedZoom(UILinkageCanvasInstance inst)
        {
            double saved = inst.SavedZoomPercent;
            inst.SavedZoomPercent = double.NaN;
            var loaded = inst.LoadedInkStrokes;
            inst.LoadedInkStrokes = null;

            if (double.IsNaN(saved) || saved <= 0) return;
            if (loaded == null || loaded.Count == 0) return;

            ApplyUILinkageZoomScale(inst, inst.LastZoomPercent / saved, loaded);
        }

        /// <summary>
        /// 把墨迹按缩放比 k 重缩放（含笔迹粗细），锚点为文档内容原点。
        /// <para>
        /// 数据坐标 d 对应内容位置 C = d + S（S 为画布当前已应用偏移，内容原点即 d = -S）；
        /// 缩放后 C' = C·k，故 d' = (d + S)·k − S = k·d + (k−1)·S。随后本轮
        /// <see cref="UpdateUILinkageScrollSync"/> 的 SetScrollInfo 会按新偏移把墨迹平移到
        /// 正确视口位置，两步合成后墨迹精确贴合缩放后的文档内容。
        /// </para>
        /// </summary>
        private void ApplyUILinkageZoomScale(
            UILinkageCanvasInstance inst, double k, IEnumerable<Stroke> strokes = null)
        {
            if (inst?.Canvas == null) return;
            if (double.IsNaN(k) || double.IsInfinity(k) || k <= 0 || Math.Abs(k - 1.0) < 0.0005) return;

            try
            {
                // 先烘焙未应用的滚动位移，保证「数据坐标 == 当前偏移下的视觉坐标」，
                // 缩放锚点（内容原点）在数据坐标系中的位置才可靠。
                inst.Canvas.FlushScrollAnimation();

                if (strokes == null) strokes = inst.Canvas.Strokes;

                var s = inst.Canvas.AppliedScrollOffset;
                var matrix = new Matrix(k, 0, 0, k, (k - 1) * s.X, (k - 1) * s.Y);
                foreach (Stroke stroke in strokes)
                    stroke.Transform(matrix, false);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动缩放同步失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        // ---------------- 横向页面定位（页面化视图居中位移的实测基准，仅 Word） ----------------

        /// <summary>
        /// 读取页面左缘相对 Document 视口左缘的位移（DIP）。
        /// <para>
        /// 页面化视图（打印版式）中，页宽适配视口时页面水平居中、超宽时贴左并出现横向
        /// 滚动条，ScrollPattern 的 hp 百分比无法表达居中位移，跨过「有无横向滚动条」
        /// 边界时墨迹会整体横移。Word 在 Document 下以 Custom 子元素（如「页面 1」）
        /// 暴露当前页，直接读其矩形即可精确得到页面左缘视口位置；横向各页共线，
        /// 任一页面元素均可用。瞬时读取失败沿用上次读数（页面不会瞬移），
        /// 连续失败才判定元素失效重找；完全不可用时由调用方退回 hp 推算。
        /// </para>
        /// </summary>
        private bool TryReadUILinkagePageLeftDip(
            UILinkageCanvasInstance inst, Rect docRect, DpiScale dpi, out double pageLeftDip)
        {
            pageLeftDip = 0;
            if (inst?.DocElement == null || docRect.IsEmpty || docRect.Width <= 0) return false;

            var el = inst.PageElement;
            if (el != null)
            {
                if (TryGetUILinkagePageLeftDip(el, docRect, dpi, out pageLeftDip))
                {
                    inst.PageReadFailureCount = 0;
                    inst.LastPageLeftDip = pageLeftDip;
                    return true;
                }

                // 瞬时无效读数（重排中等）：沿用上次页面位置，避免帧在两种基准间跳动；
                // 连续多次失败才判定元素失效，冷却重找。
                if (++inst.PageReadFailureCount <= 4)
                {
                    if (!double.IsNaN(inst.LastPageLeftDip))
                    {
                        pageLeftDip = inst.LastPageLeftDip;
                        return true;
                    }
                    return false;
                }

                inst.PageReadFailureCount = 0;
                inst.PageElement = null;
                inst.NextPageFindUtc = DateTime.UtcNow + UILinkagePageFindCooldown;
                return false;
            }

            if (DateTime.UtcNow < inst.NextPageFindUtc) return false;
            inst.NextPageFindUtc = DateTime.UtcNow + UILinkagePageFindCooldown;

            inst.PageElement = FindUILinkagePageElement(inst.DocElement);
            if (inst.PageElement != null
                && TryGetUILinkagePageLeftDip(inst.PageElement, docRect, dpi, out pageLeftDip))
            {
                inst.PageReadFailureCount = 0;
                inst.LastPageLeftDip = pageLeftDip;
                return true;
            }
            return false;
        }

        private static bool TryGetUILinkagePageLeftDip(
            AutomationElement pageEl, Rect docRect, DpiScale dpi, out double pageLeftDip)
        {
            pageLeftDip = 0;
            var pr = SafeRect(pageEl);
            if (pr.IsEmpty || pr.Width <= 0 || pr.Height <= 0) return false;

            double left = (pr.Left - docRect.Left) / dpi.DpiScaleX;
            double pageW = pr.Width / dpi.DpiScaleX;
            double viewW = docRect.Width / dpi.DpiScaleX;

            // 合理性：滚动到最右时页面左缘最多左移约一个页宽，超出视为无效读数
            // （如窗口最小化时的离屏垃圾矩形）。
            if (double.IsNaN(left) || double.IsInfinity(left)
                || Math.Abs(left) > 3 * Math.Max(pageW, viewW))
                return false;

            pageLeftDip = left;
            return true;
        }

        /// <summary>查找 Document 的页面子元素（ControlType.Custom，如「页面 1」）；仅直接子级，代价极低。</summary>
        private static AutomationElement FindUILinkagePageElement(AutomationElement docEl)
        {
            if (docEl == null) return null;
            try
            {
                var walker = TreeWalker.ControlViewWalker;
                var child = walker.GetFirstChild(docEl);
                int guard = 0;
                while (child != null && guard++ < 8)
                {
                    try
                    {
                        if (child.Current.ControlType == ControlType.Custom) return child;
                    }
                    catch
                    {
                        // 忽略单个子元素异常。
                    }
                    child = walker.GetNextSibling(child);
                }
            }
            catch
            {
                // 忽略遍历异常。
            }
            return null;
        }

        // ---------------- 横向滚动读数（Word：水平滚动条为准，页面实测只标定绝对帧） ----------------

        /// <summary>滚动百分比是否可信（NaN / 无穷 / NoScroll(-1) / 越界一律视为不可信）。</summary>
        private static bool IsUILinkageValidPercent(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= 100;

        /// <summary>
        /// 解析（并缓存）Word 文本区窗口句柄：句柄失效或冷却到期时重找。
        /// 找不到返回 Zero（调用方退回页面实测 / ScrollPattern 推算）。
        /// </summary>
        private static IntPtr ResolveUILinkageWordTextArea(UILinkageCanvasInstance inst)
        {
            if (inst == null || inst.BoundHwnd == IntPtr.Zero) return IntPtr.Zero;

            if (inst.WordTextAreaHwnd != IntPtr.Zero
                && UILinkageWindowFinder.IsWindowAlive(inst.WordTextAreaHwnd))
                return inst.WordTextAreaHwnd;

            inst.WordTextAreaHwnd = IntPtr.Zero;

            if (DateTime.UtcNow < inst.NextWordTextAreaFindUtc) return IntPtr.Zero;
            inst.NextWordTextAreaFindUtc = DateTime.UtcNow + UILinkageWordTextAreaFindCooldown;

            inst.WordTextAreaHwnd = UILinkageScrollReader.FindWordTextArea(inst.BoundHwnd);
            return inst.WordTextAreaHwnd;
        }

        /// <summary>
        /// 清空横向帧标定（Word 缩放变化、窗口换文档时调用）：下一拍从当前读数重新取首帧。
        /// 页面居中留白是随缩放/版式变化的量，沿用旧标定会让墨迹整体横移一个留白宽度。
        /// </summary>
        private static void ResetUILinkageWordHorizontalFrame(UILinkageCanvasInstance inst)
        {
            if (inst == null) return;
            inst.WordHorizontalFrameOffset = double.NaN;
            inst.LastWordFrameSampleBarOffset = double.NaN;
            inst.LastWordFrameSamplePageLeftDip = double.NaN;
        }

        /// <summary>
        /// 读取 Word 水平轴状态：偏移（DIP）、内容总宽（DIP）、滚动百分比。
        /// <para>
        /// <b>为什么不能沿用它原来的读法</b>：原先横向偏移取自 UIA <c>ScrollPattern</c> 的
        /// <c>HorizontalScrollPercent</c>/<c>HorizontalViewSize</c>，而 Word 这两个横向读数不可靠
        /// —— 文档明明宽于视口时也常报 <c>NoScroll</c>（-1）或 0（ViewSize 报 100）。
        /// 于是横向偏移恒为 0：独立画布横向滚动条弹回首位、墨迹不随 Word 横向滚动；
        /// 拖动画布横向滚动条时又会把「算不出来的 0」回写进 Word，把 Word 也拉回最左。
        /// </para>
        /// <para>
        /// 现在以文本区（<c>_WwG</c>）的水平滚动条为准：<c>nPos</c> 是位置、<c>nMax+1</c> 是内容总
        /// 单位数、<c>nPage</c> 是视口单位数，三者同源同口径，换算 DIP 用「视口宽 / nPage」即可
        /// （与单位本身是像素、缇还是列无关）；百分比按「位置 / 可滚动上限」换算，与
        /// <c>ScrollPattern</c> 百分比同义，可直接回写 <c>SetScrollPercent</c>。
        /// </para>
        /// <para>
        /// 绝对对齐（帧）仍沿用页面左缘实测：滚动条帧与页面帧只差一个常量（页面居中留白、
        /// 桌面边距），标定后一直沿用；标定要求「页面实测确实与滚动条同步位移」，
        /// 元素失效/被裁剪而返回旧矩形时保留旧标定 —— 位置始终由滚动条推进，不会停住也不会弹回。
        /// <b>内容未超出视口</b>（不可横向滚动）时不采用滚动条读数：那时页面居中留白只能由
        /// 页面实测表达，交由 <see cref="TryReadUILinkagePageLeftDip"/> 处理。
        /// </para>
        /// </summary>
        private bool TryReadUILinkageWordHorizontal(
            UILinkageCanvasInstance inst, Rect viewportRect, DpiScale dpi, double viewW,
            out double offsetDip, out double extentDip, out double percent, bool calibrateFrame = true)
        {
            offsetDip = extentDip = 0;
            percent = double.NaN;
            if (inst == null || viewW <= 0 || viewportRect.IsEmpty) return false;

            var textArea = ResolveUILinkageWordTextArea(inst);
            if (textArea == IntPtr.Zero) return false;
            if (!UILinkageScrollReader.TryGetWordHorizontalScroll(textArea, out var info)) return false;

            int maxPos = info.MaxPos;
            if (info.Page == 0 || maxPos <= 0) return false; // 不可横向滚动：交给页面实测帧

            double dipPerUnit = viewW / info.Page; // 视口宽 = nPage 个单位
            int pos = Math.Min(Math.Max(0, info.Pos - info.Min), maxPos);
            double barOffset = pos * dipPerUnit;
            extentDip = info.Total * dipPerUnit;
            percent = Math.Min(100.0, Math.Max(0.0, pos * 100.0 / maxPos));

            // 页面实测帧标定：只在该实测「确实跟着滚动条动」时才跟随，否则保留旧标定
            // （位置始终由滚动条推进，页面读数失效/卡住都不会让墨迹停住或弹回）。
            // calibrateFrame=false（拖动滚动条/点辅助按钮时的回写路径）跳过这次跨进程 UIA 读取：
            // 那里只需要范围与百分比，不需要标定。
            bool pageLeftReadable = false;
            if (calibrateFrame)
            {
                pageLeftReadable = TryReadUILinkagePageLeftDip(inst, viewportRect, dpi, out double pageLeftDip);
                if (pageLeftReadable)
                {
                    double rawFrame = -pageLeftDip - barOffset;

                    // 页面实测与滚动条必须同步位移：滚动条动了而页面读数没动（被裁剪、元素失效、
                    // 返回旧的常量矩形）说明这一路不可信 —— 此时若还跟随它，标定出的帧会把滚动
                    // 位移整个抵消掉，表现为「墨迹不滚动」。
                    bool trustworthy = true;
                    if (!double.IsNaN(inst.LastWordFrameSampleBarOffset))
                    {
                        double barDelta = barOffset - inst.LastWordFrameSampleBarOffset;
                        double pageDelta = -(pageLeftDip - inst.LastWordFrameSamplePageLeftDip);
                        trustworthy = Math.Abs(barDelta) < 0.5
                            || Math.Abs(pageDelta - barDelta) <= UILinkageWordHorizontalFrameToleranceDip;
                    }

                    inst.LastWordFrameSampleBarOffset = barOffset;
                    inst.LastWordFrameSamplePageLeftDip = pageLeftDip;

                    if (trustworthy
                        && (double.IsNaN(inst.WordHorizontalFrameOffset)
                            || Math.Abs(rawFrame - inst.WordHorizontalFrameOffset) <= UILinkageWordHorizontalFrameToleranceDip))
                        inst.WordHorizontalFrameOffset = rawFrame;
                }
            }

            double frame = double.IsNaN(inst.WordHorizontalFrameOffset) ? 0 : inst.WordHorizontalFrameOffset;
            offsetDip = barOffset + frame;

            if (calibrateFrame)
                TraceUILinkageWordHorizontal(inst, info, pageLeftReadable, barOffset, extentDip, percent, offsetDip);
            return true;
        }

        /// <summary>
        /// 横向读数诊断日志：只在「读数形态」变化时写（来源、页面实测是否可读、帧标定是否跳变），
        /// 且受 <see cref="UILinkageWordHorizontalTraceInterval"/> 节流。
        /// 横向行为异常时（弹回首位 / 墨迹不跟随）这条日志能直接指出是哪一路读数不成立。
        /// </summary>
        private static void TraceUILinkageWordHorizontal(
            UILinkageCanvasInstance inst, UILinkageScrollReader.RawScrollInfo info,
            bool pageLeftReadable, double barOffset, double extentDip, double percent, double offsetDip)
        {
            try
            {
                double frame = double.IsNaN(inst.WordHorizontalFrameOffset) ? 0 : inst.WordHorizontalFrameOffset;
                string key = $"{info.Pos}/{info.Total}/{info.Page}|{pageLeftReadable}|{frame:F1}|{inst.LastPageLeftDip:F1}";
                if (string.Equals(key, inst.LastWordHorizontalTraceKey, StringComparison.Ordinal)) return;
                if (DateTime.UtcNow - inst.LastWordHorizontalTraceUtc < UILinkageWordHorizontalTraceInterval) return;

                inst.LastWordHorizontalTraceKey = key;
                inst.LastWordHorizontalTraceUtc = DateTime.UtcNow;

                LogHelper.WriteLogToFile(
                    $"UI联动横向读数 | 滚动条 pos={info.Pos} 总数={info.Total} 视口={info.Page} → {percent:F1}%"
                    + $"（偏移 {barOffset:F0}dp、内容宽 {extentDip:F0}dp）；页面实测={(pageLeftReadable ? $"{inst.LastPageLeftDip:F0}dp" : "不可读")}"
                    + $"；帧标定={frame:F0}dp → 实际偏移 {offsetDip:F0}dp",
                    LogHelper.LogType.Trace);
            }
            catch
            {
                // 诊断日志失败不影响滚动同步。
            }
        }

        /// <summary>
        /// 轮询刷新各实例独立画布的位置/尺寸（跟随目标组件移动）、窗口最小化隐藏、
        /// 遮挡裁切，以及同窗口文档切换检测（标题变化时保存并重载墨迹，避免串笔迹）。
        /// </summary>
        private void UpdateUILinkageCanvasGeometry()
        {
            foreach (var inst in _uiLinkageInstances.ToList())
            {
                if (inst?.Canvas == null || !inst.Bound) continue;

                // 白板模式：白板背景应覆盖独立画布区域，UI 联动层让位于白板。
                // 仅临时隐藏（不解绑、不丢墨迹）；退出白板后由几何跟随自然恢复显示。
                if (IsWhiteboardMode)
                {
                    if (inst.Canvas.Visibility != Visibility.Collapsed)
                        inst.Canvas.Visibility = Visibility.Collapsed;
                    continue;
                }

                // 目标窗口最小化：临时隐藏画布（不解除绑定、不丢失墨迹）。
                if (UILinkageWindowFinder.IsMinimized(inst.BoundHwnd))
                {
                    if (inst.Canvas.Visibility != Visibility.Collapsed)
                        inst.Canvas.Visibility = Visibility.Collapsed;
                    continue;
                }

                if (inst.DocElement == null && !EnsureUILinkageViewport(inst))
                {
                    UnbindUILinkageInstance(inst);
                    continue;
                }

                var r = GetUILinkageViewportRect(inst);
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;

                // 看门狗：截图任务若没能正常收尾（未调度/线程被杀），标志会一直挂着，
                // 该实例的滚动同步与画布外观将永久卡在截图态，这里兜底复位。
                if (inst.IsCapturingLongScreenshot
                    && inst.CaptureStartedUtc != DateTime.MinValue
                    && DateTime.UtcNow - inst.CaptureStartedUtc > UILinkageCaptureWatchdogTimeout)
                {
                    LogHelper.WriteLogToFile(
                        $"UI联动长截屏超时（>{UILinkageCaptureWatchdogTimeout.TotalSeconds:F0}s），强制复位截图状态",
                        LogHelper.LogType.Warning);
                    ResetUILinkageCaptureState(inst);
                }

                // 长截屏期间视口几何已在启动时快照（拼接步距按当时尺寸计算）。若此刻仍去
                // SetViewportSize 会每拍触发一次布局，可能在两个切片之间改变画布尺寸；
                // 视口矩形没变就复用之，避免拼接错位与逐拍重排。
                if (inst.IsCapturingLongScreenshot && inst.ScreenRect == r) continue;

                UpdateUILinkageInstanceGeometry(inst, r);

                // 同窗口文档切换检测（轮询兜底，窗口标题变化即换绑墨迹）。
                if (TryComputeUILinkageDocName(inst.WindowElement, out var currentName)
                    && !string.Equals(currentName, inst.DocName, StringComparison.Ordinal))
                {
                    SaveUILinkageScrollPosition(inst);
                    SaveUILinkageInk(inst);
                    inst.Canvas.Strokes.Clear();
                    // 轮询兜底换档：与 BindUILinkageCanvas 的换文档分支保持同一套槽位/缓冲重置，
                    // 否则两条路径的行为会不一致（旧槽位残留、形状识别缓冲跨文档串用）。
                    inst.WindowSlot = 0;
                    inst.DocName = currentName;
                    AssignUILinkageWindowSlot(inst);
                    // 新文档内容坐标系与旧帧无关：重置滚动帧，载入的内容锚定墨迹才能对齐。
                    inst.Canvas.ResetScrollFrame();
                    // 横向帧标定同理失效（页面版式/留白随文档变化）。
                    ResetUILinkageWordHorizontalFrame(inst);
                    ResetUILinkageInkToShapeBuffer(inst);
                    // SumatraPDF：新文档布局不同，重新校准滚动单位（端点往返发生在换档瞬间）。
                    if (inst.Kind == UILinkageTargetKind.SumatraPdf)
                        CalibrateUILinkageSumatraScroll(inst, r);
                    LoadUILinkageInk(inst);
                    inst.Canvas.ResetInkHistory();
                    RestoreUILinkageScrollPosition(inst);
                }

                var dpi = VisualTreeHelper.GetDpi(this);
                UpdateUILinkageCanvasClip(inst, r, dpi);
            }
        }

        /// <summary>刷新单个实例画布的位置/尺寸/可见性（跟随目标组件移动）。</summary>
        private void UpdateUILinkageInstanceGeometry(UILinkageCanvasInstance inst, Rect screenRect)
        {
            if (inst?.Canvas == null) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            var topLeft = PointFromScreen(screenRect.TopLeft);
            double w = Math.Max(1, screenRect.Width / dpi.DpiScaleX);
            double h = Math.Max(1, screenRect.Height / dpi.DpiScaleY);

            if (inst.Canvas.Visibility != Visibility.Visible)
                inst.Canvas.Visibility = Visibility.Visible;

            inst.Canvas.SetViewportSize(w, h);
            var margin = new Thickness(topLeft.X, topLeft.Y, 0, 0);
            if (inst.Canvas.Margin != margin)
                inst.Canvas.Margin = margin;
            inst.ScreenRect = screenRect;
        }

        /// <summary>根据目标组件被遮挡情况，把独立画布（含墨迹/边框）裁掉被遮挡区域。</summary>
        private void UpdateUILinkageCanvasClip(UILinkageCanvasInstance inst, Rect screenRect, DpiScale dpi)
        {
            if (inst?.Canvas == null) return;

            var selfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var occluded = UILinkageWindowFinder.GetOccludedRects(inst.BoundHwnd, selfHwnd, screenRect);

            // 视口矩形、DPI 与遮挡集合都与上一拍完全一致：不重建几何、不重新赋值 OpacityMask。
            // 无条件下写会让整块画布失效重绘 —— 目标窗口最大化时该画布即整屏，且 Z 序上任何一个
            // 置顶窗口（自动隐藏任务栏的可见边条、其它程序的悬浮窗）都会让这里每 250ms 重建一次，
            // 表现为「识别成功 + 最大化后 ICCCE 的 CPU 猛然升高」。
            if (inst.LastClipViewportRect == screenRect
                && inst.LastClipDpiScaleX == dpi.DpiScaleX
                && inst.LastClipDpiScaleY == dpi.DpiScaleY
                && OccludedRectsEqual(inst.LastClipOccludedRects, occluded))
                return;

            inst.LastClipViewportRect = screenRect;
            inst.LastClipDpiScaleX = dpi.DpiScaleX;
            inst.LastClipDpiScaleY = dpi.DpiScaleY;
            inst.LastClipOccludedRects = occluded;

            if (occluded.Count == 0)
            {
                inst.Canvas.SetClip(null);
                return;
            }

            double w = screenRect.Width / dpi.DpiScaleX;
            double h = screenRect.Height / dpi.DpiScaleY;
            var visible = (Geometry)new RectangleGeometry(new Rect(0, 0, w, h));

            foreach (var rect in occluded)
            {
                double left = (rect.Left - screenRect.Left) / dpi.DpiScaleX;
                double top = (rect.Top - screenRect.Top) / dpi.DpiScaleY;
                double rw = rect.Width / dpi.DpiScaleX;
                double rh = rect.Height / dpi.DpiScaleY;
                if (rw <= 0 || rh <= 0) continue;

                var occ = new RectangleGeometry(new Rect(left, top, rw, rh));
                visible = new CombinedGeometry(GeometryCombineMode.Exclude, visible, occ);
            }

            inst.Canvas.SetClip(visible);
        }

        /// <summary>
        /// 两组遮挡矩形是否等价（顺序敏感；<see cref="UILinkageWindowFinder.GetOccludedRects"/> 的
        /// Z 序遍历顺序在同一窗口布局下稳定，矩形值来自 <c>GetWindowRect</c> 的整数，可精确比较）。
        /// </summary>
        private static bool OccludedRectsEqual(List<Rect> a, List<Rect> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>用户拖动独立画布滚动条：把目标像素偏移换算为滚动百分比，下发到目标应用真实滚动。</summary>
        private void OnUILinkageCanvasUserScroll(UILinkageCanvasInstance inst, Orientation orientation, double value)
        {
            if (inst == null || !inst.Bound || inst.DocElement == null) return;

            try
            {
                var r = GetUILinkageViewportRect(inst);
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return;

                double vp, hp, vs, hs;
                if (inst.Kind == UILinkageTargetKind.SumatraPdf)
                {
                    GetUILinkageSumatraEffUnits(inst, r, out var effV, out var effH);
                    if (!UILinkageScrollReader.TryGetSumatraScrollPercents(
                            inst.CanvasHwnd, effV, effH, out vp, out hp, out vs, out hs, out _))
                        return;
                }
                else
                {
                    if (!ReadWordScroll(inst.DocElement, out vp, out hp, out vs, out hs))
                        return;
                }

                var dpi = VisualTreeHelper.GetDpi(this);
                double viewW = r.Width / dpi.DpiScaleX;
                double viewH = r.Height / dpi.DpiScaleY;

                double extW = viewW * (hs > 0 && hs < 100 ? 100.0 / hs : 1.0);
                double extH = viewH * (vs > 0 && vs < 100 ? 100.0 / vs : 1.0);

                double hpTarget = ScrollPattern.NoScroll;
                double vpTarget = ScrollPattern.NoScroll;
                if (orientation == Orientation.Vertical)
                {
                    double scrollable = extH - viewH;
                    // 算不出可滚动范围就不下发该轴（NoScroll）：下发 0 会把目标应用拉到最顶。
                    if (scrollable > 0)
                        vpTarget = Math.Min(100.0, Math.Max(0.0, value / scrollable * 100.0));
                }
                else
                {
                    // Word 的横向范围要用文本区水平滚动条读数（ScrollPattern 横向不可靠，会把范围算成 0）。
                    if (TryResolveUILinkageHorizontalTarget(
                            inst, r, dpi, viewW, extW, hp, out double extWTarget, out _))
                    {
                        double scrollable = extWTarget - viewW;
                        if (scrollable > 0)
                            hpTarget = Math.Min(100.0, Math.Max(0.0, value / scrollable * 100.0));
                    }
                }

                ApplyUILinkageScrollPercent(inst, orientation, hpTarget, vpTarget, r);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动滚动条控制失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 滚动辅助按钮点击：按增量（DIP）滚动目标应用。
        /// </summary>
        private void OnUILinkageCanvasScrollStep(UILinkageCanvasInstance inst, Orientation orientation, double delta)
        {
            if (inst == null || !inst.Bound || inst.DocElement == null) return;

            try
            {
                var r = GetUILinkageViewportRect(inst);
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return;

                double vp, hp, vs, hs;
                if (inst.Kind == UILinkageTargetKind.SumatraPdf)
                {
                    GetUILinkageSumatraEffUnits(inst, r, out var effV, out var effH);
                    if (!UILinkageScrollReader.TryGetSumatraScrollPercents(
                            inst.CanvasHwnd, effV, effH, out vp, out hp, out vs, out hs, out _))
                        return;
                }
                else
                {
                    if (!ReadWordScroll(inst.DocElement, out vp, out hp, out vs, out hs))
                        return;
                }

                var dpi = VisualTreeHelper.GetDpi(this);
                double viewW = r.Width / dpi.DpiScaleX;
                double viewH = r.Height / dpi.DpiScaleY;

                double hpTarget = ScrollPattern.NoScroll;
                double vpTarget = ScrollPattern.NoScroll;
                if (orientation == Orientation.Vertical)
                {
                    if (double.IsNaN(vp)) return;
                    double extH = viewH * (vs > 0 && vs < 100 ? 100.0 / vs : 1.0);
                    double scrollable = Math.Max(0, extH - viewH);
                    if (scrollable <= 0) return;
                    double value = vp / 100.0 * scrollable + delta;
                    vpTarget = Math.Min(100.0, Math.Max(0.0, value / scrollable * 100.0));
                }
                else
                {
                    // 横向：Word 的位置与范围都取文本区水平滚动条读数（ScrollPattern 横向不可靠）。
                    // 取不到可信口径就直接 return —— 绝不能回写 0，那会把 Word 横向拉回最左。
                    if (!TryResolveUILinkageHorizontalTarget(
                            inst, r, dpi, viewW, viewW * (hs > 0 && hs < 100 ? 100.0 / hs : 1.0), hp,
                            out double extW, out double hpCurrent))
                        return;

                    double scrollable = Math.Max(0, extW - viewW);
                    if (scrollable <= 0) return;
                    double value = hpCurrent / 100.0 * scrollable + delta;
                    hpTarget = Math.Min(100.0, Math.Max(0.0, value / scrollable * 100.0));
                }

                ApplyUILinkageScrollPercent(inst, orientation, hpTarget, vpTarget, r);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动滚动辅助失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 解析「回写目标应用」用的水平轴口径：内容总宽（DIP）与当前横向百分比。
        /// <para>
        /// Word <b>只认文本区（_WwG）水平滚动条读数</b>：其 UIA ScrollPattern 的横向范围/百分比
        /// 不可靠（文档宽于视口时也常报 <c>HorizontalViewSize=100</c>、<c>HorizontalScrollPercent=0</c>），
        /// 采信它就会算出 <c>scrollable = 0</c> 并把兜底的 <b>0</b> 回写进 Word ——
        /// 0 表示「横向滚到最左」，正是「外部滚动条回到首位」的成因。因此取不到滚动条读数时
        /// 直接返回 false，调用方放弃该轴（传 <see cref="ScrollPattern.NoScroll"/>）而不是退化成 0。
        /// </para>
        /// SumatraPDF 用调用方算好的百分比（其值由 Win32 滚动条换算，可靠）。
        /// </summary>
        private bool TryResolveUILinkageHorizontalTarget(
            UILinkageCanvasInstance inst, Rect viewportRect, DpiScale dpi, double viewW,
            double fallbackExtW, double fallbackPercent,
            out double extW, out double percent)
        {
            extW = fallbackExtW > viewW ? fallbackExtW : viewW;
            percent = fallbackPercent;

            if (inst?.Kind != UILinkageTargetKind.Word)
                return IsUILinkageValidPercent(percent);

            if (!TryReadUILinkageWordHorizontal(
                    inst, viewportRect, dpi, viewW, out _, out double barExtW, out double barPercent,
                    calibrateFrame: false))
                return false;

            extW = Math.Max(viewW, barExtW);
            percent = barPercent;
            return true;
        }

        /// <summary>
        /// 把目标滚动百分比下发到目标应用：Word 调 ScrollPattern；SumatraPDF 发 Win32 滚动消息
        /// （内部为读数反馈闭环，通常一两个消息内收敛；调用方在 UI 线程，与 Word 的跨进程
        /// ScrollPattern 调用阻塞特性一致）。未作用的轴传 NoScroll（Word）/null（SumatraPDF）。
        /// </summary>
        private static void ApplyUILinkageScrollPercent(
            UILinkageCanvasInstance inst, Orientation orientation, double hp, double vp, Rect viewportRect = default)
        {
            if (inst == null || !inst.Bound) return;

            if (inst.Kind == UILinkageTargetKind.SumatraPdf)
            {
                if (inst.CanvasHwnd == IntPtr.Zero) return;
                GetUILinkageSumatraEffUnits(inst, viewportRect, out var effV, out var effH);
                double? v = orientation == Orientation.Vertical
                    ? Math.Min(100.0, Math.Max(0.0, vp))
                    : (double?)null;
                double? h = orientation == Orientation.Horizontal
                    ? Math.Min(100.0, Math.Max(0.0, hp))
                    : (double?)null;
                UILinkageScrollReader.TrySetSumatraScrollPercent(inst.CanvasHwnd, v, h, effV, effH);
                return;
            }

            if (inst.DocElement == null) return;
            if (inst.DocElement.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && p is ScrollPattern sp)
                sp.SetScrollPercent(hp, vp);
        }

        // ---------------- 滚动位置记忆（按文档，json sidecar） ----------------

        /// <summary>
        /// 本实例的持久化键后缀（文档内的窗口槽位）。
        /// <para>
        /// <b>必须是跨会话稳定的值。</b>历史实现用绑定窗口句柄（<c>_h{hwnd:x}</c>）做后缀：
        /// 它确实能区分同会话内的多个窗口，但 HWND 每次进程启动都由系统重新分配，
        /// 于是下一次运行算出的键与上一次不同 —— 载入时找不到文件，而上一次保存又把
        /// 无后缀的旧版文件删掉了，<b>批注就此永久失联（磁盘上留孤儿文件）</b>。
        /// 反向还会串档：HWND 被复用给另一个 Word 窗口且文档 sanitize 后同名时，
        /// 会把别档的批注载进来。
        /// </para>
        /// <para>
        /// 现在的键 = 文档名（sanitize 后）+ 该文档在本程序内的窗口序号。序号由
        /// <see cref="AssignUILinkageWindowSlot"/> 在绑定/解绑时按「该文档当前占用的最小空闲槽」
        /// 分配，因此：单窗口场景恒为槽 1、键完全稳定（跨会话可命中）；同文档多窗口时
        /// 依次占用槽 2、3…；关掉一个窗口后其槽位立即被回收，下次打开仍落到最小空闲槽，
        /// 使「最常见的那份文档」始终落在稳定的主键上。
        /// </para>
        /// </summary>
        private static string GetUILinkageWindowKeySuffix(UILinkageCanvasInstance inst)
        {
            var slot = inst?.WindowSlot ?? 0;
            if (slot <= 1) return string.Empty; // 槽 1 = 主键，不带后缀，与旧版文件名兼容
            return "_w" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 为实例分配文档内窗口槽位：取该文档名当前未被其它实例占用的最小正整数槽。
        /// 在实例创建时调用一次；解绑时把 <see cref="UILinkageCanvasInstance.WindowSlot"/>
        /// 置 0 即视为释放（本方法只看仍绑定且槽位非 0 的实例）。
        /// </summary>
        private void AssignUILinkageWindowSlot(UILinkageCanvasInstance inst)
        {
            if (inst == null) return;

            var docKey = SanitizeFileName(inst.DocName ?? string.Empty);
            var used = new HashSet<int>();
            foreach (var other in _uiLinkageInstances)
            {
                if (other == null || ReferenceEquals(other, inst)) continue;
                if (other.WindowSlot <= 0) continue;
                if (!string.Equals(SanitizeFileName(other.DocName ?? string.Empty), docKey, StringComparison.Ordinal))
                    continue;
                used.Add(other.WindowSlot);
            }

            var slot = 1;
            while (used.Contains(slot)) slot++;
            inst.WindowSlot = slot;
        }

        /// <summary>
        /// 回收孤儿持久化文件：本程序早期版本用 HWND 做后缀（<c>_h{hwnd:x}</c>），
        /// 那些文件在新键下永远不会被命中。当规范路径不存在时，按文档名回退到一个
        /// <c>_h*.isf</c> / <c>_h*.state.json</c> 已存在文件，
        /// 让「刚好用过那个缺陷版本」的用户也能找回批注，而不是面对一片空白。
        /// 只读取、不删除，避免误伤另一个同名文档的孤儿文件。
        /// </summary>
        private static string TryFindOrphanedHwndKeyedPath(string docName, string extensionSuffix)
        {
            try
            {
                var dir = Path.Combine(App.RootPath, "UILinkageInk");
                if (!Directory.Exists(dir)) return null;

                var stem = SanitizeFileName(docName) + "_h";
                var matches = Directory.GetFiles(dir, stem + "*" + extensionSuffix);
                if (matches.Length == 0) return null;

                // 多个孤儿文件时取最近写入的那个（最可能是用户最后使用的那份）。
                var best = matches[0];
                var bestTime = File.GetLastWriteTimeUtc(best);
                for (var i = 1; i < matches.Length; i++)
                {
                    var t = File.GetLastWriteTimeUtc(matches[i]);
                    if (t > bestTime)
                    {
                        best = matches[i];
                        bestTime = t;
                    }
                }

                LogHelper.WriteLogToFile(
                    $"UI联动：命中旧版 HWND 键的孤儿墨迹文件，已改为读取 {Path.GetFileName(best)}（下次保存将迁移到稳定键）",
                    LogHelper.LogType.Info);
                return best;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动孤儿文件查找失败: {ex.Message}", LogHelper.LogType.Warning);
                return null;
            }
        }

        private static string GetUILinkageStatePath(string docName, string windowKeySuffix = "")
        {
            return Path.Combine(App.RootPath, "UILinkageInk",
                SanitizeFileName(docName) + windowKeySuffix + ".state.json");
        }

        /// <summary>
        /// 旧版（不带窗口后缀）的状态文件路径。读取时作为兜底，保证升级后既有记忆不丢。
        /// </summary>
        private static string GetUILinkageLegacyStatePath(string docName)
            => GetUILinkageStatePath(docName);

        /// <summary>
        /// 记忆当前状态：<paramref name="inst"/> 上进行过墨迹书写（Strokes 非空）时，
        /// 把最近一次同步到的滚动百分比与缩放百分比写入文档名对应的 sidecar json。
        /// 滚动百分比供滚动位置恢复；缩放百分比供载入历史墨迹时按比例重缩放。
        /// </summary>
        private void SaveUILinkageScrollPosition(UILinkageCanvasInstance inst)
        {
            try
            {
                var s = SettingsManager.Settings?.UILinkage;
                if (s == null) return;
                // 状态文件兼作墨迹对齐元数据（缩放记忆）：滚动记忆或墨迹自动保存任一开启即写入。
                if (!s.RememberScrollPosition && !s.AutoSaveInk) return;
                if (inst?.Canvas == null || string.IsNullOrWhiteSpace(inst.DocName)) return;
                if (inst.Canvas.Strokes.Count == 0) return;
                if (double.IsNaN(inst.LastVpPercent)) return;

                var state = new UILinkageScrollState
                {
                    VerticalPercent = inst.LastVpPercent,
                    // 水平读数未知时保持 NaN（序列化为 NaN，读回来仍是 NaN）——绝不写成 0：
                    // 0 在恢复时表示「滚到最左」，会把 Word 横向拉回首位（历史缺陷的成因之一）。
                    HorizontalPercent = inst.LastHpPercent,
                    // 只有来自可信横向读数（Word 文本区水平滚动条）的百分比才允许用于恢复；
                    // 旧版文件没有这个标记，其水平值来自当时不可靠的 ScrollPattern 横向读数，一律不恢复。
                    HorizontalPercentTrusted = IsUILinkageValidPercent(inst.LastHpPercent),
                    ZoomPercent = inst.LastZoomPercent,
                    UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
                var path = GetUILinkageStatePath(inst.DocName, GetUILinkageWindowKeySuffix(inst));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(state));
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动滚动位置记忆失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>恢复上次滚动位置（sidecar json 存在时，后台滚动到记忆位置）。</summary>
        private void RestoreUILinkageScrollPosition(UILinkageCanvasInstance inst)
        {
            try
            {
                var s = SettingsManager.Settings?.UILinkage;
                if (s == null || !s.RememberScrollPosition) return;
                if (inst?.DocElement == null || string.IsNullOrWhiteSpace(inst.DocName)) return;

                var path = GetUILinkageStatePath(inst.DocName, GetUILinkageWindowKeySuffix(inst));
                // 兜底顺序同 LoadUILinkageInk：稳定键 → 旧版无后缀 → 旧版 HWND 键孤儿文件。
                if (!File.Exists(path))
                    path = GetUILinkageLegacyStatePath(inst.DocName);
                if (!File.Exists(path))
                    path = TryFindOrphanedHwndKeyedPath(inst.DocName, ".state.json");
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                var state = JsonConvert.DeserializeObject<UILinkageScrollState>(File.ReadAllText(path));
                if (state == null || double.IsNaN(state.VerticalPercent)) return;

                double vp = Math.Min(100.0, Math.Max(0.0, state.VerticalPercent));
                // 横向：只有「带可信标记 + 百分比合法」才恢复，否则该轴不动。
                // 这里绝不能退化成 0 —— 0 表示「滚到最左」，会把目标应用横向拉回首位
                //（历史实现把「读不到」写成 0，恢复时正表现为外部滚动条回到首位）。
                double? hp = state.HorizontalPercentTrusted && IsUILinkageValidPercent(state.HorizontalPercent)
                    ? Math.Min(100.0, Math.Max(0.0, state.HorizontalPercent))
                    : (double?)null;

                if (inst.Kind == UILinkageTargetKind.SumatraPdf)
                {
                    // SumatraPDF：Win32 滚动消息反馈闭环，后台重试至到位。
                    var canvasHwnd = inst.CanvasHwnd;
                    if (canvasHwnd == IntPtr.Zero) return;
                    GetUILinkageSumatraEffUnits(inst, GetUILinkageViewportRect(inst), out var effV, out var effH);
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            try
                            {
                                if (UILinkageScrollReader.TrySetSumatraScrollPercent(canvasHwnd, vp, hp, effV, effH))
                                    return;
                            }
                            catch { }
                            await Task.Delay(250);
                        }
                    });
                    return;
                }

                var docEl = inst.DocElement;
                double hpWord = hp ?? ScrollPattern.NoScroll;

                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 8; i++)
                    {
                        try
                        {
                            if (docEl.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && p is ScrollPattern sp)
                            {
                                sp.SetScrollPercent(hpWord, vp);
                                return;
                            }
                        }
                        catch { }
                        await Task.Delay(250);
                    }
                });
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动滚动位置恢复失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        // ---------------- 自动长截屏（墨迹区域，滚动拼接） ----------------

        /// <summary>相邻截图切片的重叠比例（0.9 步距 → 10% 重叠，供拼接对齐）。</summary>
        private const double UILinkageCaptureStepRatio = 0.9;

        /// <summary>单次长截屏允许的最大切片数，防止超长文档失控。</summary>
        private const int UILinkageMaxCapturePositions = 24;

        /// <summary>
        /// 长截屏看门狗超时。正常一轮（≤24 切片）远小于该值；超过说明截图任务没能正常收尾
        /// （例如 <c>Task.Run</c> 未被调度、后台线程被杀），此时必须复位
        /// <c>IsCapturingLongScreenshot</c>，否则该实例的滚动同步会被永久禁用、
        /// 画布永远停在无边框模式。
        /// </summary>
        private static readonly TimeSpan UILinkageCaptureWatchdogTimeout = TimeSpan.FromSeconds(90);

        /// <summary>一段长截屏的滚动区间（内容偏移 DIP：视口顶起点 / 终点）。</summary>
        private sealed class UILinkageCaptureSnapshot
        {
            public UILinkageCanvasInstance Instance;
            public Ink_Canvas.Controls.IndependentCanvas Canvas;
            public UILinkageTargetKind Kind;
            public AutomationElement DocElement;
            public IntPtr CanvasHwnd;
            public double EffUnitsV;
            public double EffUnitsH;
            public string DocName;
            public Rect DocRect;
            public List<Tuple<double, double>> Segments = new List<Tuple<double, double>>();
            public Size View;
            public Size Ext;
            public Vector Offset;
            public double Scrollable = 1;
            public double OrigVp;
            public double OrigHp;
        }

        /// <summary>
        /// 对带墨迹的区域自动长截屏：从最顶一笔上延一块到最低一笔下延一块，
        /// 间隔超过一块时分多段；每段按步距滚动截屏并纵向拼接为一张长图。
        /// 保存路径与 PPT 联动自动截图一致，便于统一管理。
        /// 返回是否已启动；启动后画布保留在屏幕上，由完成回调负责还原与移除。
        /// </summary>
        private bool StartUILinkageLongScreenshotCapture(UILinkageCanvasInstance inst)
        {
            try
            {
                if (inst?.Canvas == null || inst.IsCapturingLongScreenshot) return false;
                var s = SettingsManager.Settings?.UILinkage;
                if (s == null || !s.AutoLongScreenshot) return false;
                if (IsInPPTPresentationMode) return false;
                if (inst.Canvas.Strokes.Count == 0) return false;
                if (double.IsNaN(inst.LastVpPercent) || inst.LastViewH <= 0 || inst.LastViewW <= 0) return false;
                if (inst.LastExtH <= inst.LastViewH) return false;
                if (!UILinkageWindowFinder.IsWindowAlive(inst.BoundHwnd)) return false;

                var docRect = GetUILinkageViewportRect(inst);
                if (docRect.IsEmpty || docRect.Width <= 0 || docRect.Height <= 0) return false;

                // 烘焙未应用的滚动位移，让墨迹数据坐标与视觉一致后再取外接框。
                inst.Canvas.FlushScrollAnimation();
                var bounds = inst.Canvas.Strokes.GetBounds();
                if (bounds.IsEmpty) return false;

                var segments = BuildUILinkageCaptureSegments(inst.Canvas.Strokes, inst.LastViewH, inst.LastOffsetY, inst.LastExtH);
                if (segments.Count == 0) return false;

                double stepDip = inst.LastViewH * UILinkageCaptureStepRatio;
                int totalPositions = 0;
                foreach (var seg in segments)
                    totalPositions += Math.Max(1, (int)Math.Ceiling((seg.Item2 - seg.Item1) / stepDip));
                if (totalPositions > UILinkageMaxCapturePositions)
                {
                    LogHelper.WriteLogToFile(
                        $"UI联动长截屏跳过：所需切片数 {totalPositions} 超过上限 {UILinkageMaxCapturePositions}",
                        LogHelper.LogType.Info);
                    return false;
                }

                inst.IsCapturingLongScreenshot = true;
                inst.CaptureStartedUtc = DateTime.UtcNow;
                inst.Canvas.BeginChromelessCapture();

                var snapshot = new UILinkageCaptureSnapshot
                {
                    Instance = inst,
                    Canvas = inst.Canvas,
                    Kind = inst.Kind,
                    DocElement = inst.DocElement,
                    CanvasHwnd = inst.CanvasHwnd,
                    EffUnitsV = inst.Kind == UILinkageTargetKind.SumatraPdf ? inst.SumatraUnitsPerPxV * docRect.Height : 0,
                    EffUnitsH = inst.Kind == UILinkageTargetKind.SumatraPdf ? inst.SumatraUnitsPerPxH * docRect.Width : 0,
                    DocName = inst.DocName,
                    DocRect = docRect,
                    Segments = segments,
                    View = new Size(inst.LastViewW, inst.LastViewH),
                    Ext = new Size(inst.LastExtW, inst.LastExtH),
                    Offset = new Vector(inst.LastOffsetX, inst.LastOffsetY),
                    Scrollable = Math.Max(1, inst.LastExtH - inst.LastViewH),
                    OrigVp = inst.LastVpPercent,
                    OrigHp = inst.LastHpPercent
                };

                _ = Task.Run(() => RunUILinkageLongScreenshotCapture(snapshot));
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动长截屏启动失败: {ex.Message}", LogHelper.LogType.Warning);
                ResetUILinkageCaptureState(inst);
                return false;
            }
        }

        /// <summary>
        /// 复位实例的长截屏状态：清除「进行中」标志与起始时刻，并还原画布的无边框截图外观。
        /// <para>
        /// 长截屏有三处收尾（正常完成、启动异常、看门狗超时），必须共用同一段复位逻辑：
        /// 任一分支漏掉 <see cref="Ink_Canvas.Controls.IndependentCanvas.EndChromelessCapture"/>
        /// 会让该画布永久停在无边框模式，漏掉标志则会让滚动同步对被永久禁用。
        /// 幂等，可重复调用。
        /// </para>
        /// </summary>
        private static void ResetUILinkageCaptureState(UILinkageCanvasInstance inst)
        {
            if (inst == null) return;

            inst.IsCapturingLongScreenshot = false;
            inst.CaptureStartedUtc = DateTime.MinValue;

            try
            {
                inst.Canvas?.EndChromelessCapture();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动长截屏状态复位失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 把墨迹按内容坐标聚类分段：相邻墨迹簇间隔 ≥ 一块（视口高）则分段，
        /// 每段上下各延伸一块，并夹取到可滚动范围内。返回视口顶偏移区间（DIP）。
        /// </summary>
        private static List<Tuple<double, double>> BuildUILinkageCaptureSegments(
            StrokeCollection strokes, double viewH, double offsetY, double extH)
        {
            var ranges = new List<Tuple<double, double>>();
            foreach (Stroke stroke in strokes)
            {
                var b = stroke.GetBounds();
                if (b.IsEmpty || (b.Width <= 0 && b.Height <= 0)) continue;
                ranges.Add(Tuple.Create(b.Top + offsetY, b.Bottom + offsetY));
            }
            if (ranges.Count == 0) return new List<Tuple<double, double>>();

            ranges.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            var clusters = new List<Tuple<double, double>>();
            double curTop = ranges[0].Item1, curBottom = ranges[0].Item2;
            for (int i = 1; i < ranges.Count; i++)
            {
                var r = ranges[i];
                if (r.Item1 - curBottom >= viewH)
                {
                    clusters.Add(Tuple.Create(curTop, curBottom));
                    curTop = r.Item1;
                    curBottom = r.Item2;
                }
                else
                {
                    curBottom = Math.Max(curBottom, r.Item2);
                }
            }
            clusters.Add(Tuple.Create(curTop, curBottom));

            double maxOffset = Math.Max(0, extH - viewH);
            var segments = new List<Tuple<double, double>>();
            foreach (var c in clusters)
            {
                // 上下各延伸一块（一块 = 视口高）：
                // 起始视口顶 = 簇顶上延一块；终止视口顶 = 簇底（此时视口恰好覆盖到簇底下延一块）。
                double top = Math.Max(0, Math.Min(maxOffset, c.Item1 - viewH));
                double bottom = Math.Max(0, Math.Min(maxOffset, c.Item2));
                segments.Add(Tuple.Create(top, Math.Max(top, bottom)));
            }
            return segments;
        }

        /// <summary>长截屏后台执行：滚动 → 等待稳定 → 截图 → 拼接 → 保存；完成后还原滚动与界面。</summary>
        private async void RunUILinkageLongScreenshotCapture(UILinkageCaptureSnapshot snap)
        {
            var savedPaths = new List<string>();
            try
            {
                double viewH = snap.View.Height;
                double stepDip = viewH * UILinkageCaptureStepRatio;
                double dpiScaleY = snap.DocRect.Height / Math.Max(1, viewH);
                int sliceH = (int)Math.Round(snap.DocRect.Height);
                int sliceW = (int)Math.Round(snap.DocRect.Width);
                int imageIndex = 1;

                foreach (var seg in snap.Segments)
                {
                    var slices = new List<System.Drawing.Bitmap>();
                    try
                    {
                        var positions = new List<double>();
                        for (double o = seg.Item1; o < seg.Item2 - 0.5; o += stepDip)
                            positions.Add(o);
                        if (positions.Count == 0) positions.Add(seg.Item1);
                        if (positions[positions.Count - 1] + viewH < seg.Item2 - 0.5)
                            positions.Add(seg.Item2 - viewH);

                        foreach (var o in positions)
                        {
                            double targetVp = Math.Min(100.0, Math.Max(0.0, o / snap.Scrollable * 100.0));

                            // 先把墨迹同步到目标偏移（与文档滚动一致），再滚动 Word。
                            var canvas = snap.Canvas;
                            var view = snap.View;
                            var ext = snap.Ext;
                            var offX = snap.Offset.X;
                            await Dispatcher.InvokeAsync(() =>
                                canvas?.SetScrollInfo(view, ext, new Vector(offX, o), animate: false));

                            if (!await ScrollUILinkageTargetUntilAsync(snap, targetVp))
                                break;

                            await Task.Delay(140);
                            var bmp = CaptureRectBitmap(snap.DocRect, sliceW, sliceH);
                            if (bmp != null) slices.Add(bmp);
                        }

                        if (slices.Count == 0) continue;

                        using var tall = StitchUILinkageSlices(slices, viewH, dpiScaleY);
                        if (tall == null) continue;

                        var path = GetUILinkageLongScreenshotPath(snap.DocName, imageIndex++);
                        SaveUILinkageScreenshot(tall, path);
                        savedPaths.Add(path);
                    }
                    finally
                    {
                        foreach (var bmp in slices) bmp.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动长截屏执行失败: {ex.Message}", LogHelper.LogType.Warning);
            }
            finally
            {
                try
                {
                    // 还原墨迹位置 → 还原 Word 滚动 → 还原界面控件。
                    var canvas = snap.Canvas;
                    var view = snap.View;
                    var ext = snap.Ext;
                    var offset = snap.Offset;
                    await Dispatcher.InvokeAsync(() =>
                        canvas?.SetScrollInfo(view, ext, offset, animate: false));

                    await RestoreUILinkageTargetScrollAsync(snap);

                    bool shouldRemove = false;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        ResetUILinkageCaptureState(snap.Instance);
                        // 实例已解绑（解绑时为截图保留画布）→ 截图完成后移除控件。
                        shouldRemove = !_uiLinkageInstances.Contains(snap.Instance);
                        if (shouldRemove && canvas != null)
                        {
                            canvas.Visibility = Visibility.Collapsed;
                            InkCanvasGridForInkReplay?.Children.Remove(canvas);
                        }
                    });

                    if (savedPaths.Count > 0)
                        LogHelper.WriteLogToFile(
                            $"UI联动长截屏完成 {savedPaths.Count} 张（{snap.DocName}）", LogHelper.LogType.Info);
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"UI联动长截屏收尾失败: {ex.Message}", LogHelper.LogType.Warning);
                }
            }
        }

        /// <summary>对所有已绑定实例尝试长截屏（退出批注、关闭联动时调用）。</summary>
        internal void CaptureAllUILinkageLongScreenshots()
        {
            foreach (var inst in _uiLinkageInstances.ToList())
                StartUILinkageLongScreenshotCapture(inst);
        }

        /// <summary>
        /// 滚动目标应用直到竖直滚动百分比到位：SumatraPDF 走 Win32 滚动消息反馈闭环，
        /// Word 轮询 ScrollPattern。
        /// </summary>
        private static async Task<bool> ScrollUILinkageTargetUntilAsync(UILinkageCaptureSnapshot snap, double targetVp)
        {
            if (snap == null) return false;

            if (snap.Kind == UILinkageTargetKind.SumatraPdf)
            {
                if (snap.CanvasHwnd == IntPtr.Zero) return false;
                // TrySetSumatraScrollPercent 内部自带读数反馈闭环，直接滚动到目标百分比。
                double effV = snap.EffUnitsV;
                return await Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 1800)
                    {
                        if (UILinkageScrollReader.TrySetSumatraScrollPercent(snap.CanvasHwnd, targetVp, null, effV, 0))
                            return true;
                        System.Threading.Thread.Sleep(60);
                    }
                    return false;
                });
            }

            return await ScrollWordUntilAsync(snap.DocElement, targetVp);
        }

        /// <summary>还原目标应用的滚动位置（长截屏收尾）：SumatraPDF 走 Win32 滚动消息，Word 走 ScrollPattern。</summary>
        private static async Task RestoreUILinkageTargetScrollAsync(UILinkageCaptureSnapshot snap)
        {
            if (snap == null) return;

            if (snap.Kind == UILinkageTargetKind.SumatraPdf)
            {
                if (snap.CanvasHwnd == IntPtr.Zero) return;
                double? vp = double.IsNaN(snap.OrigVp) ? (double?)null : Math.Min(100.0, Math.Max(0.0, snap.OrigVp));
                double? hp = double.IsNaN(snap.OrigHp) ? (double?)null : Math.Min(100.0, Math.Max(0.0, snap.OrigHp));
                double effV = snap.EffUnitsV;
                double effH = snap.EffUnitsH;
                await Task.Run(() => UILinkageScrollReader.TrySetSumatraScrollPercent(snap.CanvasHwnd, vp, hp, effV, effH));
                return;
            }

            await RestoreWordScrollAsync(snap.DocElement, snap.OrigVp, snap.OrigHp);
        }

        private static async Task<bool> ScrollWordUntilAsync(AutomationElement docEl, double targetVp)
        {
            if (docEl == null) return false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1800)
            {
                try
                {
                    if (docEl.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && p is ScrollPattern sp)
                    {
                        double cur = sp.Current.VerticalScrollPercent;
                        if (!double.IsNaN(cur) && Math.Abs(cur - targetVp) <= 0.4)
                            return true;
                        sp.SetScrollPercent(ScrollPattern.NoScroll, targetVp);
                    }
                    else
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
                await Task.Delay(70);
            }
            return false;
        }

        private static async Task RestoreWordScrollAsync(AutomationElement docEl, double vp, double hp)
        {
            if (docEl == null) return;
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    if (docEl.TryGetCurrentPattern(ScrollPattern.Pattern, out var p) && p is ScrollPattern sp)
                    {
                        sp.SetScrollPercent(
                            double.IsNaN(hp) ? ScrollPattern.NoScroll : hp,
                            double.IsNaN(vp) ? ScrollPattern.NoScroll : vp);
                        return;
                    }
                }
                catch { }
                await Task.Delay(150);
            }
        }

        private static System.Drawing.Bitmap CaptureRectBitmap(System.Windows.Rect physicalRect, int width, int height)
        {
            try
            {
                var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(
                        (int)physicalRect.Left, (int)physicalRect.Top, 0, 0,
                        new System.Drawing.Size(width, height),
                        System.Drawing.CopyPixelOperation.SourceCopy);
                }
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把按 0.9 步距重叠的切片纵向拼接为一张长图（重叠区灰度匹配对齐，失败时按期望重叠裁切）。</summary>
        private static System.Drawing.Bitmap StitchUILinkageSlices(
            List<System.Drawing.Bitmap> slices, double viewHDip, double dpiScaleY)
        {
            try
            {
                if (slices == null || slices.Count == 0) return null;
                if (slices.Count == 1)
                {
                    return new System.Drawing.Bitmap(slices[0]);
                }

                int sliceH = slices[0].Height;
                int sliceW = slices[0].Width;
                int stepPx = (int)Math.Round(viewHDip * dpiScaleY * UILinkageCaptureStepRatio);
                int expectedTrim = Math.Min(sliceH - 1, Math.Max(1, sliceH - stepPx));

                int totalHeight = sliceH;
                var trims = new int[slices.Count - 1];
                for (int i = 1; i < slices.Count; i++)
                {
                    int trim = FindSliceOverlapTrim(slices[i - 1], slices[i], expectedTrim, 40);
                    trims[i - 1] = trim;
                    totalHeight += sliceH - trim;
                }

                var result = new System.Drawing.Bitmap(sliceW, totalHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(result))
                {
                    int y = 0;
                    g.DrawImage(slices[0], 0, 0);
                    y += sliceH;
                    for (int i = 1; i < slices.Count; i++)
                    {
                        y -= trims[i - 1];
                        g.DrawImage(slices[i], 0, y);
                        y += sliceH;
                    }
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>在期望裁切行附近按灰度 SAD 搜索相邻切片的最佳重叠行数。</summary>
        private static int FindSliceOverlapTrim(System.Drawing.Bitmap prev, System.Drawing.Bitmap next, int expectedTrim, int searchRange)
        {
            try
            {
                int h = prev.Height, w = prev.Width;
                int trimMin = Math.Max(1, expectedTrim - searchRange);
                int trimMax = Math.Min(h - 1, expectedTrim + searchRange);
                if (trimMax <= trimMin) return expectedTrim;

                var prevGray = ToGrayStrip(prev, h - trimMax, trimMax);
                var nextGray = ToGrayStrip(next, 0, trimMax);
                int sw = prevGray.GetLength(1);

                int best = expectedTrim;
                long bestScore = long.MaxValue;
                for (int t = trimMin; t <= trimMax; t++)
                {
                    int prevStart = trimMax - t; // prev 底部 t 行（条带内起始行）
                    long score = 0;
                    for (int y = 0; y < t; y += 2)
                    {
                        for (int x = 0; x < sw; x++)
                        {
                            int d = prevGray[prevStart + y, x] - nextGray[y, x];
                            score += d < 0 ? -d : d;
                        }
                    }
                    long rows = (t / 2 + 1) * (long)sw;
                    score = score * 1000 / Math.Max(1, rows);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = t;
                    }
                }
                return best;
            }
            catch
            {
                return expectedTrim;
            }
        }

        private static byte[,] ToGrayStrip(System.Drawing.Bitmap bmp, int startRow, int rowCount)
        {
            int w = bmp.Width;
            const int XStep = 8;
            int cols = Math.Max(1, w / XStep);
            var gray = new byte[rowCount, cols];
            var rect = new System.Drawing.Rectangle(0, startRow, w, rowCount);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                var rowBytes = new byte[stride];
                for (int y = 0; y < rowCount; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        new IntPtr(data.Scan0.ToInt64() + y * stride), rowBytes, 0, stride);
                    for (int c = 0; c < cols; c++)
                    {
                        int x = c * XStep * 3;
                        if (x + 2 < rowBytes.Length)
                            gray[y, c] = (byte)((rowBytes[x] + rowBytes[x + 1] + rowBytes[x + 2]) / 3);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return gray;
        }

        /// <summary>长截屏保存路径：与 PPT 联动自动截图一致（日期文件夹/自定义目录）。</summary>
        private string GetUILinkageLongScreenshotPath(string docName, int index)
        {
            var stem = $"{SanitizeFileName(docName)} 长截图 {DateTime.Now:HH-mm-ss} {index:00}";
            if (Settings.Automation.IsSaveScreenshotsInDateFolders)
                return GetDateFolderPath(stem);
            return Path.Combine(GetScreenshotSaveDirectory(), stem + Helpers.ScreenshotImageSaveHelper.GetExtension());
        }

        private static void SaveUILinkageScreenshot(System.Drawing.Bitmap bitmap, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            Helpers.ScreenshotImageSaveHelper.Save(bitmap, path);
            LogHelper.WriteLogToFile($"UI联动长截屏已保存: {path}", LogHelper.LogType.Info);
            _ = Task.Run(async () =>
            {
                try { await Helpers.UploadHelper.UploadFileAsync(path); }
                catch { }
            });
        }

        /// <summary>
        /// 从目标窗口标题提取文档名（去掉应用后缀）。Word 与 SumatraPDF 的文件名都在
        /// 顶层窗口标题上（形如「文档.pdf - SumatraPDF」）。读取失败返回 false。
        /// </summary>
        private static bool TryComputeUILinkageDocName(AutomationElement windowEl, out string docName)
        {
            docName = null;
            try
            {
                var name = windowEl?.Current.Name;
                if (string.IsNullOrWhiteSpace(name)) return false;

                foreach (var suffix in new[] { " - Word", " - Microsoft Word", "- Word", " - SumatraPDF", " - Sumatra PDF" })
                {
                    if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        name = name.Substring(0, name.Length - suffix.Length).Trim();
                        break;
                    }
                }
                docName = name;
                return !string.IsNullOrWhiteSpace(docName);
            }
            catch
            {
                return false;
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static string GetUILinkageInkPath(string docName, string windowKeySuffix = "")
        {
            return Path.Combine(App.RootPath, "UILinkageInk",
                SanitizeFileName(docName) + windowKeySuffix + ".isf");
        }

        /// <summary>旧版（不带窗口后缀）的墨迹文件路径，读取时作为升级兜底。</summary>
        private static string GetUILinkageLegacyInkPath(string docName)
            => GetUILinkageInkPath(docName);

        private void SaveUILinkageInk(UILinkageCanvasInstance inst)
        {
            if (inst?.Canvas == null) return;
            if (string.IsNullOrWhiteSpace(inst.DocName)) return;

            // 自动保存墨迹开关（与 PPT 联动的自动保存行为对齐）。
            var s = SettingsManager.Settings?.UILinkage;
            if (s == null || !s.AutoSaveInk) return;

            try
            {
                // 先烘焙未应用的滚动位移，保证保存的坐标与当前视觉一致。
                inst.Canvas.FlushScrollAnimation();

                // 墨迹按「内容锚定」保存：克隆笔迹（不动画布内正在使用的墨迹，解绑截图
                // 仍需原坐标渲染）并平移 +当前偏移，使保存数据以文档内容左上角为原点。
                // 载入后无论 Word 当前滚动到哪里，首轮滚动同步即可对齐，不受保存时
                // 滚动位置影响；配合 state json 的缩放记忆可在缩放比例变化后重缩放。
                var contentAnchored = inst.Canvas.Strokes.Clone();
                var offset = inst.Canvas.AppliedScrollOffset;
                var bake = new Matrix(1, 0, 0, 1, offset.X, offset.Y);
                foreach (Stroke stroke in contentAnchored)
                    stroke.Transform(bake, false);

                var path = GetUILinkageInkPath(inst.DocName, GetUILinkageWindowKeySuffix(inst));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    contentAnchored.Save(fs);

                // 注意：这里刻意【不删除】旧版无后缀 / 旧版 HWND 键的文件。
                // 历史实现对旧版无后缀文件做了「迁移后删除」，配合 HWND 后缀就变成了数据丢失：
                // HWND 每次进程启动都变，下次运行既命中不了新键、旧文件又已被删掉。
                // 现在键是稳定的，旧文件只是被取代，留着它既能让升级用户首次命中、
                // 也能在键规则再次演进时继续兜底，代价只是磁盘上一个不再增长的旧文件。
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动墨迹保存失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 周期性保存所有已绑定独立画布的墨迹（由主画布的自动保存定时器驱动）。
        /// 独立画布墨迹不在 <c>inkCanvas.Strokes</c> 里，主画布的自动保存覆盖不到它；
        /// 不落盘的话，崩溃/被强杀时自上次解绑以来的批注会全部丢失。
        /// 是否真正写盘仍由 <see cref="UILinkageSettings.AutoSaveInk"/> 决定。
        /// </summary>
        internal void SaveAllUILinkageInk()
        {
            if (_uiLinkageInstances.Count == 0) return;

            var s = SettingsManager.Settings?.UILinkage;
            if (s == null || !s.AutoSaveInk) return;

            foreach (var inst in _uiLinkageInstances.ToList())
            {
                if (inst?.Canvas == null || !inst.Bound) continue;
                if (inst.IsCapturingLongScreenshot) continue; // 截图期间坐标正被程序性驱动
                if (inst.Canvas.Strokes.Count == 0) continue;
                SaveUILinkageInk(inst);
            }
        }

        private void LoadUILinkageInk(UILinkageCanvasInstance inst)
        {
            if (inst?.Canvas == null) return;
            if (string.IsNullOrWhiteSpace(inst.DocName)) return;

            // 重置载入元数据（换文档重载时旧元数据失效）。
            inst.LoadedInkStrokes = null;
            inst.SavedZoomPercent = double.NaN;

            // 自动保存墨迹开关（关闭时不加载历史墨迹）。
            var s = SettingsManager.Settings?.UILinkage;
            if (s == null || !s.AutoSaveInk) return;

            try
            {
                var path = GetUILinkageInkPath(inst.DocName, GetUILinkageWindowKeySuffix(inst));
                // 升级/恢复兜底（按优先级）：
                //   1) 稳定键（文档名 + 窗口槽位）—— 正常路径；
                //   2) 旧版无后缀（仅文档名）—— 老版本写下的文件；
                //   3) 旧版 HWND 键（_h*）—— 短暂存在过的缺陷版本留下的孤儿文件。
                // 三步都只读，不删；下一次保存会写到稳定键上。
                if (!File.Exists(path))
                    path = GetUILinkageLegacyInkPath(inst.DocName);
                if (!File.Exists(path))
                    path = TryFindOrphanedHwndKeyedPath(inst.DocName, ".isf");
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    var loaded = new StrokeCollection(fs);

                    // 记录载入笔迹与保存时缩放：首次读到当前缩放时按比例重缩放这批墨迹，
                    // 保证两次批注之间 Word 缩放变化过的情况下历史墨迹仍对齐文档内容。
                    inst.LoadedInkStrokes = loaded.ToList();
                    inst.SavedZoomPercent = ReadUILinkageSavedZoomPercent(inst);

                    inst.Canvas.Strokes.Add(loaded);
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动墨迹加载失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>读取本实例记忆的保存时缩放百分比（state json）。缺失/无效返回 NaN。</summary>
        private static double ReadUILinkageSavedZoomPercent(UILinkageCanvasInstance inst)
        {
            try
            {
                if (inst == null || string.IsNullOrWhiteSpace(inst.DocName)) return double.NaN;

                var path = GetUILinkageStatePath(inst.DocName, GetUILinkageWindowKeySuffix(inst));
                // 兜底顺序同 LoadUILinkageInk：稳定键 → 旧版无后缀 → 旧版 HWND 键孤儿文件。
                if (!File.Exists(path))
                    path = GetUILinkageLegacyStatePath(inst.DocName);
                if (!File.Exists(path))
                    path = TryFindOrphanedHwndKeyedPath(inst.DocName, ".state.json");
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return double.NaN;

                var state = JsonConvert.DeserializeObject<UILinkageScrollState>(File.ReadAllText(path));
                return state?.ZoomPercent ?? double.NaN;
            }
            catch
            {
                return double.NaN;
            }
        }

        #endregion

        // ---------------- 独立画布与橡皮擦/工具的衔接 ----------------

        internal bool IsUILinkageCanvasBound => _uiLinkageInstances.Any(i => i.Bound);

        /// <summary>橡皮擦当前作用的独立画布实例（首次移动时按点位解析）。</summary>
        private UILinkageCanvasInstance _uiLinkageEraseInstance;

        /// <summary>独立画布当前墨迹（供橡皮擦等复用主画布逻辑时使用）；未解析到实例返回 null。</summary>
        internal StrokeCollection GetUILinkageStrokes()
            => _uiLinkageEraseInstance?.Canvas?.Strokes;

        /// <summary>按点位解析橡皮擦作用的实例（主画布坐标）。</summary>
        internal bool TryResolveUILinkageEraseInstance(Point inkCanvasPoint, out UILinkageCanvasInstance instance)
            => TryGetUILinkageInstanceAt(inkCanvasPoint, out instance);
    }
}
