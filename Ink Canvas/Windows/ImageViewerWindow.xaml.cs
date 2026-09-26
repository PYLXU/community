using Ink_Canvas.Controls;
using Ink_Canvas.Helpers;
using Ink_Canvas.Properties;
using Ink_Canvas.Windows.SettingsViews.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
// 只别名引入需要的形状类型：直接 using System.Windows.Shapes 会让 Path 与 System.IO.Path 冲突，
// 而 XAML 里那个 ResizeGrip（Path）字段在生成代码中是全限定名，不需要 using。
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Ink_Canvas.Windows
{
    /// <summary>
    /// 内置图片查看器窗口：一块「以图片为背景的独立画布」+ 底部扩展栏。
    /// <para>
    /// 关键设计：
    /// </para>
    /// <list type="bullet">
    /// <item><b>无系统边框</b>：<c>WindowStyle=None</c> + 自绘底栏。移动/缩放/吸附全部交给
    /// Windows 自己完成 —— 在 <c>WM_NCHITTEST</c> 里把「底栏空白处 / 图片之外的留白」判为
    /// <c>HTCAPTION</c>、把边框判为 <c>HTLEFT/HTRIGHT/…</c>，于是 Aero Snap
    /// （顶部→最大化、左/右→半屏分屏）、贴边不越界、双击标题栏最大化这些系统行为全都免费得到。</item>
    /// <item><b>不超出屏幕</b>：<c>WM_GETMINMAXINFO</c> 把最大化边界限制到显示器工作区、
    /// 把最大可拖拽尺寸限制到显示器范围。</item>
    /// <item><b>任务栏</b>：每个窗口是一个独立顶层窗口且 <c>ShowInTaskbar=true</c>，
    /// 因此任务栏会为每个窗口显示实时缩略图预览、点击即分别唤起到前台；
    /// 缩略图用 <c>ThumbnailClipMargin</c> 裁掉底栏，只显示图片区域。</item>
    /// <item><b>多实例</b>：实例登记在 <see cref="ImageViewerManager"/>，每张图片一个窗口。</item>
    /// </list>
    /// </summary>
    public partial class ImageViewerWindow : Window
    {
        // ---------------- Win32 常量 ----------------

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_DPICHANGED = 0x02E0;

        /// <summary><c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>：让 DWM 按深色绘制本窗口的边框/装饰。</summary>
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        /// <summary>同上的旧编号（Windows 10 1809~1909）。</summary>
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        /// <summary>
        /// 可拖拽/可缩放的边框命中宽度（DIP）。
        /// 取 10 而不是鼠标习惯的 5~7：触摸屏上手指按不准，边框太窄会变成「一拖就移动窗口」。
        /// </summary>
        private const double ResizeBorderThickness = 10.0;

        /// <summary>窗口边框补偿用到的 GetSystemMetrics 索引（实测不可用时的回退）。</summary>
        private const int SM_CXSIZEFRAME = 32;
        private const int SM_CYSIZEFRAME = 33;
        private const int SM_CXPADDEDBORDER = 92;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>
        /// 取窗口那圈**不可见的** <c>WS_THICKFRAME</c> 边框厚度（物理像素）。
        /// <para>
        /// 优先用「当前窗口矩形 − 客户区矩形」实测：DPI 缩放、主题、Windows 版本都会让
        /// <c>SM_CXSIZEFRAME + SM_CXPADDEDBORDER</c> 的换算对不上（这台机器上算出来是 8，实测是 7），
        /// 只有实测值能让最大化后的可见区域正好等于工作区。实测拿不到时再退回系统度量。
        /// </para>
        /// </summary>
        private static void GetFrameThickness(IntPtr hwnd, out int frameX, out int frameY)
        {
            if (hwnd != IntPtr.Zero
                && GetWindowRect(hwnd, out var windowRect)
                && GetClientRect(hwnd, out var clientRect))
            {
                int x = ((windowRect.Right - windowRect.Left) - clientRect.Right) / 2;
                int y = ((windowRect.Bottom - windowRect.Top) - clientRect.Bottom) / 2;
                // 注意要接受 0：处理了 WM_NCCALCSIZE 之后客户区已铺满整个窗口，
                // 「窗口矩形 == 客户区矩形」正是我们想要的状态，此时补偿量必须是 0。
                if (x >= 0 && y >= 0 && x < 64 && y < 64)
                {
                    frameX = x;
                    frameY = y;
                    return;
                }
            }

            int pad = Math.Max(0, GetSystemMetrics(SM_CXPADDEDBORDER));
            frameX = Math.Max(0, GetSystemMetrics(SM_CXSIZEFRAME)) + pad;
            frameY = Math.Max(0, GetSystemMetrics(SM_CYSIZEFRAME)) + pad;
        }

        /// <summary>
        /// 让客户区铺满整个窗口，取消那圈非客户区边框。
        /// <para>
        /// 症状：**「标题栏上方多出一条白色边框」**。
        /// 原因：本窗口是 <c>WindowStyle=None</c>（没有标题栏）却带 <c>WS_THICKFRAME</c>，
        /// Windows 因此留了一圈约 7px 的非客户区；没有标题栏就没有任何内容去绘制这一圈，
        /// 系统便按**系统主题**给它涂色 —— 浅色主题下就是近白色
        /// （实测该色带与窗口后面是什么完全无关，移动窗口也不变，所以不是"桌面漏进来"）。
        /// </para>
        /// <para>
        /// 这里直接把这圈取消：客户区 == 窗口矩形，也就没有"该涂什么颜色"的问题。
        /// 附带好处是最大化/全屏的可见区域正好等于目标矩形（边框补偿实测为 0）。
        /// 缩放、边缘吸附、最大化边界都不受影响 —— 它们走 WM_NCHITTEST / WM_GETMINMAXINFO。
        /// </para>
        /// <para>
        /// 注意：本消息只改「客户区算多大」，本身不产生 <c>WM_SIZE</c>，
        /// 所以还需要 <see cref="ForceClientAreaRelayout"/> 让 WPF 的根视觉跟着重排。
        /// </para>
        /// </summary>
        private static IntPtr HandleNonClientCalcSize(IntPtr wParam, ref bool handled)
        {
            // wParam != 0 表示"要重新计算"，返回 0 即声明客户区等于整个窗口。
            if (wParam == IntPtr.Zero) return IntPtr.Zero;

            handled = true;
            return IntPtr.Zero;
        }

        /// <summary>
        /// 触发一次真实的 <c>WM_SIZE</c>，让 WPF 的根视觉按**新的**客户区尺寸重新布局。
        /// <para>
        /// <see cref="HandleNonClientCalcSize"/> 只改客户区的计算方式、并不产生 <c>WM_SIZE</c>；
        /// 不补这一下，WPF 的根视觉会停在旧尺寸上，窗口四周留出一圈未绘制的白边。
        /// 这里把尺寸推 1px 再复原，走的是与用户手动缩放完全相同的代码路径
        /// （两步都在窗口显示完成后的同一轮里，肉眼看不到中间态）。
        /// </para>
        /// </summary>
        private void ForceClientAreaRelayout()
        {
            try
            {
                double width = Width, height = Height;
                if (double.IsNaN(width) || double.IsNaN(height) || width <= 1 || height <= 1) return;

                Width = width + 1;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Width = width;
                    Height = height;
                }), DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        /// <summary>
        /// 让 DWM 知道本窗口是深色的，从而用深色绘制窗口边框/圆角装饰。
        /// <para>
        /// 不设置的话，浅色系统主题下 DWM 会给窗口边框涂上近白色。
        /// 属性编号在 Win10 1809~1909 是 19，Win10 2004+/Win11 是 20，两个都试一遍即可。
        /// </para>
        /// </summary>
        private static void ApplyDarkWindowFrame(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            try
            {
                int enable = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enable, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref enable, sizeof(int));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        // ---------------- 状态 ----------------

        /// <summary>当前显示的图片路径（绝对路径）。</summary>
        private string _imagePath;

        /// <summary>同目录下可浏览的图片（自然排序），供「上一张/下一张」使用。</summary>
        private List<string> _siblings = new List<string>();

        /// <summary>当前图片在 <see cref="_siblings"/> 中的下标；-1 表示不在列表里。</summary>
        private int _siblingIndex = -1;

        private HwndSource _hwndSource;
        private bool _fullScreenActive;
        private WindowState _stateBeforeFullScreen = WindowState.Normal;

        private readonly DispatcherTimer _infoHideTimer;
        private readonly DispatcherTimer _adjustDebounceTimer;
        private bool _adjustReady;
        private bool _closingHandled;

        // ---------------- 构造 ----------------

        public ImageViewerWindow()
        {
            InitializeComponent();

            var settings = ViewerSettings;
            ShowInTaskbar = settings?.ShowInTaskbar ?? true;
            Background = new SolidColorBrush(ImageViewerFormats.ParseColorOr(
                settings?.BackgroundColor, Color.FromRgb(0x1E, 0x1E, 0x1E)));

            ApplyWindowIcon();
            BuildTitleBar();
            BuildBottomBar();
            SetupTaskbarInfo();

            _infoHideTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(6)
            };
            _infoHideTimer.Tick += (_, _) =>
            {
                _infoHideTimer.Stop();
                InfoOverlay.Visibility = Visibility.Collapsed;
            };

            _adjustDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(140)
            };
            _adjustDebounceTimer.Tick += (_, _) =>
            {
                _adjustDebounceTimer.Stop();
                ApplyImageAdjustment();
            };

            ViewerCanvas.ViewChanged += OnCanvasViewChanged;
            // 批注模式（主工具栏驱动）变化时切换鼠标指针，触摸屏/鼠标上都能一眼看出当前能否书写。
            ViewerCanvas.InkSurface.EditingModeChanged += (_, _) => UpdateCanvasCursor();
            AttachInkActivityTracking();

            SourceInitialized += OnSourceInitialized;
            Loaded += OnWindowLoaded;
            SizeChanged += (_, _) => OnLayoutChanged();
            LocationChanged += (_, _) => OnLayoutChanged();
            // 必须同时盯住画布自身的尺寸：底栏高度、DPI、窗口装饰任何一项变化都可能只改画布
            // 而不改窗口尺寸，漏掉就会出现「适应窗口」用了过期视口、图片被裁掉一截。
            ViewerCanvas.SizeChanged += (_, _) => OnLayoutChanged();
            // 擦除手势收尾：补上主画布那侧的「橡皮擦自动切换回批注」。
            ViewerCanvas.PreviewMouseUp += (_, _) => NotifyHostEraserGestureEnded();
            ViewerCanvas.PreviewStylusUp += (_, _) => NotifyHostEraserGestureEnded();
            StateChanged += OnWindowStateChanged;
            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnWindowClosing;
            Closed += OnWindowClosed;

            _adjustReady = true;
        }

        /// <summary>查看器相关设置（每次读取都取最新对象，设置页改动即时生效）。</summary>
        private static Ink_Canvas.ImageViewerSettings ViewerSettings => SettingsManager.Settings.ImageViewer;

        /// <summary>当前显示的图片路径。</summary>
        public string ImagePath => _imagePath;

        /// <summary>
        /// 本窗口的画布（图片底图 + 批注层）。
        /// 宿主的批注状态同步（<c>IndependentCanvasStateSync</c>）会作用到它上面，
        /// 与 UI 联动画布走同一条链路。
        /// </summary>
        internal ImageViewerCanvas Canvas => ViewerCanvas;

        // ---------------- 生命周期 ----------------

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                _hwndSource = (HwndSource)PresentationSource.FromVisual(this);
                _hwndSource?.AddHook(WndProc);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"图片查看器挂接窗口消息失败: {ex.Message}", LogHelper.LogType.Warning);
            }

            ApplyTaskbarIdentity();

            // 深色窗口边框 + 客户区铺满窗口：两者一起才能让「标题栏上方那条白色边框」消失。
            ApplyDarkWindowFrame(_hwndSource?.Handle ?? IntPtr.Zero);
        }

        // ---------------- 图标 / 任务栏身份 ----------------

        /// <summary>
        /// 设置窗口与任务栏图标。
        /// 用「创建快捷方式」那套图标生成器产出的图片查看器专属图标（.ico），
        /// 拿不到就退回程序自身图标，绝不出现无图标窗口。
        /// </summary>
        private void ApplyWindowIcon()
        {
            try
            {
                string iconPath = FileAssociationManager.GetImageViewerIconPath();
                if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
                    // OnLoad：读完即释放文件句柄，之后重新生成图标不会被本进程占用挡住。
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    Icon = bitmap;
                    if (TitleBarIcon != null) TitleBarIcon.Source = bitmap;
                    return;
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"载入图片查看器图标失败: {ex.Message}", LogHelper.LogType.Warning);
            }

            // 回退：沿用程序图标（标题栏也用它）。
            try
            {
                if (TitleBarIcon != null) TitleBarIcon.Source = Icon;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        /// <summary>
        /// 让任务栏把本窗口与其它 ICC 窗口（主界面、设置窗口…）**分开归组**：
        /// 写入窗口级 AppUserModelID + 固定到任务栏时用的名称/图标。
        /// </summary>
        private void ApplyTaskbarIdentity()
        {
            try
            {
                WindowAppIdentity.Apply(
                    this,
                    FileAssociationManager.ImageViewerAppUserModelId,
                    FileAssociationManager.ImageViewerAppDisplayName,
                    FileAssociationManager.GetImageViewerIconPath());
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"设置图片查看器任务栏标识失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        // ---------------- 标题栏 ----------------

        /// <summary>标题栏右侧的最小化/最大化/关闭按钮（左侧图标与标题在 XAML 里）。</summary>
        private void BuildTitleBar()
        {
            // 标题栏按钮不带文字说明（视觉上贴近系统标题栏），所以单独用一个小样式。
            var btnMinimize = CreateTitleBarButton("\uE921", ImageViewerStrings.BarMinimize,
                (_, _) => WindowState = WindowState.Minimized);
            _btnTitleBarMaximize = CreateTitleBarButton("\uE922", ImageViewerStrings.BarMaximize,
                (_, _) => ToggleMaximize());
            var btnClose = CreateTitleBarButton("\uE8BB", ImageViewerStrings.BarClose,
                (_, _) => Close(), useCloseStyle: true);

            TitleBarButtonGroup.Children.Add(btnMinimize);
            TitleBarButtonGroup.Children.Add(_btnTitleBarMaximize);
            TitleBarButtonGroup.Children.Add(btnClose);

            UpdateTitleBarMaximizeGlyph();
        }

        private Button CreateTitleBarButton(
            string glyph, string tooltip, RoutedEventHandler onClick, bool useCloseStyle = false)
        {
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = BarIconFont,
                FontSize = 11,
                Foreground = BarGlyphBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var button = new Button
            {
                Content = icon,
                ToolTip = tooltip,
                Width = 44,
                Height = 32,
                Margin = new Thickness(0),
                Focusable = false,
                IsTabStop = false,
                Cursor = Cursors.Arrow,
                Background = TransparentBrush,
                BorderThickness = new Thickness(0),
                Style = (Style)FindResource(useCloseStyle ? "ViewerTitleBarCloseButtonStyle" : "ViewerTitleBarButtonStyle"),
            };

            AutomationProperties.SetName(button, tooltip);
            AutomationProperties.SetHelpText(button, tooltip);
            button.Click += onClick;
            return button;
        }

        private void UpdateTitleBarMaximizeGlyph()
        {
            if (_btnTitleBarMaximize?.Content is not TextBlock text) return;

            bool maximized = WindowState == WindowState.Maximized;
            text.Text = maximized ? "\uE923" : "\uE922";
            _btnTitleBarMaximize.ToolTip = maximized
                ? ImageViewerStrings.BarRestore
                : ImageViewerStrings.BarMaximize;
        }

        /// <summary>
        /// 接管本窗口画布的墨迹活动记录。
        /// <para>
        /// Ctrl+Z / Ctrl+Y 是 ICC 注册的<b>系统级热键</b>，查看器窗口根本收不到，
        /// 因此必须把「用户在这块画布上落过笔」告诉宿主，宿主才会把撤销分派到这里，
        /// 而不是去撤销白板内容 —— 语义与 UI 联动批注层完全一致。
        /// </para>
        /// </summary>
        private void AttachInkActivityTracking()
        {
            var surface = ViewerCanvas.InkSurface;
            if (surface == null) return;

            // handledEventsToo: true —— 这些事件可能已被其它处理器（InkCanvas 自身）处理掉，
            // 我们只关心「用户是否在这里落过笔」。
            surface.AddHandler(UIElement.StylusDownEvent,
                new StylusDownEventHandler((_, _) => NoteInkActivity()), true);
            surface.AddHandler(UIElement.MouseDownEvent,
                new MouseButtonEventHandler((_, _) => NoteInkActivity()), true);
            surface.AddHandler(UIElement.TouchDownEvent,
                new EventHandler<TouchEventArgs>((_, _) => NoteInkActivity()), true);
        }

        private void NoteInkActivity()
        {
            // 只认「落笔」：不要挂 StrokesChanged —— 缩放/平移会逐帧改笔迹几何，
            // 把它当成活动信号会让用户只是拖了一下图片，Ctrl+Z 就被劫持到本窗口。
            ImageViewerManager.NoteCanvasActivity(ViewerCanvas);
        }

        /// <summary>
        /// 非批注状态下仍需接收输入：图片查看器要靠鼠标拖动与双指手势平移缩放图片。
        /// 不落墨由宿主的 <c>EditingMode = None</c> 保证，而不是靠禁用整块画布。
        /// </summary>
        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            ApplySavedPlacement();

            // 客户区已铺满整个窗口，这里让 WPF 的根视觉跟上新尺寸（否则四周会留一圈未绘制的白边）。
            ForceClientAreaRelayout();

            OnLayoutChanged();
            UpdateInfoOverlay(forceVisible: true);

            // 触摸屏：图像调整面板里的滑杆需要显式接管触摸/手写笔（WPF 滑杆默认按鼠标处理，
            // 手指拖动会变成一次点击而不是连续拖动）。与主窗口用的是同一个辅助类。
            SliderTouchHelper.AddTouchSupport(SliderBrightness);
            SliderTouchHelper.AddTouchSupport(SliderContrast);
            SliderTouchHelper.AddTouchSupport(SliderSaturation);

            UpdateCanvasCursor();
            UpdateInkWritingState(IsHostAnnotating, force: true);

            // 擦头图片资源来自与主画布共用的一份 ResourceDictionary。缺了它擦头框会退化成
            // 「有尺寸没图案」的空白，静默失效很难查，所以启动时检查一次并明确记录。
            if (ViewerCanvas.EraserFootprintResourceAvailable)
            {
                LogHelper.WriteLogToFile("图片查看器：橡皮擦擦头资源已就绪（与主画布同款）", LogHelper.LogType.Event);
            }
            else
            {
                LogHelper.WriteLogToFile(
                    "图片查看器：未找到橡皮擦擦头图片资源（MW_Eraser.xaml 未合并），擦头预览将不显示",
                    LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 按当前编辑模式切换画布指针：可书写/擦除时用十字，其余（鼠标漫游模式）用「可拖动」四向箭头，
        /// 让「空白拖动窗口、图上拖动图片」这一分工在鼠标上也可发现。
        /// </summary>
        private void UpdateCanvasCursor()
        {
            try
            {
                // 内层 InkCanvas 可能被 WPF 按编辑模式重设成笔形指针，这里一并清空，
                // 保证指针始终由本窗口按「当前能不能写」来决定。
                ViewerCanvas.InkSurface.Cursor = null;

                var mode = ViewerCanvas.InkSurface.EditingMode;
                ViewerCanvas.Cursor = mode == InkCanvasEditingMode.None
                    ? Cursors.SizeAll
                    : mode == InkCanvasEditingMode.Select ? Cursors.Arrow : Cursors.Cross;

                if (mode != InkCanvasEditingMode.EraseByPoint)
                    ViewerCanvas.HideEraserFootprint();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        /// <summary>
        /// 查看器里完成一次擦除手势后的收尾。
        /// <para>
        /// 擦除本身走 InkCanvas 原生擦除（擦头形状由 <c>IndependentCanvasStateSync</c>
        /// 从主画布同步，与主画布一致；擦头预览由共用的 <c>InkCanvas.Ink.EraserFootprint</c>
        /// 绘制），这里只补上主画布那侧的「橡皮擦自动切换回批注」，
        /// 保证擦完之后的工具栏行为与在主画布/UI 联动画布上擦完一样。
        /// </para>
        /// </summary>
        private void NotifyHostEraserGestureEnded()
        {
            var mode = ViewerCanvas.InkSurface.EditingMode;
            if (mode != InkCanvasEditingMode.EraseByPoint && mode != InkCanvasEditingMode.EraseByStroke) return;

            try
            {
                HostMainWindow?.HandleEraserOperationEnded();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"图片查看器橡皮擦收尾失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        private void OnWindowStateChanged(object sender, EventArgs e)
        {
            UpdateMaximizeButtonGlyph();
            UpdateTitleBarMaximizeGlyph();
            OnLayoutChanged();
        }

        // ---------------- 打开图片 ----------------

        /// <summary>在当前窗口里载入一张图片（换图：批注面随图片切换）。</summary>
        /// <param name="filePath">图片路径。</param>
        public void LoadImage(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            try
            {
                // 先把上一张图的批注落盘，避免换图时丢失。
                SaveAnnotationsForCurrentImage();

                string fullPath;
                try { fullPath = Path.GetFullPath(filePath); }
                catch { fullPath = filePath; }

                var bitmap = DecodeImage(fullPath);
                if (bitmap == null)
                {
                    ShowLoadFailure(fullPath);
                    return;
                }

                _imagePath = fullPath;
                RefreshSiblingList(fullPath);

                ViewerCanvas.SetImage(bitmap, ViewerSettings?.FitToWindowOnOpen ?? true);

                // 视图矩阵已就绪，可以按图片坐标恢复批注了。
                RestoreAnnotations(fullPath, bitmap);

                ResetAdjustmentSliders();
                UpdateWindowTitle();
                UpdateNavigationButtons();
                UpdateInfoOverlay(forceVisible: true);

                RememberFolder(fullPath);

                LogHelper.WriteLogToFile($"图片查看器已打开: {fullPath}", LogHelper.LogType.Event);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"图片查看器打开图片失败（{filePath}）: {ex.Message}", LogHelper.LogType.Error);
                ShowLoadFailure(filePath);
            }
        }

        /// <summary>解码图片，并按 EXIF 方向把像素转正（手机照片必须，否则会躺着显示）。</summary>
        private static BitmapSource DecodeImage(string path)
        {
            try
            {
                BitmapFrame frame;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    frame = BitmapFrame.Create(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        // OnLoad：立刻读完并释放文件句柄，用户随后可以删除/移动这张图。
                        BitmapCacheOption.OnLoad);
                }

                frame.Freeze();
                return ApplyExifOrientation(frame);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"图片解码失败（{path}）: {ex.Message}", LogHelper.LogType.Warning);
                return null;
            }
        }

        /// <summary>按 EXIF 方向标签（0x0112）把位图转正。</summary>
        private static BitmapSource ApplyExifOrientation(BitmapSource source)
        {
            ushort orientation = 1;
            try
            {
                if (source is BitmapFrame frame && frame.Metadata is BitmapMetadata metadata)
                {
                    object value = metadata.GetQuery("/app1/ifd/{ushort=274}");
                    if (value != null) orientation = Convert.ToUInt16(value);
                }
            }
            catch (Exception ex)
            {
                // 非 JPEG / 无 EXIF：保持原样（1 = 正常方向）。
                System.Diagnostics.Debug.WriteLine(ex);
            }

            if (orientation <= 1 || orientation > 8) return source;

            var transform = new TransformGroup();
            switch (orientation)
            {
                case 2: transform.Children.Add(new ScaleTransform(-1, 1)); break;
                case 3: transform.Children.Add(new RotateTransform(180)); break;
                case 4: transform.Children.Add(new ScaleTransform(1, -1)); break;
                case 5:
                    transform.Children.Add(new ScaleTransform(-1, 1));
                    transform.Children.Add(new RotateTransform(90));
                    break;
                case 6: transform.Children.Add(new RotateTransform(90)); break;
                case 7:
                    transform.Children.Add(new ScaleTransform(-1, 1));
                    transform.Children.Add(new RotateTransform(270));
                    break;
                case 8: transform.Children.Add(new RotateTransform(270)); break;
                default: return source;
            }

            try
            {
                var transformed = new TransformedBitmap(source, transform);
                transformed.Freeze();
                return transformed;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"应用 EXIF 方向失败: {ex.Message}", LogHelper.LogType.Warning);
                return source;
            }
        }

        private void ShowLoadFailure(string path)
        {
            string message = string.Format(ImageViewerStrings.LoadFailedMessage, Path.GetFileName(path));
            LogHelper.WriteLogToFile($"图片查看器: {message}", LogHelper.LogType.Warning);

            try
            {
                MessageBox.Show(this, message, ImageViewerStrings.LoadFailedTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }

            // 一张图都载入不了：窗口没有必要留下。
            if (string.IsNullOrEmpty(_imagePath)) Close();
        }

        // ---------------- 同目录导航 ----------------

        private void RefreshSiblingList(string currentPath)
        {
            _siblings = ImageViewerFormats.EnumerateSiblingImages(currentPath);
            _siblingIndex = _siblings.FindIndex(
                p => string.Equals(p, currentPath, StringComparison.OrdinalIgnoreCase));
        }

        private void Navigate(int offset)
        {
            if (_siblings.Count <= 1 || _siblingIndex < 0) return;

            // 循环浏览：最后一张的「下一张」回到第一张。
            int next = ((_siblingIndex + offset) % _siblings.Count + _siblings.Count) % _siblings.Count;
            LoadImage(_siblings[next]);
        }

        private void UpdateNavigationButtons()
        {
            bool canNavigate = _siblings.Count > 1;
            if (_btnPrevious != null) _btnPrevious.IsEnabled = canNavigate;
            if (_btnNext != null) _btnNext.IsEnabled = canNavigate;
        }

        private void RememberFolder(string imagePath)
        {
            try
            {
                var settings = ViewerSettings;
                if (settings == null) return;

                string folder = Path.GetDirectoryName(imagePath);
                if (string.IsNullOrEmpty(folder)) return;
                if (string.Equals(settings.LastFolder, folder, StringComparison.OrdinalIgnoreCase)) return;

                settings.LastFolder = folder;
                SettingsManager.SaveSettingsToFile();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        // ---------------- 批注持久化 ----------------

        private void SaveAnnotationsForCurrentImage()
        {
            try
            {
                if (string.IsNullOrEmpty(_imagePath)) return;
                if (!(ViewerSettings?.AutoSaveInk ?? false)) return;

                ImageViewerAnnotationStore.Save(
                    _imagePath, ViewerCanvas.Strokes, ViewerCanvas.ViewMatrix);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"保存图片批注失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        private void RestoreAnnotations(string imagePath, BitmapSource bitmap)
        {
            try
            {
                if (!(ViewerSettings?.AutoSaveInk ?? false)) return;

                var strokes = ImageViewerAnnotationStore.Load(imagePath, ViewerCanvas.ViewMatrix);
                if (strokes.Count == 0) return;

                // 用「无声」通道写入，避免把「载入历史批注」记成一步可撤销操作。
                ViewerCanvas.Strokes.Add(strokes);
                ViewerCanvas.ResetInkHistory();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"恢复图片批注失败（{imagePath}）: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        // ---------------- 底部扩展栏 ----------------

        private Button _btnPrevious;
        private Button _btnNext;
        private Button _btnMaximize;
        private Button _btnFullScreen;
        private Button _btnInkWriting;
        private Button _btnTitleBarMaximize;

        /// <summary>「墨迹书写」按钮的图标与文字，用于按批注状态整体染色。</summary>
        private TextBlock _inkWritingGlyph;
        private TextBlock _inkWritingLabel;

        /// <summary>上一次已知的「主工具栏是否批注中」，用于让按钮刷新幂等。</summary>
        private bool? _lastAnnotationState;

        private static readonly FontFamily BarIconFont =
            new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets");

        /// <summary>批注状态高亮用的强调色。</summary>
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
        private static readonly Brush AccentBackground = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xC2, 0xFF));
        private static readonly Brush TransparentBrush = Brushes.Transparent;
        private static readonly Brush IdleBorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        private static readonly Brush BarGlyphBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
        private static readonly Brush BarLabelBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));

        /// <summary>按需求组装底栏三组按钮：导航 | 视图 + 墨迹书写 | 窗口。</summary>
        private void BuildBottomBar()
        {
            // ---- 左组：上一张 / 文件夹 / 下一张 ----
            _btnPrevious = CreateBarButton("\uE76B", ImageViewerStrings.BarPrevious, ImageViewerStrings.BarPrevious,
                (_, _) => Navigate(-1));
            var btnFolder = CreateBarButton("\uE8B7", ImageViewerStrings.BarFolder, ImageViewerStrings.BarFolder,
                (_, _) => BrowseForImage());
            _btnNext = CreateBarButton("\uE76C", ImageViewerStrings.BarNext, ImageViewerStrings.BarNext,
                (_, _) => Navigate(1));

            LeftButtonGroup.Children.Add(_btnPrevious);
            LeftButtonGroup.Children.Add(btnFolder);
            LeftButtonGroup.Children.Add(_btnNext);

            // ---- 中组：放大 / 缩小 / 顺时针 / 逆时针 / 图像调整 ----（右侧接「墨迹书写」）
            var btnZoomIn = CreateBarButton("\uE8A3", ImageViewerStrings.BarZoomIn, ImageViewerStrings.BarZoomIn,
                (_, _) => RunCanvasGesture(() => ViewerCanvas.AnimateZoomIn()));
            var btnZoomOut = CreateBarButton("\uE71F", ImageViewerStrings.BarZoomOut, ImageViewerStrings.BarZoomOut,
                (_, _) => RunCanvasGesture(() => ViewerCanvas.AnimateZoomOut()));
            var btnRotateCw = CreateBarButton("\uE7AD", ImageViewerStrings.BarRotateCwShort,
                ImageViewerStrings.BarRotateCw, (_, _) => RunCanvasGesture(ViewerCanvas.RotateClockwise));
            var btnRotateCcw = CreateBarButton("\uE7AD", ImageViewerStrings.BarRotateCcwShort,
                ImageViewerStrings.BarRotateCcw,
                (_, _) => RunCanvasGesture(ViewerCanvas.RotateCounterClockwise), mirrorHorizontally: true);
            var btnAdjust = CreateBarButton("\uE9E9", ImageViewerStrings.BarAdjust, ImageViewerStrings.BarAdjust,
                (_, _) => ToggleAdjustPopup());

            // 主工具栏批注状态的开关：点击即让主工具栏进入/退出批注模式。
            _btnInkWriting = CreateBarButton("\uE70F", ImageViewerStrings.BarInkWriting,
                ImageViewerStrings.BarInkWritingHint, (_, _) => ToggleHostAnnotationMode(),
                useToggleStyle: true,
                captureParts: (glyph, label) =>
                {
                    _inkWritingGlyph = glyph;
                    _inkWritingLabel = label;
                });

            CenterButtonGroup.Children.Add(btnZoomIn);
            CenterButtonGroup.Children.Add(btnZoomOut);
            CenterButtonGroup.Children.Add(CreateBarSeparator());
            CenterButtonGroup.Children.Add(btnRotateCw);
            CenterButtonGroup.Children.Add(btnRotateCcw);
            CenterButtonGroup.Children.Add(CreateBarSeparator());
            CenterButtonGroup.Children.Add(btnAdjust);
            CenterButtonGroup.Children.Add(CreateBarSeparator());
            CenterButtonGroup.Children.Add(_btnInkWriting);

            AdjustPopup.PlacementTarget = btnAdjust;

            // ---- 右组：最小化 / 最大化 / 全屏 / 关闭 ----
            var btnMinimize = CreateBarButton("\uE921", ImageViewerStrings.BarMinimize, ImageViewerStrings.BarMinimize,
                (_, _) => WindowState = WindowState.Minimized);
            _btnMaximize = CreateBarButton("\uE922", ImageViewerStrings.BarMaximize, ImageViewerStrings.BarMaximize,
                (_, _) => ToggleMaximize());
            _btnFullScreen = CreateBarButton("\uE740", ImageViewerStrings.BarFullScreen, ImageViewerStrings.BarFullScreen,
                (_, _) => ToggleFullScreen());
            var btnClose = CreateBarButton("\uE8BB", ImageViewerStrings.BarClose, ImageViewerStrings.BarClose,
                (_, _) => Close(), useCloseStyle: true);

            RightButtonGroup.Children.Add(btnMinimize);
            RightButtonGroup.Children.Add(CreateBarSeparator());
            RightButtonGroup.Children.Add(_btnMaximize);
            RightButtonGroup.Children.Add(_btnFullScreen);
            RightButtonGroup.Children.Add(CreateBarSeparator());
            RightButtonGroup.Children.Add(btnClose);

            UpdateMaximizeButtonGlyph();
            UpdateInkWritingState(IsHostAnnotating, force: true);
        }

        private Rectangle CreateBarSeparator()
            => new Rectangle { Style = (Style)FindResource("ViewerBarSeparatorStyle") };

        /// <summary>
        /// 创建一个底栏按钮：上方图标、下方文字说明（需求要求「图标底部加入文字说明」）。
        /// <para>
        /// <paramref name="mirrorHorizontally"/> 用于「逆时针旋转」：Segoe 图标集只有一个旋转箭头，
        /// 镜像后就得到明确的逆时针语义。<paramref name="useToggleStyle"/> 用于「墨迹书写」，
        /// 它带边框以便用强调色表示当前是否处于批注状态。
        /// </para>
        /// </summary>
        private Button CreateBarButton(
            string glyph, string label, string tooltip, RoutedEventHandler onClick,
            bool mirrorHorizontally = false, bool useCloseStyle = false, bool useToggleStyle = false,
            Action<TextBlock, TextBlock> captureParts = null)
        {
            var glyphText = new TextBlock
            {
                Text = glyph,
                FontFamily = BarIconFont,
                FontSize = 16,
                Foreground = BarGlyphBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };

            if (mirrorHorizontally)
            {
                glyphText.RenderTransformOrigin = new Point(0.5, 0.5);
                glyphText.RenderTransform = new ScaleTransform(-1, 1);
            }

            var labelText = new TextBlock
            {
                Text = label,
                FontSize = 10.5,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = BarLabelBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
            };

            var content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(glyphText);
            content.Children.Add(labelText);

            string styleKey = useCloseStyle
                ? "ViewerBarCloseButtonStyle"
                : useToggleStyle ? "ViewerBarToggleButtonStyle" : "ViewerBarButtonStyle";

            var button = new Button
            {
                Content = content,
                ToolTip = tooltip,
                Style = (Style)FindResource(styleKey),
            };

            // 无障碍/自动化：按钮内容是图标 + 文字的 StackPanel，UIA 默认取不到可读名称，
            // 必须显式给出，否则读屏软件与自动化脚本都只能看到一个无名按钮。
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetHelpText(button, tooltip);

            captureParts?.Invoke(glyphText, labelText);

            button.Click += onClick;
            return button;
        }

        // ---------------- 主工具栏批注状态联动 ----------------

        /// <summary>主窗口（ICC 主界面）。查看器作为卫星窗口，批注模式由它统一管理。</summary>
        private static MainWindow HostMainWindow => Application.Current?.MainWindow as MainWindow;

        /// <summary>主工具栏当前是否处于批注（可书写）状态。</summary>
        private static bool IsHostAnnotating => HostMainWindow?.IsAnnotating ?? false;

        /// <summary>
        /// 「墨迹书写」按钮：让主工具栏在批注模式与鼠标模式之间切换。
        /// 走的是主窗口现成的批注模式入口（与画笔/光标按钮、自动化动作同一条序列），
        /// 因此逻辑工具、原生湿墨迹管线、卫星画布状态全部保持一致。
        /// </summary>
        private void ToggleHostAnnotationMode()
        {
            try
            {
                var host = HostMainWindow;
                if (host == null) return;

                bool enter = !host.IsAnnotating;
                host.SetAnnotationModeFromAutomation(enter);
                UpdateInkWritingState(host.IsAnnotating, force: true);

                // 主工具栏状态变化后立刻把状态推给本画布，不等下一拍 250ms 轮询。
                host.SyncSatelliteCanvasStateNow();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"图片查看器切换批注模式失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>按主工具栏的批注状态刷新「墨迹书写」按钮的高亮与提示（幂等）。</summary>
        /// <param name="annotating">主工具栏是否处于批注状态。</param>
        /// <param name="force">为 true 时即使状态未变也强制刷新一次。</param>
        internal void UpdateInkWritingState(bool annotating, bool force = false)
        {
            if (_btnInkWriting == null) return;
            if (!force && _lastAnnotationState == annotating) return;
            _lastAnnotationState = annotating;

            _btnInkWriting.Background = annotating ? AccentBackground : TransparentBrush;
            _btnInkWriting.BorderBrush = annotating ? AccentBrush : IdleBorderBrush;
            _btnInkWriting.ToolTip = annotating
                ? ImageViewerStrings.BarInkWritingActiveHint
                : ImageViewerStrings.BarInkWritingHint;

            // 图标与文字一起换成强调色，触摸屏上远看也能分辨当前状态。
            if (_inkWritingGlyph != null)
                _inkWritingGlyph.Foreground = annotating ? AccentBrush : BarGlyphBrush;
            if (_inkWritingLabel != null)
                _inkWritingLabel.Foreground = annotating ? AccentBrush : BarLabelBrush;
        }

        /// <summary>把一次画布变形包在「交互中降质量 / 结束后恢复」里。</summary>
        private void RunCanvasGesture(Action gesture)
        {
            if (gesture == null) return;
            ViewerCanvas.BeginInteractiveTransform();
            try
            {
                gesture();
            }
            finally
            {
                ViewerCanvas.EndInteractiveTransform();
            }
        }

        private void UpdateMaximizeButtonGlyph()
        {
            if (_btnMaximize?.Content is not TextBlock text) return;

            bool maximized = WindowState == WindowState.Maximized;
            // E922 = ChromeMaximize，E923 = ChromeRestore
            text.Text = maximized ? "\uE923" : "\uE922";
            _btnMaximize.ToolTip = maximized ? ImageViewerStrings.BarRestore : ImageViewerStrings.BarMaximize;
        }

        private void ToggleMaximize()
        {
            if (_fullScreenActive) ExitFullScreen();

            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void BrowseForImage()
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = ImageViewerStrings.BarFolder,
                    Filter = ImageViewerFormats.BuildOpenFileDialogFilter(ViewerSettings),
                    CheckFileExists = true,
                    Multiselect = false,
                };

                string folder = string.IsNullOrEmpty(_imagePath) ? null : Path.GetDirectoryName(_imagePath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) dialog.InitialDirectory = folder;

                if (dialog.ShowDialog(this) != true) return;
                LoadImage(dialog.FileName);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"图片查看器选择文件失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        // ---------------- 任务栏 ----------------

        /// <summary>
        /// 配置任务栏条目。每个查看器窗口都是独立顶层窗口，Windows 会为每个窗口显示
        /// 实时缩略图预览并可分别唤起到前台；这里再用 <c>ThumbnailClipMargin</c> 把底栏
        /// 裁出缩略图之外，使预览里只有图片本身。
        /// <para>
        /// 注意：.NET Core 版 WPF 没有移植 <c>ThumbnailToolBar</c> /
        /// <c>ThumbnailToolBarButton</c>（它们依赖 ITaskbarList3 的 COM 封装），
        /// 因此这里只用 <see cref="TaskbarItemInfo"/> 在 .NET 上确实可用的部分。
        /// </para>
        /// </summary>
        private void SetupTaskbarInfo()
        {
            try
            {
                TaskbarItemInfo = new TaskbarItemInfo();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"图片查看器配置任务栏条目失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        private void UpdateThumbnailClip()
        {
            try
            {
                if (TaskbarItemInfo == null) return;
                if (ViewerCanvas.ActualWidth <= 0 || ViewerCanvas.ActualHeight <= 0) return;

                var origin = ViewerCanvas.TranslatePoint(new Point(0, 0), this);
                double right = Math.Max(0, ActualWidth - origin.X - ViewerCanvas.ActualWidth);
                double bottom = Math.Max(0, ActualHeight - origin.Y - ViewerCanvas.ActualHeight);

                TaskbarItemInfo.ThumbnailClipMargin = new Thickness(
                    Math.Max(0, origin.X), Math.Max(0, origin.Y), right, bottom);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        // ---------------- 布局 / 状态 ----------------

        private void OnLayoutChanged()
        {
            UpdateThumbnailClip();

            if (ViewerCanvas.ActualWidth > 0 && ViewerCanvas.ActualHeight > 0)
                ViewerCanvas.OnViewportChanged(ViewerCanvas.ActualWidth, ViewerCanvas.ActualHeight);

            UpdateInfoOverlay(forceVisible: false);
        }

        private void OnCanvasViewChanged()
        {
            UpdateInfoOverlay(forceVisible: true);
        }

        private void UpdateWindowTitle()
        {
            string name = string.IsNullOrEmpty(_imagePath) ? string.Empty : Path.GetFileName(_imagePath);
            string brand = ImageViewerStrings.WindowTitleBrand;

            // 标题栏与窗口标题统一为「文件名 - Ink Canvas 图片查看器」。
            Title = string.IsNullOrEmpty(name) ? brand : $"{name} - {brand}";
            if (TextTitleBar != null) TextTitleBar.Text = Title;

            if (TaskbarItemInfo != null)
            {
                TaskbarItemInfo.Description = string.IsNullOrEmpty(name)
                    ? brand
                    : $"{name} · {ViewerCanvas.ImagePixelWidth}×{ViewerCanvas.ImagePixelHeight}";
            }
        }

        /// <summary>刷新左下角状态浮层；<paramref name="forceVisible"/> 为 true 时重新计时。</summary>
        private void UpdateInfoOverlay(bool forceVisible)
        {
            try
            {
                if (!string.IsNullOrEmpty(_imagePath))
                {
                    // 文件名已经在标题栏上了，这里只留尺寸/缩放/序号，避免重复占地方。
                    TextInfoTitle.Text = string.Format(
                        ImageViewerStrings.ImageInfoFormat,
                        ViewerCanvas.ImagePixelWidth,
                        ViewerCanvas.ImagePixelHeight,
                        Math.Round(ViewerCanvas.ZoomScale * 100));

                    bool hasSiblings = _siblings.Count > 1 && _siblingIndex >= 0;
                    TextInfoDetail.Text = hasSiblings
                        ? string.Format(ImageViewerStrings.ImageIndexFormat, _siblingIndex + 1, _siblings.Count)
                        : string.Empty;
                    TextInfoDetail.Visibility = hasSiblings ? Visibility.Visible : Visibility.Collapsed;
                }

                if (!forceVisible) return;

                if (_infoHideTimer == null) return;
                InfoOverlay.Visibility = Visibility.Visible;
                _infoHideTimer.Stop();
                _infoHideTimer.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        // ---------------- 图像调整 ----------------

        private void ToggleAdjustPopup()
        {
            // 关闭再打开不能重置调整值：滑杆与画布上的效果必须始终一致。
            // 真正的重置只在「换图」与「重置」按钮里做。
            AdjustPopup.IsOpen = !AdjustPopup.IsOpen;
            if (AdjustPopup.IsOpen) UpdateAdjustmentTexts();
        }

        private void UpdateAdjustmentTexts()
        {
            TextBrightness.Text = Math.Round(SliderBrightness.Value).ToString("0");
            TextContrast.Text = Math.Round(SliderContrast.Value).ToString("0");
            TextSaturation.Text = Math.Round(SliderSaturation.Value).ToString("0");
        }

        private void ResetAdjustmentSliders()
        {
            _adjustReady = false;
            try
            {
                SliderBrightness.Value = 0;
                SliderContrast.Value = 0;
                SliderSaturation.Value = 0;
                TextBrightness.Text = "0";
                TextContrast.Text = "0";
                TextSaturation.Text = "0";
            }
            finally
            {
                _adjustReady = true;
            }

            ViewerCanvas.ResetImageSurface();
        }

        private void AdjustSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_adjustReady) return;

            TextBrightness.Text = Math.Round(SliderBrightness.Value).ToString("0");
            TextContrast.Text = Math.Round(SliderContrast.Value).ToString("0");
            TextSaturation.Text = Math.Round(SliderSaturation.Value).ToString("0");

            // 逐像素处理与像素数成正比：拖动过程中合并成一次，松手后必然再算一遍。
            _adjustDebounceTimer.Stop();
            _adjustDebounceTimer.Start();
        }

        private void ApplyImageAdjustment()
        {
            try
            {
                var original = ViewerCanvas.OriginalImage;
                if (original == null) return;

                double brightness = SliderBrightness.Value;
                double contrast = SliderContrast.Value;
                double saturation = SliderSaturation.Value;

                if (ImageAdjustment.IsIdentity(brightness, contrast, saturation))
                {
                    ViewerCanvas.ResetImageSurface();
                    return;
                }

                ViewerCanvas.UpdateImageSurface(
                    ImageAdjustment.Apply(original, brightness, contrast, saturation));
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"应用图像调整失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        private void BtnAdjustReset_Click(object sender, RoutedEventArgs e)
        {
            ResetAdjustmentSliders();
        }

        // ---------------- 全屏 ----------------

        private void ToggleFullScreen()
        {
            if (_fullScreenActive) ExitFullScreen();
            else EnterFullScreen();
        }

        private void EnterFullScreen()
        {
            _stateBeforeFullScreen = WindowState;
            _fullScreenActive = true;

            // 全屏隐藏标题栏与底栏，但必须留下出口：显式的「退出全屏」按钮 + Esc。
            TitleBar.Visibility = Visibility.Collapsed;
            BottomBar.Visibility = Visibility.Collapsed;
            BtnExitFullScreen.Visibility = Visibility.Visible;
            Topmost = true;
            UpdateFullScreenButton();

            // 先回到 Normal 再最大化，强制 Windows 用新的最大化边界（整块显示器，含任务栏）重算。
            bool needReapply = WindowState == WindowState.Maximized;
            WindowState = WindowState.Normal;
            if (needReapply)
            {
                Dispatcher.BeginInvoke(new Action(() => WindowState = WindowState.Maximized),
                    DispatcherPriority.Loaded);
            }
            else
            {
                WindowState = WindowState.Maximized;
            }

            // 全屏后必须自己拿焦点：Esc 依赖键盘焦点，窗口没焦点时用户会觉得「退不出去」。
            Activate();
            Focus();
        }

        private void ExitFullScreen()
        {
            if (!_fullScreenActive) return;

            _fullScreenActive = false;
            Topmost = false;
            TitleBar.Visibility = Visibility.Visible;
            BottomBar.Visibility = Visibility.Visible;
            BtnExitFullScreen.Visibility = Visibility.Collapsed;
            UpdateFullScreenButton();

            bool wasMaximized = _stateBeforeFullScreen == WindowState.Maximized;
            WindowState = WindowState.Normal;
            if (wasMaximized)
            {
                Dispatcher.BeginInvoke(new Action(() => WindowState = WindowState.Maximized),
                    DispatcherPriority.Loaded);
            }
        }

        private void BtnExitFullScreen_Click(object sender, RoutedEventArgs e) => ExitFullScreen();

        private void UpdateFullScreenButton()
        {
            if (_btnFullScreen?.Content is StackPanel panel
                && panel.Children.Count > 0
                && panel.Children[0] is TextBlock glyph)
            {
                glyph.Text = _fullScreenActive ? "\uE73F" : "\uE740"; // E73F = BackToWindow
            }

            if (_btnFullScreen != null)
            {
                _btnFullScreen.ToolTip = _fullScreenActive
                    ? ImageViewerStrings.BarExitFullScreen
                    : ImageViewerStrings.BarFullScreen;
            }

            // 全屏下的浮动退出按钮文案（按钮本身只在全屏时可见）。
            if (TextExitFullScreen != null) TextExitFullScreen.Text = ImageViewerStrings.BarExitFullScreen;
            if (BtnExitFullScreen != null)
                BtnExitFullScreen.ToolTip = ImageViewerStrings.FullScreenExitHint;
        }

        // ---------------- 键盘快捷键 ----------------

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 图像调整面板持有键盘焦点时，方向键/加减号属于滑杆：只让 Esc 穿透（关面板）。
            if (AdjustPopup.IsOpen
                && AdjustPopup.Child is UIElement popupRoot
                && popupRoot.IsKeyboardFocusWithin
                && e.Key != Key.Escape)
            {
                return;
            }

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            switch (e.Key)
            {
                case Key.Escape:
                    if (_fullScreenActive) { ExitFullScreen(); e.Handled = true; }
                    else if (AdjustPopup.IsOpen) { AdjustPopup.IsOpen = false; e.Handled = true; }
                    return;

                case Key.Left:
                    Navigate(-1);
                    e.Handled = true;
                    return;

                case Key.Right:
                    Navigate(1);
                    e.Handled = true;
                    return;

                case Key.OemPlus:
                case Key.Add:
                    RunCanvasGesture(() => ViewerCanvas.ZoomAt(ViewportCenter(), ImageViewerCanvasZoomStep));
                    e.Handled = true;
                    return;

                case Key.OemMinus:
                case Key.Subtract:
                    RunCanvasGesture(() => ViewerCanvas.ZoomAt(ViewportCenter(), 1.0 / ImageViewerCanvasZoomStep));
                    e.Handled = true;
                    return;

                case Key.D0:
                case Key.NumPad0:
                    if (!ctrl) return;
                    RunCanvasGesture(ViewerCanvas.FitToViewport);
                    e.Handled = true;
                    return;

                case Key.D1:
                case Key.NumPad1:
                    if (!ctrl) return;
                    RunCanvasGesture(ViewerCanvas.SetActualSize);
                    e.Handled = true;
                    return;

                case Key.R:
                    RunCanvasGesture(shift ? ViewerCanvas.RotateCounterClockwise : ViewerCanvas.RotateClockwise);
                    e.Handled = true;
                    return;

                case Key.F11:
                    ToggleFullScreen();
                    e.Handled = true;
                    return;

                case Key.Z:
                    if (!ctrl) return;
                    ViewerCanvas.InkHistoryUndo();
                    e.Handled = true;
                    return;

                case Key.Y:
                    if (!ctrl) return;
                    ViewerCanvas.InkHistoryRedo();
                    e.Handled = true;
                    return;
            }
        }

        private const double ImageViewerCanvasZoomStep = 1.25;

        private Point ViewportCenter()
            => new Point(ViewerCanvas.ActualWidth / 2, ViewerCanvas.ActualHeight / 2);

        // ---------------- 窗口放置 ----------------

        /// <summary>套用上次记住的窗口位置与大小，并保证窗口一定落在可见屏幕范围内。</summary>
        private void ApplySavedPlacement()
        {
            var settings = ViewerSettings;
            try
            {
                if (settings != null && settings.RememberWindowPlacement && settings.WindowLeft.HasValue)
                {
                    Left = settings.WindowLeft.Value;
                    Top = settings.WindowTop ?? Top;
                    Width = settings.WindowWidth;
                    Height = settings.WindowHeight;
                }
                else
                {
                    // 没有历史记录：在鼠标所在屏幕的工作区居中，并缩放到不超过工作区。
                    var work = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
                    var dpi = VisualTreeHelper.GetDpi(this);

                    double workWidth = work.Width / dpi.DpiScaleX;
                    double workHeight = work.Height / dpi.DpiScaleY;

                    Width = Math.Min(Width, workWidth * 0.9);
                    Height = Math.Min(Height, workHeight * 0.9);

                    var virtualOriginLeft = SystemParameters.VirtualScreenLeft;
                    var virtualOriginTop = SystemParameters.VirtualScreenTop;
                    double monitorLeft = work.Left / dpi.DpiScaleX + virtualOriginLeft;
                    double monitorTop = work.Top / dpi.DpiScaleY + virtualOriginTop;

                    Left = monitorLeft + (workWidth - Width) / 2;
                    Top = monitorTop + (workHeight - Height) / 2;
                }

                ClampIntoVirtualScreen();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"套用图片查看器窗口位置失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 把窗口夹进虚拟屏幕范围：宽度/高度不超过屏幕，且至少留出足够区域可见，
        /// 避免「上次在副屏、这次副屏没接」导致窗口消失在屏幕外。
        /// </summary>
        private void ClampIntoVirtualScreen()
        {
            double vsLeft = SystemParameters.VirtualScreenLeft;
            double vsTop = SystemParameters.VirtualScreenTop;
            double vsWidth = SystemParameters.VirtualScreenWidth;
            double vsHeight = SystemParameters.VirtualScreenHeight;

            if (vsWidth <= 0 || vsHeight <= 0) return;

            const double minVisibleWidth = 120;
            const double minVisibleHeight = 80;

            Width = Math.Min(Math.Max(MinWidth, Width), vsWidth);
            Height = Math.Min(Math.Max(MinHeight, Height), vsHeight);

            if (double.IsNaN(Left)) Left = vsLeft + (vsWidth - Width) / 2;
            if (double.IsNaN(Top)) Top = vsTop + (vsHeight - Height) / 2;

            Left = Math.Min(Math.Max(Left, vsLeft - Width + minVisibleWidth), vsLeft + vsWidth - minVisibleWidth);
            Top = Math.Min(Math.Max(Top, vsTop), vsTop + vsHeight - minVisibleHeight);
        }

        private void SavePlacement()
        {
            try
            {
                var settings = ViewerSettings;
                if (settings == null || !settings.RememberWindowPlacement) return;

                // 最大化/全屏时用 RestoreBounds 记住「还原后」的尺寸，否则下次打开会以最大化尺寸定窗口。
                var bounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;

                if (bounds.Width <= 0 || bounds.Height <= 0) return;

                settings.WindowLeft = bounds.Left;
                settings.WindowTop = bounds.Top;
                settings.WindowWidth = bounds.Width;
                settings.WindowHeight = bounds.Height;
                SettingsManager.SaveSettingsToFile();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        // ---------------- 关闭 ----------------

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closingHandled) return;
            _closingHandled = true;

            // 平滑缩放挂在静态的 CompositionTarget.Rendering 上，窗口关掉却不停掉的话
            // 回调会继续跑在一棵已经断开的视觉树上。
            try { ViewerCanvas.CancelZoomAnimation(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

            SaveAnnotationsForCurrentImage();
            SavePlacement();

            if (_hwndSource != null)
            {
                try { _hwndSource.RemoveHook(WndProc); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
                _hwndSource = null;
            }
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            _infoHideTimer?.Stop();
            _adjustDebounceTimer?.Stop();
            ImageViewerManager.Unregister(this);
        }

        // ---------------- 窗口消息：命中测试 / 最大化边界 ----------------

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_NCCALCSIZE:
                    return HandleNonClientCalcSize(wParam, ref handled);

                case WM_NCHITTEST:
                    return HandleNcHitTest(lParam, ref handled);

                case WM_GETMINMAXINFO:
                    return HandleGetMinMaxInfo(hwnd, lParam, ref handled);

                case WM_EXITSIZEMOVE:
                    // 系统拖动/缩放（含 Aero Snap 吸附）结束后收尾：刷新缩略图裁剪并记住位置。
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        OnLayoutChanged();
                        SavePlacement();
                    }), DispatcherPriority.Background);
                    break;

                case WM_DPICHANGED:
                    Dispatcher.BeginInvoke(new Action(OnLayoutChanged), DispatcherPriority.Background);
                    break;
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// 自定义命中测试：
        /// <list type="bullet">
        /// <item>窗口边框 → <c>HTLEFT/HTRIGHT/HTTOP/HTBOTTOM</c> 系列，交给系统缩放；</item>
        /// <item>底栏空白处、图片之外的留白、Alt 按住时的任意位置 → <c>HTCAPTION</c>，
        /// 交给系统拖动，从而免费获得 Aero Snap（顶部最大化、左右半屏分屏）与不越界约束。</item>
        /// </list>
        /// </summary>
        private IntPtr HandleNcHitTest(IntPtr lParam, ref bool handled)
        {
            try
            {
                long value = lParam.ToInt64();
                int screenX = unchecked((short)(value & 0xFFFF));
                int screenY = unchecked((short)((value >> 16) & 0xFFFF));

                var client = PointFromScreen(new Point(screenX, screenY));
                double width = ActualWidth;
                double height = ActualHeight;
                if (width <= 0 || height <= 0) return IntPtr.Zero;

                bool maximized = WindowState == WindowState.Maximized || _fullScreenActive;

                if (!maximized)
                {
                    bool left = client.X <= ResizeBorderThickness;
                    bool right = client.X >= width - ResizeBorderThickness;
                    bool top = client.Y <= ResizeBorderThickness;
                    bool bottom = client.Y >= height - ResizeBorderThickness;

                    if (top && left) { handled = true; return new IntPtr(HTTOPLEFT); }
                    if (top && right) { handled = true; return new IntPtr(HTTOPRIGHT); }
                    if (bottom && left) { handled = true; return new IntPtr(HTBOTTOMLEFT); }
                    if (bottom && right) { handled = true; return new IntPtr(HTBOTTOMRIGHT); }
                    if (left) { handled = true; return new IntPtr(HTLEFT); }
                    if (right) { handled = true; return new IntPtr(HTRIGHT); }
                    if (top) { handled = true; return new IntPtr(HTTOP); }
                    if (bottom) { handled = true; return new IntPtr(HTBOTTOM); }
                }

                if (!_fullScreenActive && IsDragRegion(client))
                {
                    handled = true;
                    return new IntPtr(HTCAPTION);
                }
            }
            catch (Exception ex)
            {
                // 命中测试在消息循环里，绝不能抛出。
                System.Diagnostics.Debug.WriteLine(ex);
            }

            return IntPtr.Zero;
        }

        /// <summary>该点是否应当作为「拖动窗口」的区域。</summary>
        private bool IsDragRegion(Point client)
        {
            // Alt + 任意位置：图片铺满视口（没有留白）时的拖窗逃生口。
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) return true;

            // 标题栏：除右侧三个按钮外整条都是拖动区（与系统标题栏一致）。
            if (TitleBar.ActualHeight > 0)
            {
                double titleBottom = TitleBar.TranslatePoint(new Point(0, 0), this).Y + TitleBar.ActualHeight;
                if (client.Y <= titleBottom) return !IsOverButtonGroup(TitleBarButtonGroup, client);
            }

            // 底栏：只有按钮之外的空白处才拖窗。
            // 布局尚未完成时（ActualHeight=0）不能把整窗都判成底栏，否则连批注都写不了。
            if (BottomBar.ActualHeight > 0)
            {
                double barTop = BottomBar.TranslatePoint(new Point(0, 0), this).Y;
                if (client.Y >= barTop) return !IsOverButtonGroup(RightButtonGroup, client)
                                              && !IsOverButtonGroup(CenterButtonGroup, client)
                                              && !IsOverButtonGroup(LeftButtonGroup, client);
            }

            // 画布：图片之外的留白（letterbox）拖动 = 移动窗口；
            // 图片上的左键仍然留给批注书写。
            var canvasPoint = TranslatePoint(client, ViewerCanvas);
            return !ViewerCanvas.IsPointOverImage(canvasPoint);
        }

        /// <summary>该点是否落在某个按钮组里的按钮上（用几何边界判定，避免视觉树命中测试的开销与歧义）。</summary>
        private bool IsOverButtonGroup(Panel group, Point client)
        {
            if (group == null) return false;

            foreach (UIElement child in group.Children)
            {
                if (child is not FrameworkElement element) continue;
                if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;

                var origin = element.TranslatePoint(new Point(0, 0), this);
                if (client.X >= origin.X && client.X <= origin.X + element.ActualWidth
                    && client.Y >= origin.Y && client.Y <= origin.Y + element.ActualHeight)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 约束最大化/拖拽尺寸：
        /// <list type="bullet">
        /// <item>最大化时贴合<b>显示器工作区</b>（不盖任务栏）；全屏时贴合<b>整块显示器</b>；</item>
        /// <item>最大可拖拽尺寸不超过显示器范围 → 窗口本体永远不超出屏幕；</item>
        /// <item>最小尺寸取自 <see cref="FrameworkElement.MinWidth"/>/<see cref="FrameworkElement.MinHeight"/>。</item>
        /// </list>
        /// <para>
        /// 关键在 <c>ptMaxSize</c>/<c>ptMaxPosition</c> 要按**窗口边框**补偿：
        /// <c>WindowStyle=None</c> + <c>ResizeMode=CanResize</c> 的窗口仍有 <c>WS_THICKFRAME</c>，
        /// Windows 最大化时会把这圈不可见边框算进去。不补偿就会「最大化不完全」——
        /// 窗口比工作区大一圈、被顶到左上方，右下两条边反而超出屏幕。
        /// 这里把尺寸加上、位置减去边框厚度，让**可见区域**正好等于工作区。
        /// </para>
        /// </summary>
        private IntPtr HandleGetMinMaxInfo(IntPtr hwnd, IntPtr lParam, ref bool handled)
        {
            try
            {
                var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                var screen = WinForms.Screen.FromHandle(hwnd);
                if (screen == null) return IntPtr.Zero;

                var monitor = screen.Bounds;
                var target = _fullScreenActive ? screen.Bounds : screen.WorkingArea;

                // 边框补偿（物理像素）：实测当前窗口的不可见 thick frame 厚度。
                GetFrameThickness(hwnd, out int frameX, out int frameY);

                info.ptMaxPosition.X = target.Left - monitor.Left - frameX;
                info.ptMaxPosition.Y = target.Top - monitor.Top - frameY;
                info.ptMaxSize.X = target.Width + frameX * 2;
                info.ptMaxSize.Y = target.Height + frameY * 2;

                info.ptMaxTrackSize.X = monitor.Width + frameX * 2;
                info.ptMaxTrackSize.Y = monitor.Height + frameY * 2;
                var dpi = VisualTreeHelper.GetDpi(this);
                info.ptMinTrackSize.X = (int)Math.Ceiling(Math.Max(1, MinWidth) * dpi.DpiScaleX);
                info.ptMinTrackSize.Y = (int)Math.Ceiling(Math.Max(1, MinHeight) * dpi.DpiScaleY);

                Marshal.StructureToPtr(info, lParam, false);
                handled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }

            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint ptReserved;
            public NativePoint ptMaxSize;
            public NativePoint ptMaxPosition;
            public NativePoint ptMinTrackSize;
            public NativePoint ptMaxTrackSize;
        }

        // ---------------- 供管理器调用 ----------------

        /// <summary>刷新背景色（设置页改色后即时生效）。</summary>
        internal void ApplyBackgroundColor()
        {
            Background = new SolidColorBrush(ImageViewerFormats.ParseColorOr(
                ViewerSettings?.BackgroundColor, Color.FromRgb(0x1E, 0x1E, 0x1E)));
        }

        /// <summary>把窗口从最小化恢复并激活（任务栏缩略图点击的等价行为）。</summary>
        internal void ActivateFromTaskbar()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            try
            {
                Focus();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
    }
}
