using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ink_Canvas.Controls
{
    /// <summary>
    /// 独立画布：一块与主画布解耦的墨迹区域。
    /// - 复用 WPF InkCanvas / Stroke 节点结构，不重复造轮子。
    /// - 作为独立控件存在，不受主画布墨迹移动（stroke.Transform）影响，保持在原位。
    /// - 无内部填充（透明），鼠标模式下不遮挡穿透视觉。
    /// - 控件总尺寸 = 视口 + 向外延伸的滚动条条带：竖直/水平滚动条位于视口右侧/下侧
    ///   的外侧条带内，不占用批注视口。
    /// - 滚动同步：<see cref="SetScrollInfo"/> 传入目标偏移，墨迹以平滑过渡动画跟随。
    /// - 顶部「墨迹跟随页面平移」提示条（10s 自动关闭）；右下角独立撤销/恢复按钮
    ///   （只作用于本画布墨迹，与主画布 TimeMachine 互不影响）。
    /// <para>
    /// 派生说明：本类不是 sealed，<see cref="BackgroundLayer"/> / <see cref="ApplyInkTransform"/>
    /// / <see cref="AutoSizeToViewport"/> 是给「填充式宿主」（内置图片查看器）预留的扩展点：
    /// 背景层可以放图片底图，任意仿射变换让墨迹与底图保持同一坐标系。
    /// </para>
    /// </summary>
    public class IndependentCanvas : UserControl
    {
        /// <summary>向外延伸的滚动条条带厚度（DIP）。</summary>
        private const double ScrollBarThickness = 24;

        private static readonly TimeSpan ScrollAnimDuration = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan NoticeAutoCloseDelay = TimeSpan.FromSeconds(10);

        /// <summary>
        /// 触发滚动补间动画的最小位移（DIP，两轴绝对值之和）。
        /// <para>
        /// 低于该值的位移直接贴合：补间期间会订阅 <c>CompositionTarget.Rendering</c>，
        /// 每帧对全部笔迹做 <c>Transform</c> 并重绘整块画布（目标窗口最大化后即全屏）。
        /// 目标应用的读数抖动（渲染线程回写、滚动条单位换算取整）会带来持续的微小增量，
        /// 一律动画会让渲染循环长时间无法空闲 —— CPU 被无意义地吃满。
        /// </para>
        /// </summary>
        private const double ScrollAnimMinShiftDip = 1.0;

        private readonly Grid _rootGrid;
        private readonly Border _contentHost;
        private readonly Grid _inkHost;
        private readonly System.Windows.Controls.Canvas _backgroundLayer;
        private readonly System.Windows.Controls.Canvas _overlayLayer;
        private readonly InkCanvas _inkCanvas;
        private readonly System.Windows.Controls.Canvas _fadeLayer;
        private readonly MatrixTransform _fadeLayerTransform;
        private Matrix _fadeLayerMatrix = Matrix.Identity;
        private readonly ScrollBar _verticalScrollBar;
        private readonly ScrollBar _horizontalScrollBar;
        private readonly FrameworkElement _noticeBar;
        private readonly DispatcherTimer _noticeTimer;
        private readonly StackPanel _inkButtonsPanel;
        private readonly Button _undoButton;
        private readonly Button _redoButton;
        private readonly IndependentInkHistory _history;

        // 滚动辅助按钮：左右两侧居中的上/下按钮、左下角的左/右按钮（与撤销/恢复同样式）。
        private readonly StackPanel _verticalAssistLeft;
        private readonly StackPanel _verticalAssistRight;
        private readonly StackPanel _horizontalAssist;
        private readonly Button _scrollUpLeftButton;
        private readonly Button _scrollDownLeftButton;
        private readonly Button _scrollUpRightButton;
        private readonly Button _scrollDownRightButton;
        private readonly Button _scrollLeftButton;
        private readonly Button _scrollRightButton;
        private bool _inChromelessCapture;

        private double _scrollX;
        private double _scrollY;

        private double _viewportW = 1;
        private double _viewportH = 1;
        private bool _showVerticalBar = true;
        private bool _showHorizontalBar = true;
        private bool _scrollBarsSuppressed;

        // 平滑滚动动画状态：_pendingShift 为尚未烘焙进笔迹数据的目标位移，
        // 每帧按缓动曲线将其中一部分真实应用到笔迹，保证「数据坐标 == 视觉坐标」。
        private Vector _pendingShift;
        private Vector _animSegmentStart;
        private Vector _animSegmentApplied;
        private DateTime _animStartTimeUtc;
        private bool _scrollAnimating;
        private bool _snapNextScrollSync = true;

        public IndependentCanvas()
        {
            // 让内容网格填满控件（否则内容按自身尺寸对齐，滚动条/墨迹区域会塌缩）。
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;

            _inkCanvas = new InkCanvas
            {
                Background = Brushes.Transparent,
                ClipToBounds = true
            };

            // 触摸抑制（对齐主画布 InkCanvas_PreviewTouchDown 的策略）：
            // WPF 会把触摸提升为触笔事件，裸 InkCanvas 因此会把「单指拖动 / 双指手势」
            // 直接画成笔迹。主画布靠自绘输入管线 + 手势系统规避，独立画布必须在
            // 提升阶段就抑制：第二指在 MULTI_TOUCH_DELAY_MS 内落下即判定为多指手势，
            // 期间落在画布上的那一笔不提交（延迟提交 + 撤销）。
            _inkCanvas.IsManipulationEnabled = false;
            _inkCanvas.PreviewTouchDown += InkSurface_PreviewTouchDown;
            _inkCanvas.PreviewTouchUp += InkSurface_PreviewTouchUp;
            _inkCanvas.LostTouchCapture += InkSurface_LostTouchCapture;

            // 无内部填充色：保持透明，避免影响鼠标模式下的穿透视觉。
            //
            // 背景层：位于墨迹之下、与墨迹同坐标系。基类自身不使用（保持空），
            // 派生宿主（内置图片查看器）在此放图片底图；放在 _inkHost 内可保证与墨迹一起
            // 被 ClipToBounds 裁剪、共享同一套坐标。IsHitTestVisible=false 使其不抢输入。
            _backgroundLayer = new System.Windows.Controls.Canvas
            {
                Background = null,
                IsHitTestVisible = false,
                ClipToBounds = true
            };

            // 渐隐层：与内层 InkCanvas 同一坐标系（两者都在同一个 Grid 单元格里、同尺寸同原点），
            // 供宿主共享的 InkFadeManager 挂载激光笔/渐隐笔迹的视觉副本。因为滚动同步是把
            // 笔迹数据整体 Transform 平移的，渐隐层也必须跟着平移，否则渐隐中的激光笔迹
            // 会与画布内容错位（见 ApplyShift）。用矩阵而非平移量，是为了让派生宿主
            // （图片查看器）的缩放/旋转也能同步到渐隐视觉。
            _fadeLayer = new System.Windows.Controls.Canvas
            {
                Background = null,
                IsHitTestVisible = false,
                ClipToBounds = true
            };
            _fadeLayerTransform = new MatrixTransform(_fadeLayerMatrix);
            _fadeLayer.RenderTransform = _fadeLayerTransform;

            _inkHost = new Grid();
            _inkHost.Children.Add(_backgroundLayer);
            _inkHost.Children.Add(_inkCanvas);
            _inkHost.Children.Add(_fadeLayer);

            _contentHost = new Border
            {
                Child = _inkHost,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                ClipToBounds = true
            };

            // 覆盖层：与视口同坐标系、**不随视图变换**的宿主，供派生宿主放「屏幕空间」的
            // 交互装饰（内置图片查看器在这里放橡皮擦擦头足迹 —— 擦头必须跟着指针走，
            // 绝不能被墨迹的缩放/旋转带着跑）。不接收命中测试，因此不会抢走书写/擦除输入。
            _overlayLayer = new System.Windows.Controls.Canvas
            {
                Background = null,
                IsHitTestVisible = false,
                ClipToBounds = true
            };

            _verticalScrollBar = CreateScrollBar(Orientation.Vertical);
            _horizontalScrollBar = CreateScrollBar(Orientation.Horizontal);

            _noticeBar = CreateNoticeBar(out _noticeTimer);

            _history = new IndependentInkHistory(_inkCanvas);
            _history.HistoryChanged += UpdateInkButtonsState;
            _undoButton = CreateRoundInkButton("\uE7A7", "撤销");
            _redoButton = CreateRoundInkButton("\uE7A6", "恢复");
            _undoButton.Click += (_, _) => _history.Undo();
            _redoButton.Click += (_, _) => _history.Redo();
            _inkButtonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 8, 8),
                IsHitTestVisible = true
            };
            _inkButtonsPanel.Children.Add(_undoButton);
            _inkButtonsPanel.Children.Add(_redoButton);
            UpdateInkButtonsState();

            // 滚动辅助按钮：上下可滚动时左右两侧居中显示上/下；左右可滚动时左下角显示左/右。
            _scrollUpLeftButton = CreateRoundInkButton("\uE70E", "向上滚动", new Thickness(0));
            _scrollDownLeftButton = CreateRoundInkButton("\uE70D", "向下滚动", new Thickness(0, 8, 0, 0));
            _scrollUpRightButton = CreateRoundInkButton("\uE70E", "向上滚动", new Thickness(0));
            _scrollDownRightButton = CreateRoundInkButton("\uE70D", "向下滚动", new Thickness(0, 8, 0, 0));
            _scrollLeftButton = CreateRoundInkButton("\uE76B", "向左滚动", new Thickness(0));
            _scrollRightButton = CreateRoundInkButton("\uE76C", "向右滚动", new Thickness(8, 0, 0, 0));

            _scrollUpLeftButton.Click += (_, _) => OnScrollStepRequested?.Invoke(Orientation.Vertical, -ScrollStepDip());
            _scrollDownLeftButton.Click += (_, _) => OnScrollStepRequested?.Invoke(Orientation.Vertical, ScrollStepDip());
            _scrollUpRightButton.Click += (_, _) => OnScrollStepRequested?.Invoke(Orientation.Vertical, -ScrollStepDip());
            _scrollDownRightButton.Click += (_, _) => OnScrollStepRequested?.Invoke(Orientation.Vertical, ScrollStepDip());
            _scrollLeftButton.Click += (_, _) => OnScrollStepRequested?.Invoke(Orientation.Horizontal, -ScrollStepDipHorizontal());
            _scrollRightButton.Click += (_, _) => OnScrollStepRequested?.Invoke(Orientation.Horizontal, ScrollStepDipHorizontal());

            _verticalAssistLeft = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                IsHitTestVisible = true
            };
            _verticalAssistLeft.Children.Add(_scrollUpLeftButton);
            _verticalAssistLeft.Children.Add(_scrollDownLeftButton);

            _verticalAssistRight = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                IsHitTestVisible = true
            };
            _verticalAssistRight.Children.Add(_scrollUpRightButton);
            _verticalAssistRight.Children.Add(_scrollDownRightButton);

            _horizontalAssist = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8, 0, 0, 8),
                IsHitTestVisible = true
            };
            _horizontalAssist.Children.Add(_scrollLeftButton);
            _horizontalAssist.Children.Add(_scrollRightButton);

            _rootGrid = new Grid();
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // (0,0) 视口：墨迹宿主 + 顶部提示条 + 右下角撤销/恢复 + 滚动辅助按钮。
            Grid.SetRow(_contentHost, 0);
            Grid.SetColumn(_contentHost, 0);
            Grid.SetRow(_noticeBar, 0);
            Grid.SetColumn(_noticeBar, 0);
            Grid.SetRow(_inkButtonsPanel, 0);
            Grid.SetColumn(_inkButtonsPanel, 0);
            Grid.SetRow(_verticalAssistLeft, 0);
            Grid.SetColumn(_verticalAssistLeft, 0);
            Grid.SetRow(_verticalAssistRight, 0);
            Grid.SetColumn(_verticalAssistRight, 0);
            Grid.SetRow(_horizontalAssist, 0);
            Grid.SetColumn(_horizontalAssist, 0);

            // (1,0)/(0,1)：视口向外延伸的滚动条条带（右侧/下侧），不占用批注视口。
            Grid.SetRow(_verticalScrollBar, 0);
            Grid.SetColumn(_verticalScrollBar, 1);
            Grid.SetRow(_horizontalScrollBar, 1);
            Grid.SetColumn(_horizontalScrollBar, 0);

            _rootGrid.Children.Add(_contentHost);
            _rootGrid.Children.Add(_overlayLayer);
            _rootGrid.Children.Add(_verticalScrollBar);
            _rootGrid.Children.Add(_horizontalScrollBar);
            _rootGrid.Children.Add(_noticeBar);
            _rootGrid.Children.Add(_inkButtonsPanel);
            _rootGrid.Children.Add(_verticalAssistLeft);
            _rootGrid.Children.Add(_verticalAssistRight);
            _rootGrid.Children.Add(_horizontalAssist);

            // 默认不显示滚动条，等调用处开启。
            _verticalScrollBar.Visibility = Visibility.Collapsed;
            _horizontalScrollBar.Visibility = Visibility.Collapsed;

            // 用户拖动滚动条时通知调用处，由其决定是否真实滚动目标组件。
            _verticalScrollBar.Scroll += (_, _) => OnUserScroll?.Invoke(Orientation.Vertical, _verticalScrollBar.Value);
            _horizontalScrollBar.Scroll += (_, _) => OnUserScroll?.Invoke(Orientation.Horizontal, _horizontalScrollBar.Value);

            // 画布隐藏/显示时同步滚动条与墨迹按钮的可见性。
            IsVisibleChanged += (_, _) => UpdateOverlayVisibility();

            Content = _rootGrid;
            UpdateLayoutSizes();
        }

        /// <summary>
        /// 用户拖动滚动条时的回调（方向 + 目标滚动条值，DIP）。由调用处据此调用目标组件的
        /// ScrollPattern（或其它滚动接口）实现真实滚动；程序调用 SetScrollInfo 不会触发该回调。
        /// </summary>
        public Action<Orientation, double> OnUserScroll;

        /// <summary>滚动辅助按钮点击回调（方向 + 滚动增量，DIP；正值向下/向右）。</summary>
        public Action<Orientation, double> OnScrollStepRequested;

        // ---------------- 触摸抑制（对齐主画布的手势判定策略） ----------------

        /// <summary>主画布同款多指判定窗口：第二指在此时间内落下即视为多指手势。</summary>
        private const double MultiTouchDelayMs = 100;

        /// <summary>当前按下的触摸设备 Id（用于判断是否已进入多指手势）。</summary>
        private readonly System.Collections.Generic.HashSet<int> _touchDownIds
            = new System.Collections.Generic.HashSet<int>();

        /// <summary>因多指手势被抑制的触摸设备 Id（抬起时用于回滚其误产生的笔迹）。</summary>
        private readonly System.Collections.Generic.HashSet<int> _suppressedTouchIds
            = new System.Collections.Generic.HashSet<int>();

        /// <summary>按下第一根手指时的笔迹快照，用于多指判定成立后回滚这一笔。</summary>
        private StrokeCollection _touchStrokeSnapshot;

        private bool _touchEditingModeSaved;
        private bool _multiTouchSuppressed;
        private bool _multiTouchSuppressTimerActive;
        private DateTime _lastTouchDownUtc = DateTime.MinValue;

        /// <summary>
        /// 触摸抑制时临时切换到的编辑模式（由宿主按当前工具注入，与主画布
        /// <c>lastInkCanvasEditingMode</c> 同义）。默认 Ink：仅在检测到多指手势时启用。
        /// </summary>
        public InkCanvasEditingMode TouchUserEditingMode { get; set; } = InkCanvasEditingMode.Ink;

        /// <summary>
        /// 触摸抑制是否可用：宿主在非笔类工具（鼠标/图形/漫游等）下应置为 false，
        /// 避免把本就不接收输入的画布状态改坏。
        /// </summary>
        public bool TouchSuppressionEnabled { get; set; } = true;

        /// <summary>
        /// 非批注状态下是否仍需要接收输入（默认 false，即整块画布让出命中测试）。
        /// <para>
        /// 内置图片查看器必须覆写为 true：它要在鼠标/漫游模式下继续用鼠标拖动与双指手势
        /// 平移缩放图片，因此不能像 UI 联动批注层那样在非批注状态被彻底禁用；
        /// 不落墨由 <c>EditingMode = None</c> 保证（见 <c>IndependentCanvasStateSync</c>）。
        /// </para>
        /// </summary>
        public virtual bool RequiresInputWhenIdle => false;

        /// <summary>
        /// 是否接受宿主按批注状态暂停/恢复本画布的浮层（滚动条、撤销/恢复按钮等），默认 true。
        /// <para>
        /// 内置图片查看器覆写为 false：它的滚动条本就按设计隐藏，而撤销/恢复按钮是批注的
        /// 主要入口，不该因为切到鼠标模式就消失。
        /// </para>
        /// </summary>
        public virtual bool HostControlsOverlaySuppression => true;

        // ---------------- 手掌擦（与主画布同一套接触面积判定） ----------------

        /// <summary>
        /// 手掌接触分类器：由宿主注入（见 <see cref="Ink_Canvas.Ink.PalmContactClassifier"/>），
        /// 内部按与主画布相同的设置与算法判定接触面是否为手掌。为 null 时本画布不做手掌判定。
        /// </summary>
        public Ink_Canvas.Ink.PalmContactClassifier PalmContactClassifier { get; set; }

        /// <summary>本画布当前是否因手掌接触而处于擦除态。</summary>
        public bool IsPalmErasing { get; private set; }

        /// <summary>进入手掌擦前的编辑模式，抬起后据此还原。</summary>
        private InkCanvasEditingMode _palmErasePreviousEditingMode = InkCanvasEditingMode.Ink;

        /// <summary>
        /// 接触面是否达到手掌阈值。口径与主画布 <c>InkCanvas_PreviewTouchDown</c> 完全一致：
        /// 用 <c>GetTouchPoint(null).Bounds</c>（根视觉坐标、已是 DIP）的宽高交给宿主分类器，
        /// 而分类器内部复用共用的 <see cref="Ink_Canvas.Ink.PalmEraserCalculator"/> 与设置，
        /// 因此阈值/灵敏度/擦头尺寸的语义与主画布相同，不存在第二套参数。
        /// </summary>
        private bool TryEvaluatePalmContact(TouchEventArgs e, out double eraserWidthDip)
        {
            eraserWidthDip = 0;
            var classifier = PalmContactClassifier;
            if (classifier == null) return false;

            try
            {
                var bounds = e.GetTouchPoint(null).Bounds;
                return classifier(bounds.Width, bounds.Height, out eraserWidthDip)
                       && eraserWidthDip > 0;
            }
            catch (Exception ex)
            {
                Ink_Canvas.Helpers.LogHelper.WriteLogToFile(
                    $"独立画布手掌擦判定失败: {ex.Message}", Ink_Canvas.Helpers.LogHelper.LogType.Warning);
                return false;
            }
        }

        /// <summary>
        /// 进入手掌擦：切到 <see cref="InkCanvasEditingMode.EraseByPoint"/> 并把擦头设成判定出的宽度。
        /// 调用方负责同时把该触摸事件标记为已处理 —— 必须阻断 WPF 的「触摸提升为触笔」，
        /// 否则手掌会在抬掌后留下一道笔迹（主画布靠自绘输入管线天然规避，本画布没有那条管线）。
        /// </summary>
        private void EnterPalmErase(double eraserWidthDip)
        {
            IsPalmErasing = true;
            _palmErasePreviousEditingMode = _inkCanvas.EditingMode;
            _inkCanvas.EraserShape = new RectangleStylusShape(eraserWidthDip, eraserWidthDip);
            _inkCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
        }

        /// <summary>退出手掌擦，还原进入前的编辑模式与擦头形状。</summary>
        private void ExitPalmErase()
        {
            if (!IsPalmErasing) return;
            IsPalmErasing = false;

            // 还原为「进入前」的模式，而不是宿主当前下发的 TouchUserEditingMode：
            // 期间宿主可能已经切过工具，而触摸抑制那条路径本来就以宿主下发值为准，
            // 两条路径各自保持自己的语义，互不覆盖。
            _inkCanvas.EditingMode = _palmErasePreviousEditingMode;
            _inkCanvas.EraserShape = null;
        }

        private void InkSurface_PreviewTouchDown(object sender, TouchEventArgs e)
        {
            // 手掌擦优先于多指判定：手掌落下时不该被当成手势，也不该产生笔迹。
            // 判定未通过则原样继续走下面的多指抑制逻辑。
            if (TryEvaluatePalmContact(e, out var palmEraserWidth))
            {
                EnterPalmErase(palmEraserWidth);
                // 阻止触摸提升为触笔事件 → 手掌不会落墨；随后 WPF 的 InkCanvas 会按
                // EraseByPoint 处理同一触摸产生的触笔事件，从而真的擦除。
                e.Handled = true;
                return;
            }

            if (!TouchSuppressionEnabled) return;

            var now = DateTime.UtcNow;
            var elapsedMs = (now - _lastTouchDownUtc).TotalMilliseconds;
            _lastTouchDownUtc = now;

            _touchDownIds.Add(e.TouchDevice.Id);

            // 单指：记录快照，供「第二指紧随其后」时回滚这一笔。
            if (_touchDownIds.Count == 1)
            {
                // 只有可能落墨的模式才值得克隆整份笔迹集合：None / 擦除模式下这一指
                // 本就不会新增笔迹，回滚也就无所指。触摸屏上每次落指都克隆一次全量笔迹
                // 是实打实的卡顿来源，图片查看器这类「始终接收输入」的宿主尤其明显。
                bool mayCollectInk = _inkCanvas.EditingMode == InkCanvasEditingMode.Ink
                                     || _inkCanvas.EditingMode == InkCanvasEditingMode.Select;
                _touchStrokeSnapshot = mayCollectInk ? _inkCanvas.Strokes.Clone() : null;
                return;
            }

            // 多指手势判定：第二指在窗口内落下 → 这一笔不是书写意图。
            // 与主画布一致，判定本身也放在延迟回调里，保证「两指同时落下」也走抑制分支。
            if (_touchDownIds.Count > 1 && elapsedMs < MultiTouchDelayMs)
            {
                _suppressedTouchIds.Add(e.TouchDevice.Id);
                if (!_multiTouchSuppressTimerActive)
                {
                    _multiTouchSuppressTimerActive = true;
                    var remaining = Math.Max(1, MultiTouchDelayMs - elapsedMs);
                    var timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(remaining)
                    };
                    timer.Tick += (s, _) =>
                    {
                        timer.Stop();
                        _multiTouchSuppressTimerActive = false;
                        if (_touchDownIds.Count > 1) EnterMultiTouchSuppression();
                    };
                    timer.Start();
                }
                return;
            }

            // 第二指落下较晚（超过判定窗口）：按多指手势处理，不再提交新的笔迹。
            _suppressedTouchIds.Add(e.TouchDevice.Id);
            EnterMultiTouchSuppression();
        }

        private void EnterMultiTouchSuppression()
        {
            if (_multiTouchSuppressed) return;
            _multiTouchSuppressed = true;

            if (!_touchEditingModeSaved)
            {
                // 只记「是否需要恢复」；恢复值取宿主当前下发的 TouchUserEditingMode，
                // 避免把画布改回手势开始前的过时模式。
                _touchEditingModeSaved = true;
            }

            // 切 None 抑制 WPF 内置墨迹：第二指及之后不再产生新笔迹。
            // 形状/漫游等工具下宿主本来就让独立画布保持 None，这里只对可书写模式生效
            // （对齐主画布：橡皮擦模式不抑制，让原生擦除照常工作）。
            if (_inkCanvas.EditingMode == InkCanvasEditingMode.Ink
                || _inkCanvas.EditingMode == InkCanvasEditingMode.Select)
            {
                _inkCanvas.EditingMode = InkCanvasEditingMode.None;
            }
        }

        private void InkSurface_PreviewTouchUp(object sender, TouchEventArgs e)
            => ReleaseTouch(e.TouchDevice.Id);

        private void InkSurface_LostTouchCapture(object sender, TouchEventArgs e)
            => ReleaseTouch(e.TouchDevice.Id);

        private void ReleaseTouch(int touchId)
        {
            _touchDownIds.Remove(touchId);

            // 还有手指按着：保持抑制状态。
            if (_touchDownIds.Count > 0) return;

            // 最后一次接触抬起：手掌擦态必须退出，否则画布会永久停在 EraseByPoint。
            ExitPalmErase();

            bool wasSuppressed = _suppressedTouchIds.Count > 0;
            _suppressedTouchIds.Clear();
            _multiTouchSuppressTimerActive = false;

            var snapshot = _touchStrokeSnapshot;
            _touchStrokeSnapshot = null;

            // 先把编辑模式恢复到宿主当前工具，再做回滚。
            if (_multiTouchSuppressed)
            {
                _multiTouchSuppressed = false;
                if (_touchEditingModeSaved)
                {
                    _touchEditingModeSaved = false;
                    // 宿主可能在这期间改过工具：以宿主当前下发值为准，避免把模式改回旧值。
                    _inkCanvas.EditingMode = TouchUserEditingMode;
                }
            }

            if (!wasSuppressed || snapshot == null) return;

            // 关键时序：PreviewTouchUp 早于 InkCanvas 提交在途笔画（后者在 TouchUp/StylusUp 阶段
            // 才落到 Strokes），因此回滚必须延后一拍，否则会「回滚了个寂寞」，多指手势留下的
            // 误笔迹仍然留在画布上。
            Dispatcher.BeginInvoke(new Action(() => RollbackTouchArtefacts(snapshot)),
                DispatcherPriority.Background);
        }

        /// <summary>移除多指手势期间被误提交的笔迹（仅移除快照之后新增的笔画）。</summary>
        private void RollbackTouchArtefacts(StrokeCollection snapshot)
        {
            if (snapshot == null) return;
            try
            {
                var current = _inkCanvas.Strokes;
                var added = new StrokeCollection(current.Where(s => !snapshot.Contains(s)));
                if (added.Count > 0) current.Remove(added);
            }
            catch (Exception ex)
            {
                Ink_Canvas.Helpers.LogHelper.WriteLogToFile(
                    $"独立画布多指手势笔迹回滚失败: {ex.Message}",
                    Ink_Canvas.Helpers.LogHelper.LogType.Warning);
            }
        }

        private double ScrollStepDip() => Math.Max(120, _viewportH * 0.85);

        private double ScrollStepDipHorizontal() => Math.Max(120, _viewportW * 0.85);

        /// <summary>滚动条样式缓存（加粗 thumb 的自定义模板，解析一次全局复用）。</summary>
        private static readonly Lazy<Style> ScrollBarStyleCache = new Lazy<Style>(CreateScrollBarStyle);

        private static Style CreateScrollBarStyle()
        {
            // 自定义模板：无两端箭头按钮，thumb 加粗为 14px 圆角胶囊（默认模板的 thumb 过细，
            // 触摸不易拖动）；竖直条 value 向下递增（IsDirectionReversed），轨道支持点击翻页。
            const string xaml = """
                <Style
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    TargetType="{x:Type ScrollBar}">
                    <Setter Property="Background" Value="Transparent" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="{x:Type ScrollBar}">
                                <Grid Background="Transparent">
                                    <Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}">
                                        <Track.DecreaseRepeatButton>
                                            <RepeatButton Command="{x:Static ScrollBar.PageUpCommand}"
                                                          Opacity="0" Focusable="False" IsTabStop="False" />
                                        </Track.DecreaseRepeatButton>
                                        <Track.IncreaseRepeatButton>
                                            <RepeatButton Command="{x:Static ScrollBar.PageDownCommand}"
                                                          Opacity="0" Focusable="False" IsTabStop="False" />
                                        </Track.IncreaseRepeatButton>
                                        <Track.Thumb>
                                            <Thumb x:Name="ThumbPart" Width="14" Height="Auto" Focusable="False">
                                                <Thumb.Template>
                                                    <ControlTemplate TargetType="{x:Type Thumb}">
                                                        <Border x:Name="ThumbBg" Background="#88353D49"
                                                                CornerRadius="7" />
                                                        <ControlTemplate.Triggers>
                                                            <Trigger Property="IsMouseOver" Value="True">
                                                                <Setter TargetName="ThumbBg"
                                                                        Property="Background" Value="#AA3E4756" />
                                                            </Trigger>
                                                            <Trigger Property="IsDragging" Value="True">
                                                                <Setter TargetName="ThumbBg"
                                                                        Property="Background" Value="#CC2A2F3A" />
                                                            </Trigger>
                                                        </ControlTemplate.Triggers>
                                                    </ControlTemplate>
                                                </Thumb.Template>
                                            </Thumb>
                                        </Track.Thumb>
                                    </Track>
                                </Grid>
                                <ControlTemplate.Triggers>
                                    <Trigger Property="Orientation" Value="Vertical">
                                        <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="True" />
                                    </Trigger>
                                    <Trigger Property="Orientation" Value="Horizontal">
                                        <Setter TargetName="ThumbPart" Property="Width" Value="Auto" />
                                        <Setter TargetName="ThumbPart" Property="Height" Value="14" />
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                """;
            try
            {
                return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
            }
            catch (Exception ex)
            {
                // 解析失败时回退默认模板（仅影响 thumb 视觉粗细），并阻止 Lazy 缓存异常。
                Ink_Canvas.Helpers.LogHelper.WriteLogToFile(
                    $"独立画布滚动条模板解析失败: {ex.Message}", Ink_Canvas.Helpers.LogHelper.LogType.Warning);
                return new Style(typeof(System.Windows.Controls.Primitives.ScrollBar));
            }
        }

        private static ScrollBar CreateScrollBar(Orientation orientation)
        {
            return new ScrollBar
            {
                Orientation = orientation,
                Minimum = 0,
                Maximum = 0,
                Value = 0,
                ViewportSize = 0,
                IsTabStop = false,
                Style = ScrollBarStyleCache.Value,
                // 加粗滚动条以便触摸操作（竖直条宽、水平条高）。
                Width = orientation == Orientation.Vertical ? ScrollBarThickness : double.NaN,
                Height = orientation == Orientation.Horizontal ? ScrollBarThickness : double.NaN
            };
        }

        private FrameworkElement CreateNoticeBar(out DispatcherTimer autoCloseTimer)
        {
            var text = new TextBlock
            {
                Text = "当前区域支持笔迹随页面移动",
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 卡片不接收命中（其区域仍可正常书写），仅右侧关闭按钮可点击。
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x20, 0x24, 0x2C)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(24, 14, 48, 14),
                IsHitTestVisible = false,
                Child = text
            };

            var closeButton = new Button
            {
                Content = new TextBlock
                {
                    Text = "\uE8BB",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD4, 0xDC))
                },
                Width = 34,
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false
            };

            // 居中悬浮于视口中央，随画布移动。
            var panel = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = true,
                Visibility = Visibility.Collapsed
            };
            panel.Children.Add(card);
            panel.Children.Add(closeButton);

            autoCloseTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = NoticeAutoCloseDelay
            };
            autoCloseTimer.Tick += (_, _) => HideNotice();

            closeButton.Click += (_, _) => HideNotice();
            return panel;
        }

        private static Button CreateRoundInkButton(string glyph, string tooltip, Thickness? margin = null)
        {
            var content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 17,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border))
            {
                Name = "roundBg"
            };
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(20));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;

            return new Button
            {
                Content = content,
                Width = 40,
                Height = 40,
                Margin = margin ?? new Thickness(4, 0, 0, 0),
                ToolTip = tooltip,
                Focusable = false,
                IsTabStop = false,
                Cursor = Cursors.Hand,
                Template = template,
                Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x2A, 0x2E, 0x36)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };
        }

        /// <summary>当前画布内的墨迹集合（复用 Stroke 节点结构）。</summary>
        public StrokeCollection Strokes => _inkCanvas.Strokes;

        /// <summary>内部 InkCanvas（供上层需要读取/清空墨迹时使用）。</summary>
        public InkCanvas InkSurface => _inkCanvas;

        private Helpers.InkFadeVisualHost _fadeVisualHost;

        /// <summary>
        /// 渐隐视觉宿主：供宿主共享的 <see cref="Helpers.InkFadeManager"/> 把激光笔/渐隐笔迹的
        /// 视觉副本挂到本画布内部（与笔迹同坐标系），从而复用主画布那一整套渐隐实现。
        /// </summary>
        public Helpers.InkFadeVisualHost FadeVisualHost
            => _fadeVisualHost ??= new Helpers.InkFadeVisualHost(_fadeLayer.Children);

        /// <summary>填充（背景）样式。默认透明，不应设置不透明填充以免影响鼠标模式穿透。</summary>
        public Brush Fill
        {
            get => _contentHost.Background;
            set => _contentHost.Background = value;
        }

        /// <summary>边框画刷。</summary>
        public Brush BorderStroke
        {
            get => _contentHost.BorderBrush;
            set => _contentHost.BorderBrush = value;
        }

        /// <summary>边框粗细。</summary>
        public Thickness BorderStrokeThickness
        {
            get => _contentHost.BorderThickness;
            set => _contentHost.BorderThickness = value;
        }

        /// <summary>是否显示竖直滚动条（设置项）。</summary>
        public bool ShowVerticalScrollBar
        {
            get => _showVerticalBar;
            set
            {
                if (_showVerticalBar == value) return;
                _showVerticalBar = value;
                UpdateLayoutSizes();
            }
        }

        /// <summary>是否显示水平滚动条（设置项）。</summary>
        public bool ShowHorizontalScrollBar
        {
            get => _showHorizontalBar;
            set
            {
                if (_showHorizontalBar == value) return;
                _showHorizontalBar = value;
                UpdateLayoutSizes();
            }
        }

        /// <summary>
        /// 设置视口（批注区域）大小。控件总尺寸 = 视口 + 向外延伸的滚动条条带，
        /// 因此滚动条位于视口外侧而不占用批注区域。
        /// </summary>
        public void SetViewportSize(double width, double height)
        {
            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) width = 1;
            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0) height = 1;

            _viewportW = width;
            _viewportH = height;
            UpdateLayoutSizes();
        }

        /// <summary>
        /// 暂停显示滚动条与撤销/恢复按钮（如鼠标模式下无法交互时）。
        /// 画布本身与墨迹仍显示。
        /// </summary>
        public void SetScrollBarsSuppressed(bool suppressed)
        {
            if (_scrollBarsSuppressed == suppressed) return;
            _scrollBarsSuppressed = suppressed;
            UpdateOverlayVisibility();
        }

        /// <summary>滚动条/墨迹按钮是否因鼠标模式等被暂停显示。</summary>
        public bool IsScrollBarsSuppressed => _scrollBarsSuppressed;

        private void UpdateLayoutSizes()
        {
            _rootGrid.ColumnDefinitions[1].Width = _showVerticalBar ? GridLength.Auto : new GridLength(0);
            _rootGrid.RowDefinitions[1].Height = _showHorizontalBar ? GridLength.Auto : new GridLength(0);

            // 控件总尺寸 = 视口 + 向外延伸的滚动条条带（条带保留与否取决于设置而非暂停状态，
            // 避免鼠标模式切换时视口尺寸跳动）。
            // 填充式宿主（AutoSizeToViewport=false）的尺寸由父容器布局决定，这里不能覆盖。
            if (AutoSizeToViewport)
            {
                double targetW = _viewportW + (_showVerticalBar ? ScrollBarThickness : 0);
                double targetH = _viewportH + (_showHorizontalBar ? ScrollBarThickness : 0);
                if (Width != targetW) Width = targetW;
                if (Height != targetH) Height = targetH;
            }

            UpdateOverlayVisibility();
        }

        /// <summary>
        /// 为 false 时本控件不再把 <c>Width</c>/<c>Height</c> 绑定到
        /// <see cref="SetViewportSize"/> 传入的视口尺寸，而由父容器布局决定（默认 true）。
        /// <para>
        /// 图片查看器这类「填充式宿主」必须置 false：窗口尺寸变化时画布应跟着拉伸，
        /// 而不是被 SetViewportSize 钉死成上一次的视口大小。
        /// </para>
        /// </summary>
        public bool AutoSizeToViewport { get; set; } = true;

        /// <summary>
        /// 统一维护滚动条与撤销/恢复按钮的可见性：
        /// 仅在「设置开启 + 未被暂停（如鼠标模式）+ 画布自身可见」时显示。
        /// 画布隐藏（Visibility 或窗口最小化）时同步隐藏。
        /// </summary>
        private void UpdateOverlayVisibility()
        {
            bool interactive = IsVisible && !_scrollBarsSuppressed;

            _verticalScrollBar.Visibility = _showVerticalBar && interactive
                ? Visibility.Visible
                : Visibility.Collapsed;
            _horizontalScrollBar.Visibility = _showHorizontalBar && interactive
                ? Visibility.Visible
                : Visibility.Collapsed;
            _inkButtonsPanel.Visibility = interactive && !_inChromelessCapture
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateScrollAssistButtons();
        }

        /// <summary>
        /// 滚动辅助按钮可见性：上下可滚动（Maximum>0）时显示左右两侧的上/下按钮，
        /// 左右可滚动时显示左下角的左/右按钮；鼠标模式/截图模式下隐藏。
        /// </summary>
        private void UpdateScrollAssistButtons()
        {
            bool interactive = IsVisible && !_scrollBarsSuppressed && !_inChromelessCapture;
            bool vScrollable = _verticalScrollBar.Maximum > 0.5;
            bool hScrollable = _horizontalScrollBar.Maximum > 0.5;

            _verticalAssistLeft.Visibility = interactive && vScrollable ? Visibility.Visible : Visibility.Collapsed;
            _verticalAssistRight.Visibility = interactive && vScrollable ? Visibility.Visible : Visibility.Collapsed;
            _horizontalAssist.Visibility = interactive && hScrollable ? Visibility.Visible : Visibility.Collapsed;

            double v = _verticalScrollBar.Value, vMax = _verticalScrollBar.Maximum;
            bool vUpEnabled = v > 0.5;
            bool vDownEnabled = v < vMax - 0.5;
            _scrollUpLeftButton.IsEnabled = vUpEnabled;
            _scrollDownLeftButton.IsEnabled = vDownEnabled;
            _scrollUpRightButton.IsEnabled = vUpEnabled;
            _scrollDownRightButton.IsEnabled = vDownEnabled;

            double h = _horizontalScrollBar.Value, hMax = _horizontalScrollBar.Maximum;
            _scrollLeftButton.IsEnabled = h > 0.5;
            _scrollRightButton.IsEnabled = h < hMax - 0.5;
        }

        /// <summary>显示顶部「墨迹跟随页面平移」提示，10 秒后自动关闭。</summary>
        public void ShowFollowNotice()
        {
            _noticeBar.Visibility = Visibility.Visible;
            _noticeTimer.Stop();
            _noticeTimer.Start();
        }

        /// <summary>隐藏顶部提示条。</summary>
        public void HideNotice()
        {
            _noticeTimer.Stop();
            _noticeBar.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 由调用处传入滚动信息，驱动墨迹平移与滚动条滚动。
        /// </summary>
        /// <param name="viewport">可视区大小（即本画布视口大小，DIP）。</param>
        /// <param name="extent">内容总大小（DIP）。</param>
        /// <param name="offset">当前滚动偏移（向右/向下为正）。</param>
        /// <param name="animate">true 时墨迹以平滑过渡动画跟随滚动；false 时立即贴合。</param>
        public void SetScrollInfo(Size viewport, Size extent, Vector offset, bool animate = true)
        {
            // 非有限偏移（NaN/∞）来自目标应用读数瞬时越界；直接采信会污染 _scrollX/_scrollY，
            // 并把 NaN/∞ 平移烘焙进笔迹数据导致墨迹永久消失。这里整拍丢弃，等下一拍好读数。
            if (double.IsNaN(offset.X) || double.IsInfinity(offset.X)
                || double.IsNaN(offset.Y) || double.IsInfinity(offset.Y))
                return;

            double deltaX = offset.X - _scrollX;
            double deltaY = offset.Y - _scrollY;
            _scrollX = offset.X;
            _scrollY = offset.Y;

            ApplyToScrollBar(_verticalScrollBar, offset.Y, viewport.Height, extent.Height);
            ApplyToScrollBar(_horizontalScrollBar, offset.X, viewport.Width, extent.Width);
            UpdateScrollAssistButtons();

            if (deltaX == 0 && deltaY == 0) return;

            // 内容向下/向右滚，墨迹随之向上/向左平移，保持贴合内容。
            var shift = new Vector(-deltaX, -deltaY);

            // 位移低于阈值不值得起补间（见 ScrollAnimMinShiftDip）：
            // 补间是 60fps 的「全量笔迹 Transform + 全屏重绘」，微增量直接贴合更划算。
            bool animateThisShift = animate
                && Math.Abs(shift.X) + Math.Abs(shift.Y) >= ScrollAnimMinShiftDip;

            if (!animateThisShift || _snapNextScrollSync)
            {
                _snapNextScrollSync = false;
                StopScrollAnimation();
                var immediate = _pendingShift + shift;
                _pendingShift = default;
                ApplyShift(immediate);
                return;
            }

            _pendingShift += shift;
            StartScrollAnimation();
        }

        /// <summary>开启新的滚动会话：下一次 SetScrollInfo 直接贴合（不动画），并清空未烘焙位移。</summary>
        public void BeginScrollSession()
        {
            StopScrollAnimation();
            _snapNextScrollSync = true;
            if (_pendingShift.X != 0 || _pendingShift.Y != 0)
            {
                ApplyShift(_pendingShift);
                _pendingShift = default;
            }
        }

        /// <summary>
        /// 重置滚动帧：清空已应用偏移记忆（置零）并令下一次 SetScrollInfo 直接贴合。
        /// 用于同窗口切换文档：新文档的内容坐标系与旧帧无关，若沿用旧帧偏移，
        /// 首轮滚动同步会把载入的「内容锚定」墨迹平移残留偏移而错位。
        /// </summary>
        public void ResetScrollFrame()
        {
            BeginScrollSession();
            _scrollX = 0;
            _scrollY = 0;
            // 坐标系回到新文档原点：渐隐层（若有残留的渐隐视觉）也要回到原点，
            // 否则它会带着旧文档的滚动偏移继续漂浮。
            _fadeLayerMatrix = Matrix.Identity;
            _fadeLayerTransform.Matrix = _fadeLayerMatrix;
            // 竖直条用视口高、水平条用视口宽；extent 取视口本身（范围归零）。
            // 原实现给竖直条传了 _viewportW、extent 硬编码 1，会让竖直条的 ViewportSize
            // 与实际视口不符、Maximum 恒为 0，辅助滚动按钮的可用状态随之错乱。
            ApplyToScrollBar(_verticalScrollBar, 0, _viewportH, _viewportH);
            ApplyToScrollBar(_horizontalScrollBar, 0, _viewportW, _viewportW);
        }

        /// <summary>当前已应用的滚动逻辑偏移（DIP；长截屏等需对齐墨迹位置的场景读取）。</summary>
        public Vector AppliedScrollOffset => new Vector(_scrollX, _scrollY);

        /// <summary>视口尺寸（DIP）。</summary>
        public Size ViewportSize => new Size(_viewportW, _viewportH);

        /// <summary>
        /// 进入无边框截图模式：隐藏边框、提示条与所有辅助按钮（墨迹保留可见），
        /// 供长截屏截取干净的墨迹区域。结束后调用 <see cref="EndChromelessCapture"/> 还原。
        /// </summary>
        public void BeginChromelessCapture()
        {
            if (_inChromelessCapture) return;
            _inChromelessCapture = true;
            _borderBrushBackup = _contentHost.BorderBrush;
            _contentHost.BorderBrush = Brushes.Transparent;
            _noticeBar.Visibility = Visibility.Collapsed;
            UpdateOverlayVisibility();
        }

        /// <summary>退出无边框截图模式，还原边框与控件可见性。</summary>
        public void EndChromelessCapture()
        {
            if (!_inChromelessCapture) return;
            _inChromelessCapture = false;
            _contentHost.BorderBrush = _borderBrushBackup;
            UpdateOverlayVisibility();
        }

        private Brush _borderBrushBackup;

        /// <summary>立即应用尚未烘焙的滚动位移（保存墨迹/解除绑定前调用，保证坐标一致）。</summary>
        public void FlushScrollAnimation()
        {
            if (!_scrollAnimating && _pendingShift.X == 0 && _pendingShift.Y == 0) return;
            StopScrollAnimation();
            ApplyShift(_pendingShift);
            _pendingShift = default;
        }

        private void StartScrollAnimation()
        {
            _animSegmentStart = _pendingShift;
            _animSegmentApplied = default;
            _animStartTimeUtc = DateTime.UtcNow;
            if (!_scrollAnimating)
            {
                _scrollAnimating = true;
                CompositionTarget.Rendering += OnScrollAnimationFrame;
            }
        }

        private void StopScrollAnimation()
        {
            if (!_scrollAnimating) return;
            _scrollAnimating = false;
            CompositionTarget.Rendering -= OnScrollAnimationFrame;
        }

        private void OnScrollAnimationFrame(object sender, EventArgs e)
        {
            double t = (DateTime.UtcNow - _animStartTimeUtc) / ScrollAnimDuration;
            if (t >= 1)
            {
                // 收尾：把本段剩余位移全部烘焙进笔迹数据。
                StopScrollAnimation();
                ApplyShift(_pendingShift);
                _pendingShift = default;
                return;
            }

            double eased = 1 - Math.Pow(1 - t, 3); // EaseOutCubic
            var appliedTarget = new Vector(_animSegmentStart.X * eased, _animSegmentStart.Y * eased);
            var delta = appliedTarget - _animSegmentApplied;
            if (delta.X != 0 || delta.Y != 0)
            {
                ApplyShift(delta);
                _pendingShift -= delta;
                _animSegmentApplied = appliedTarget;
            }
        }

        /// <summary>把位移真实应用到全部笔迹（数据坐标 == 视觉坐标，橡皮擦/选择无需换算）。</summary>
        private void ApplyShift(Vector shift)
        {
            if (shift.X == 0 && shift.Y == 0) return;
            ApplyInkTransform(new Matrix(1, 0, 0, 1, shift.X, shift.Y));
        }

        /// <summary>
        /// 对全部笔迹与渐隐层应用一次仿射增量变换。
        /// <para>
        /// 这是本画布「笔迹数据坐标 == 视觉坐标」不变式的唯一入口：滚动同步传平移矩阵；
        /// 派生宿主（内置图片查看器）传缩放/旋转矩阵。因为变换被真实烘焙进笔迹点集，
        /// 橡皮擦命中测试、框选、坐标换算全都不需要知道视图变换的存在。
        /// </para>
        /// <para>
        /// <paramref name="matrix"/> 是<b>视口坐标系里的增量</b>（左乘新变换、右乘旧变换的
        /// 组合顺序已在调用方算好），因此这里直接以「先已有、后增量」的顺序累乘。
        /// </para>
        /// </summary>
        protected void ApplyInkTransform(Matrix matrix)
        {
            // 非有限矩阵（含 NaN/∞）会把笔迹点集腐蚀成 NaN —— 一旦烘焙进 Stroke 数据便无法
            // 渲染、也无法恢复，表现为「整块墨迹凭空消失」。滚动/缩放读数瞬时越界都可能
            // 产生这种矩阵，这里作为唯一入口兜底丢弃，宁可不移动也不腐蚀笔迹。
            if (!IsFinite(matrix)) return;

            // 渐隐视觉与笔迹同坐标系，必须同步变换，否则渐隐中的笔迹会与墨迹错位。
            _fadeLayerMatrix = _fadeLayerMatrix * matrix;
            _fadeLayerTransform.Matrix = _fadeLayerMatrix;

            if (_inkCanvas.Strokes.Count == 0) return;

            foreach (Stroke stroke in _inkCanvas.Strokes)
                stroke.Transform(matrix, false);
        }

        /// <summary>矩阵各分量（含偏移）是否均为有限值。</summary>
        private static bool IsFinite(Matrix m)
            => !double.IsNaN(m.M11) && !double.IsInfinity(m.M11)
            && !double.IsNaN(m.M12) && !double.IsInfinity(m.M12)
            && !double.IsNaN(m.M21) && !double.IsInfinity(m.M21)
            && !double.IsNaN(m.M22) && !double.IsInfinity(m.M22)
            && !double.IsNaN(m.OffsetX) && !double.IsInfinity(m.OffsetX)
            && !double.IsNaN(m.OffsetY) && !double.IsInfinity(m.OffsetY);

        /// <summary>
        /// 背景层（图片底图等）：位于墨迹之下、与墨迹同坐标系。
        /// <para>
        /// 基类保持为空且不接收命中测试。派生宿主往 <c>Children</c> 里放内容，
        /// 并用 <see cref="ApplyInkTransform"/> 的同一次变换同步它，即可让底图与墨迹
        /// 始终贴合（图片查看器的缩放/旋转/平移就是这样实现的）。
        /// </para>
        /// </summary>
        protected System.Windows.Controls.Canvas BackgroundLayer => _backgroundLayer;

        /// <summary>
        /// 覆盖层（**不随视图变换**的屏幕空间装饰宿主）：位于视口之上、与视口同坐标系，
        /// 且不接收命中测试。
        /// <para>
        /// 与 <see cref="BackgroundLayer"/> 的区别：背景层在墨迹之下且与墨迹共享坐标变换；
        /// 覆盖层在墨迹之上且<b>不参与变换</b>。内置图片查看器用它承载橡皮擦擦头足迹 ——
        /// 擦头必须跟着指针走，不能被图片的缩放/旋转带着跑。
        /// </para>
        /// </summary>
        protected System.Windows.Controls.Canvas OverlayLayer => _overlayLayer;

        /// <summary>
        /// 渐隐层当前累积的变换矩阵（供派生宿主把底图设成同一变换）。
        /// </summary>
        protected Matrix InkTransformMatrix => _fadeLayerMatrix;

        /// <summary>
        /// 把渐隐层的累积变换归零，<b>但不移动任何笔迹</b>。
        /// <para>
        /// 派生宿主在「整体替换内容」（如图片查看器换图）时使用：调用方随后会自行把笔迹
        /// 集合换成属于新内容的那些，因此这里不能也不需要去平移旧笔迹；但渐隐层的累积
        /// 变换必须与宿主新设的绝对变换同时归零，否则二者会永久错位。
        /// </para>
        /// </summary>
        protected void ResetInkTransformWithoutMovingStrokes()
        {
            _fadeLayerMatrix = Matrix.Identity;
            _fadeLayerTransform.Matrix = _fadeLayerMatrix;
            _pendingShift = default;
            StopScrollAnimation();
        }

        private static void ApplyToScrollBar(ScrollBar bar, double value, double viewportSize, double extentSize)
        {
            double max = extentSize - viewportSize;
            if (double.IsNaN(max) || double.IsInfinity(max)) max = 0;
            max = Math.Max(0, max);

            bar.ViewportSize = Math.Max(0, viewportSize);
            bar.Maximum = max;
            bar.Value = Math.Min(Math.Max(0, value), max);
        }

        /// <summary>
        /// 裁剪内容宿主（墨迹、边框）的被遮挡区域。
        /// <para>
        /// 同时设置 <see cref="UIElement.Clip"/> 与 <see cref="UIElement.OpacityMask"/>：
        /// OpacityMask 负责把遮挡区域渲染为透明（视觉隐藏）；Clip 除渲染裁剪外还会裁剪
        /// 命中测试，使遮挡区域的书写/擦除输入穿透到下层主画布（非独立画布）——
        /// 用户正对着的是盖在目标窗口之上的那层窗口，批注应落到主画布而不是 Word 联动层。
        /// </para>
        /// 传 null 同时清除两者，恢复 <c>ClipToBounds</c> 的默认行为。
        /// </summary>
        public void SetClip(Geometry clip)
        {
            if (clip == null)
            {
                if (_contentHost.OpacityMask != null) _contentHost.OpacityMask = null;
                if (_contentHost.Clip != null) _contentHost.Clip = null;
                return;
            }

            // 白色几何 = 可见区域，其余透明 = 遮挡区域；几何坐标即内容宿主本地坐标。
            _contentHost.Clip = clip;
            _contentHost.OpacityMask = new DrawingBrush
            {
                Drawing = new GeometryDrawing { Geometry = clip, Brush = Brushes.White },
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
        }

        /// <summary>清空独立画布的撤销/恢复历史（加载文档墨迹后调用）。</summary>
        public void ResetInkHistory()
        {
            _history.Clear();
        }

        /// <summary>
        /// 释放本画布持有的订阅。宿主把实例解绑（移出视觉树）时必须调用一次：
        /// 墨迹撤销历史会把自己挂到每条 <see cref="Stroke.StylusPointsChanged"/> 上，
        /// 不释放会让历史笔迹继续持有观察者与点集快照。幂等，可重复调用。
        /// </summary>
        public void ReleaseSubscriptions()
        {
            _history.Dispose();
        }

        /// <summary>本画布是否还有可撤销的墨迹操作（供宿主快捷键分派）。</summary>
        public bool InkHistoryCanUndo => _history.CanUndo;

        /// <summary>本画布是否还有可恢复的墨迹操作（供宿主快捷键分派）。</summary>
        public bool InkHistoryCanRedo => _history.CanRedo;

        /// <summary>撤销本画布最近一次墨迹操作（不影响主画布）。</summary>
        public void InkHistoryUndo() => _history.Undo();

        /// <summary>恢复本画布最近一次被撤销的墨迹操作（不影响主画布）。</summary>
        public void InkHistoryRedo() => _history.Redo();

        /// <summary>
        /// 用 <paramref name="replacements"/> 替换 <paramref name="removed"/>（如收笔后的平滑/美化），
        /// 走 <see cref="StrokeCollection.Replace(StrokeCollection, StrokeCollection)"/> 的单次事件，
        /// 因此在本画布撤销栈里只占一步——否则平滑会变成「撤销两次才回到书写前」。
        /// </summary>
        public void ReplaceStrokes(StrokeCollection removed, StrokeCollection replacements)
        {
            if (_inkCanvas == null) return;
            if (replacements == null || replacements.Count == 0) return;

            if (removed == null || removed.Count == 0)
            {
                _inkCanvas.Strokes.Add(replacements);
                return;
            }

            _inkCanvas.Strokes.Replace(removed, replacements);
        }

        /// <summary>
        /// 清空本画布的全部墨迹（「清空画布」语义，与主画布 <c>inkCanvas.Strokes.Clear()</c> 一致）。
        /// <para>
        /// 注意不要只清「当前视口」：滚动同步是把整个笔迹集合 Transform 平移实现的，
        /// 视口外的笔迹依然属于本画布，只清视口会让用户滚动目标文档后看到旧批注「复活」。
        /// 清空动作由 <see cref="IndependentInkHistory"/> 自动记录，可在本画布内撤销恢复。
        /// </para>
        /// </summary>
        public void ClearAllStrokes()
        {
            if (_inkCanvas.Strokes.Count == 0) return;
            _inkCanvas.Strokes.Clear();
        }

        /// <summary>
        /// 静默移除笔迹：只改墨迹集合，<b>不写入本画布撤销历史</b>。
        /// <para>
        /// 用于「笔迹本就不该成为永久内容」的场景（激光笔/墨迹渐隐：已交给
        /// <see cref="Helpers.InkFadeManager"/> 做视觉渐隐），与主画布
        /// <c>ProcessCommittedStroke</c> 里 <c>inkCanvas.Strokes.Remove(e.Stroke)</c> 同义；
        /// 但额外避免在独立画布撤销栈里留下一条「删除」，否则用户按撤销会把
        /// 已经渐隐消失的激光笔迹重新贴回画布。
        /// </para>
        /// </summary>
        public void RemoveStrokesSilently(System.Collections.Generic.IEnumerable<Stroke> strokes)
        {
            if (strokes == null) return;
            var toRemove = new StrokeCollection(strokes.Where(s => _inkCanvas.Strokes.Contains(s)));
            if (toRemove.Count == 0) return;

            using (_history.SuppressRecording())
                _inkCanvas.Strokes.Remove(toRemove);
        }

        /// <summary>
        /// 遗忘某条笔迹的「新增」撤销记录。
        /// <para>
        /// 配合 <see cref="RemoveStrokesSilently"/>：WPF 收集笔迹时会先同步写入
        /// 集合（撤销栈随即记下一条「新增」），静默移除只保证不产生「删除」记录，
        /// 若不同时抹掉这条「新增」，用户撤销时会执行一次「移除」而看不到任何变化。
        /// </para>
        /// </summary>
        public void ForgetStrokeAddition(Stroke stroke)
        {
            if (stroke == null) return;
            _history.ForgetAdditionOf(stroke);
        }

        /// <summary>
        /// 清空当前可视区域内的墨迹（可见区域即本画布当前视口），保留视口之外的墨迹。
        /// 因滚动同步通过 stroke.Transform 令墨迹始终处于「当前视口坐标」，故只需与
        /// (0,0,视口宽,视口高) 求交即可。清除动作进入本画布撤销历史，可撤销恢复。
        /// </summary>
        /// <remarks>
        /// 「清空画布」按钮已改用 <see cref="ClearAllStrokes"/>；本方法保留给
        /// 「只擦掉眼前这些」这类需要按视口裁剪的场景。
        /// </remarks>
        public void ClearVisibleRegionStrokes()
        {
            double w = _inkCanvas.ActualWidth > 0 ? _inkCanvas.ActualWidth : _inkCanvas.Width;
            double h = _inkCanvas.ActualHeight > 0 ? _inkCanvas.ActualHeight : _inkCanvas.Height;
            if (double.IsNaN(w) || w <= 0) w = _contentHost.ActualWidth;
            if (double.IsNaN(h) || h <= 0) h = _contentHost.ActualHeight;

            var viewport = new Rect(0, 0, Math.Max(1, w), Math.Max(1, h));
            var toRemove = new StrokeCollection(_inkCanvas.Strokes.Where(s => s.GetBounds().IntersectsWith(viewport)));
            if (toRemove.Count > 0)
                _inkCanvas.Strokes.Remove(toRemove);
        }

        private void UpdateInkButtonsState()
        {
            _undoButton.IsEnabled = _history.CanUndo;
            _redoButton.IsEnabled = _history.CanRedo;
            _undoButton.Opacity = _history.CanUndo ? 1.0 : 0.4;
            _redoButton.Opacity = _history.CanRedo ? 1.0 : 0.4;
        }

        /// <summary>
        /// 一条可撤销操作。两种形态互斥：
        /// <list type="bullet">
        /// <item>集合增删（书写、擦除、清空、形状纠正的替换）—— <see cref="Added"/>/<see cref="Removed"/> 非空；</item>
        /// <item>几何变更（框选拖动/缩放笔迹）—— <see cref="PointStroke"/> 非空，
        /// 且 <see cref="BeforePoints"/>/<see cref="AfterPoints"/> 为同一笔迹的前后点集。</item>
        /// </list>
        /// </summary>
        private sealed class InkOperation
        {
            private InkOperation() { }

            public StrokeCollection Added { get; private set; }
            public StrokeCollection Removed { get; private set; }

            public Stroke PointStroke { get; private set; }
            public StylusPointCollection BeforePoints { get; private set; }
            public StylusPointCollection AfterPoints { get; set; }

            public bool IsPointsOperation => PointStroke != null;

            public static InkOperation ForCollection(StrokeCollection added, StrokeCollection removed)
                => new InkOperation
                {
                    Added = added ?? new StrokeCollection(),
                    Removed = removed ?? new StrokeCollection()
                };

            public static InkOperation ForPoints(
                Stroke stroke, StylusPointCollection before, StylusPointCollection after)
                => new InkOperation
                {
                    PointStroke = stroke,
                    BeforePoints = before,
                    AfterPoints = after
                };
        }

        /// <summary>
        /// 独立画布专用的轻量墨迹撤销/恢复历史。记录两类操作，都只作用于本画布，
        /// 不影响主画布 <c>TimeMachine</c>：
        /// <list type="number">
        /// <item><b>集合增删</b>：监听 <c>StrokesChanged</c>（书写、擦除、清空、形状/平滑的 Replace）。</item>
        /// <item><b>几何变更</b>：框选工具的拖动/缩放是直接改 <c>Stroke.StylusPoints</c>，
        /// <b>不触发</b> <c>StrokesChanged</c>，因此必须额外监听每条笔迹的
        /// <c>StylusPointsChanged</c>。<c>Stroke.Transform</c>（滚动同步用）同样不触发该事件，
        /// 所以滚动不会污染历史 —— 这一点是刻意的，不能被「补上 StylusPointsChanged」
        /// 之外的任何做法破坏（见 ApplyShift）。</item>
        /// </list>
        /// <para>
        /// 拖动过程中逐帧产生的点集变化会<b>就地合并</b>成一条记录（更新最新一条的
        /// <c>AfterPoints</c>），因此一次拖拽按一次撤销回退，不会产生上百条历史。
        /// </para>
        /// </summary>
        private sealed class IndependentInkHistory : IDisposable
        {
            private const int MaxHistoryCount = 200;

            private readonly InkCanvas _surface;
            private readonly System.Collections.Generic.List<InkOperation> _undo
                = new System.Collections.Generic.List<InkOperation>();
            private readonly System.Collections.Generic.List<InkOperation> _redo
                = new System.Collections.Generic.List<InkOperation>();

            /// <summary>每条笔迹在「当前这次拖拽」开始前的点集（几何操作基线）。</summary>
            private readonly System.Collections.Generic.Dictionary<Stroke, StylusPointCollection> _dragBaseline
                = new System.Collections.Generic.Dictionary<Stroke, StylusPointCollection>();

            private bool _applying;
            private bool _suppressed;
            private bool _disposed;

            public event Action HistoryChanged;

            public IndependentInkHistory(InkCanvas surface)
            {
                _surface = surface;
                surface.Strokes.StrokesChanged += OnStrokesChanged;

                // 构造时画布可能已有墨迹（载入历史批注）：补齐订阅，否则这些笔迹的
                // 框选移动不会被记录。注意此处必须处于「不记录」语义下，
                // 建立基线本身不算一次操作。
                foreach (Stroke stroke in surface.Strokes) AttachStroke(stroke);
            }

            public bool CanUndo => _undo.Count > 0;
            public bool CanRedo => _redo.Count > 0;

            private void OnStrokesChanged(object sender, StrokeCollectionChangedEventArgs e)
            {
                if (_disposed) return;

                // 订阅维护必须无条件执行（即使处于 _applying/_suppressed），
                // 否则撤销回放增删笔迹后，后续的框选移动会漏记。
                if (e?.Removed != null)
                {
                    foreach (Stroke stroke in e.Removed)
                    {
                        DetachStroke(stroke);
                        _dragBaseline.Remove(stroke);
                    }
                }

                if (e?.Added != null)
                {
                    foreach (Stroke stroke in e.Added)
                    {
                        AttachStroke(stroke);
                        // 建立几何基线：笔迹刚入集合时记下点集，之后框选拖动才有「拖动前」可比。
                        // 对书写产生的笔迹，此处记录的就是原始点集；收笔后的平滑/形状纠正会走
                        // Strokes.Replace（先移除再新增），新增的那条会重新建立基线，因此
                        // 基线始终对应当前在集合中的那个对象。
                        if (stroke != null && !_dragBaseline.ContainsKey(stroke))
                            _dragBaseline[stroke] = stroke.StylusPoints.Clone();
                    }
                }

                if (_applying || _suppressed) return;
                if ((e.Added?.Count ?? 0) == 0 && (e.Removed?.Count ?? 0) == 0) return;

                PushOperation(InkOperation.ForCollection(
                    e.Added != null ? new StrokeCollection(e.Added) : new StrokeCollection(),
                    e.Removed != null ? new StrokeCollection(e.Removed) : new StrokeCollection()));
            }

            private void AttachStroke(Stroke stroke)
            {
                if (stroke == null) return;
                stroke.StylusPointsChanged -= OnStrokePointsChanged;
                stroke.StylusPointsChanged += OnStrokePointsChanged;
            }

            private void DetachStroke(Stroke stroke)
            {
                if (stroke == null) return;
                stroke.StylusPointsChanged -= OnStrokePointsChanged;
            }

            /// <summary>
            /// 笔迹点集变化 = 框选工具在改几何（书写产生的笔迹是「先入集合、后不再变点集」，
            /// 滚动同步走 <c>Transform</c> 不触发本事件）。
            /// </summary>
            private void OnStrokePointsChanged(object sender, EventArgs e)
            {
                if (_disposed || _applying || _suppressed) return;
                if (sender is not Stroke stroke) return;
                if (!_surface.Strokes.Contains(stroke)) return;

                if (!_dragBaseline.TryGetValue(stroke, out var baseline))
                {
                    // 第一次见到这条笔迹变点集：此刻的「当前点集」已经是变化后的值，
                    // 无法还原真正的初始值，因此退化为「本次变化前不可知」——
                    // 用当前值作基线会让这次拖动不可撤销。为避免这种情况，
                    // 基线在笔迹入集合时就已建立（见 AttachStroke + _dragBaseline 初始化）。
                    return;
                }

                var after = stroke.StylusPoints.Clone();

                // 合并：同一条笔迹在连续变化中只占一条记录，持续刷新 AfterPoints。
                if (_undo.Count > 0)
                {
                    var top = _undo[_undo.Count - 1];
                    if (top.IsPointsOperation && ReferenceEquals(top.PointStroke, stroke))
                    {
                        top.AfterPoints = after;
                        _redo.Clear();
                        HistoryChanged?.Invoke();
                        return;
                    }
                }

                PushOperation(InkOperation.ForPoints(stroke, baseline.Clone(), after));

                // 本次拖拽已归档：把基线推进到「本段结束」，下一次独立拖动会开启新记录。
                _dragBaseline[stroke] = after.Clone();
            }

            private void PushOperation(InkOperation op)
            {
                if (op == null) return;
                _undo.Add(op);
                if (_undo.Count > MaxHistoryCount)
                    _undo.RemoveAt(0);
                _redo.Clear();
                HistoryChanged?.Invoke();
            }

            public void Undo()
            {
                if (_undo.Count == 0) return;
                var op = _undo[_undo.Count - 1];
                _undo.RemoveAt(_undo.Count - 1);

                _applying = true;
                try
                {
                    if (op.IsPointsOperation)
                    {
                        ApplyPoints(op.PointStroke, op.BeforePoints);
                    }
                    else
                    {
                        if (op.Added.Count > 0) _surface.Strokes.Remove(op.Added);
                        if (op.Removed.Count > 0) _surface.Strokes.Add(op.Removed);
                    }

                    _redo.Add(op);
                }
                finally
                {
                    _applying = false;
                }

                // 撤销后基线重置为「当前实际状态」，下一次拖动从正确位置起算。
                ResetBaselines();
                HistoryChanged?.Invoke();
            }

            public void Redo()
            {
                if (_redo.Count == 0) return;
                var op = _redo[_redo.Count - 1];
                _redo.RemoveAt(_redo.Count - 1);

                _applying = true;
                try
                {
                    if (op.IsPointsOperation)
                    {
                        ApplyPoints(op.PointStroke, op.AfterPoints);
                    }
                    else
                    {
                        if (op.Removed.Count > 0) _surface.Strokes.Remove(op.Removed);
                        if (op.Added.Count > 0) _surface.Strokes.Add(op.Added);
                    }

                    _undo.Add(op);
                }
                finally
                {
                    _applying = false;
                }

                ResetBaselines();
                HistoryChanged?.Invoke();
            }

            /// <summary>把点集写回笔迹（仅在 <c>_applying</c> 保护下调用，避免自我记录）。</summary>
            private void ApplyPoints(Stroke stroke, StylusPointCollection points)
            {
                if (stroke == null || points == null) return;
                if (!_surface.Strokes.Contains(stroke)) return;
                stroke.StylusPoints = points.Clone();
            }

            /// <summary>把所有在集合中的笔迹基线重置为当前点集。</summary>
            private void ResetBaselines()
            {
                _dragBaseline.Clear();
                foreach (Stroke stroke in _surface.Strokes)
                {
                    if (stroke == null) continue;
                    _dragBaseline[stroke] = stroke.StylusPoints.Clone();
                }
            }

            public void Clear()
            {
                _dragBaseline.Clear();
                if (_undo.Count == 0 && _redo.Count == 0) return;
                _undo.Clear();
                _redo.Clear();
                HistoryChanged?.Invoke();
            }

            /// <summary>
            /// 在 using 作用域内暂停历史记录（该范围内对墨迹集合的增删都不进撤销栈）。
            /// </summary>
            public IDisposable SuppressRecording() => new RecordingScope(this);

            /// <summary>抹掉某条笔迹对应的「新增」记录（用于渐隐笔迹等不应进入撤销栈的笔迹）。</summary>
            public void ForgetAdditionOf(Stroke stroke)
            {
                if (stroke == null) return;
                var changed = false;
                for (var i = _undo.Count - 1; i >= 0; i--)
                {
                    var op = _undo[i];
                    if (op.IsPointsOperation) continue;
                    var isSingleAdd = op.Added.Count == 1 && ReferenceEquals(op.Added[0], stroke)
                                      && op.Removed.Count == 0;
                    if (!isSingleAdd) continue;
                    _undo.RemoveAt(i);
                    changed = true;
                    break;
                }

                if (changed) HistoryChanged?.Invoke();
            }

            private sealed class RecordingScope : IDisposable
            {
                private readonly IndependentInkHistory _owner;
                private readonly bool _previous;

                public RecordingScope(IndependentInkHistory owner)
                {
                    _owner = owner;
                    _previous = owner._suppressed;
                    owner._suppressed = true;
                }

                public void Dispose() => _owner._suppressed = _previous;
            }

            /// <summary>
            /// 解除全部订阅。本画布一旦被移除（解绑、切文档）就不再需要继续观察墨迹，
            /// 但 <see cref="Stroke.StylusPointsChanged"/> 是「笔迹 → 本对象」的引用，
            /// 不解绑会让每一条历史笔迹都继续持有本观察者。
            /// </summary>
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                try { _surface.Strokes.StrokesChanged -= OnStrokesChanged; }
                catch { /* 集合已失效，忽略 */ }

                foreach (var stroke in _dragBaseline.Keys) DetachStroke(stroke);
                foreach (Stroke stroke in _surface.Strokes) DetachStroke(stroke);

                _dragBaseline.Clear();
                _undo.Clear();
                _redo.Clear();
            }
        }
    }
}
