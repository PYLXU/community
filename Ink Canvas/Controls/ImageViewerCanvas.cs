using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ink_Canvas.Controls
{
    /// <summary>
    /// 内置图片查看器的画布：<see cref="IndependentCanvas"/> 的图片版本。
    /// <para>
    /// 在独立画布的<b>背景层</b>里放一张图片，并把「图片像素空间 → 视口空间」的视图变换
    /// （缩放 / 旋转 / 平移）<b>同时</b>作用到图片与墨迹上：
    /// </para>
    /// <list type="bullet">
    /// <item>图片本身用 <see cref="MatrixTransform"/> 渲染，走 GPU，缩放/旋转不掉帧；</item>
    /// <item>墨迹走基类的 <see cref="IndependentCanvas.ApplyInkTransform"/>，把变换<b>烘焙进笔迹点集</b>。
    /// 这样橡皮擦命中测试、框选、坐标换算全都无需知道视图变换的存在
    /// （基类「数据坐标 == 视觉坐标」不变式），批注也因此始终贴在图片的同一处内容上。</item>
    /// </list>
    /// <para>
    /// 两套坐标系的换算关系：
    /// <c>视口坐标 = ViewMatrix × 图片坐标</c>。批注需要按图片持久化时，
    /// 用 <see cref="ViewMatrix"/> 的逆把视口坐标的笔迹点换算回图片坐标保存，
    /// 下次载入时再用当前 <see cref="ViewMatrix"/> 正变换回去，批注就永远锚在图片内容上。
    /// </para>
    /// <para>
    /// 交互分工（与查看器窗口的 WM_NCHITTEST 配合）：
    /// 左键在图上 = 书写批注；中键 / 右键 / 空格或 Ctrl + 左键 = 平移图片；滚轮 = 以光标为中心缩放；
    /// 图片之外的空白处左键拖动由窗口接管为「移动窗口」。
    /// </para>
    /// </summary>
    public sealed class ImageViewerCanvas : IndependentCanvas
    {
        /// <summary>最小缩放（再小就只剩几个像素了）。</summary>
        public const double MinZoom = 0.02;

        /// <summary>最大缩放。</summary>
        public const double MaxZoom = 32.0;

        /// <summary>滚轮每格的缩放倍率。</summary>
        private const double WheelZoomFactor = 1.15;

        /// <summary>按钮/快捷键的缩放倍率。</summary>
        private const double StepZoomFactor = 1.25;

        /// <summary>
        /// 平滑缩放的时间常数（秒）。每帧按 <c>1 − e^(−dt/τ)</c> 逼近目标值，
        /// 因此与帧率无关；τ 越小越跟手，0.06 在「跟手」与「有过渡感」之间比较平衡。
        /// </summary>
        private const double ZoomEaseTau = 0.06;

        /// <summary>平滑缩放收敛阈值（相对值），小于它就吸附到目标值并结束动画。</summary>
        private const double ZoomEaseSnapRatio = 0.0015;

        /// <summary>背景层里的图片元素。</summary>
        private readonly Image _image;

        /// <summary>
        /// 橡皮擦擦头足迹：与主画布**同一份实现、同一组图片资源**。
        /// 主工具栏切到几何擦（面积擦）时跟随指针显示，与主画布/UI 联动画布所见一致。
        /// </summary>
        private readonly Ink_Canvas.Ink.EraserFootprint _eraserFootprint;

        /// <summary>图片的渲染变换（绝对视图矩阵，与墨迹的累积变换保持一致）。</summary>
        private readonly MatrixTransform _imageTransform;

        /// <summary>未经过「图像调整」的原始位图。</summary>
        private BitmapSource _originalImage;

        /// <summary>图片自身的像素尺寸（视图变换的源头坐标系）。</summary>
        private int _pixelWidth;
        private int _pixelHeight;

        /// <summary>当前缩放倍率（1.0 = 1 图片像素对应 1 DIP）。</summary>
        private double _zoom = 1.0;

        /// <summary>当前旋转角度（度，正值为顺时针）。</summary>
        private double _rotation;

        /// <summary>视图平移量（视口 DIP）。</summary>
        private Vector _translation;

        /// <summary>「适应窗口」模式：视口尺寸变化时自动重新适配。</summary>
        private bool _fitMode;

        /// <summary>交互变形（拖动/缩放/旋转）过程中把重采样降到 LowQuality，结束后恢复。</summary>
        private bool _interactive;

        private bool _panning;
        private Point _panLastPoint;
        private InkCanvasEditingMode _editingModeBeforePan;

        // 平滑缩放动画状态
        private double _zoomTarget = 1.0;
        private Point _zoomAnchor;
        private bool _zoomAnimating;
        private DateTime _zoomAnimLastUtc;

        // 触摸手势状态（手指 id → 画布坐标）
        private readonly System.Collections.Generic.Dictionary<int, Point> _touchPoints
            = new System.Collections.Generic.Dictionary<int, Point>();
        private bool _touchGestureActive;
        private Point _touchLastCentroid;
        private double _touchLastSpread;
        private bool _singleTouchPanning;
        private Point _singleTouchLastPoint;

        /// <summary>平移是否被限制在「图片不留缝」的范围内（默认开）。</summary>
        public bool ConstrainPanning { get; set; } = true;

        /// <summary>视图（缩放/旋转/平移）发生变化时触发，供宿主刷新状态文字。</summary>
        public event Action ViewChanged;

        /// <summary>
        /// 非批注状态下仍需接收输入：图片查看器要靠鼠标拖动与双指手势平移缩放图片。
        /// 不落墨由宿主的 <c>EditingMode = None</c> 保证，而不是靠禁用整块画布。
        /// </summary>
        public override bool RequiresInputWhenIdle => true;

        /// <summary>
        /// 不接受宿主按批注状态暂停浮层：滚动条本就按设计隐藏，
        /// 而撤销/恢复按钮是批注的主要入口，切到鼠标模式时也要留着。
        /// </summary>
        public override bool HostControlsOverlaySuppression => false;

        /// <summary>当前是否处于「非批注」状态（此时单指/左键拖动用于平移图片）。</summary>
        private bool IsIdleForPanGestures => InkSurface.EditingMode == InkCanvasEditingMode.None;

        public ImageViewerCanvas()
        {
            // 填充式宿主：尺寸交给窗口布局，不跟随 SetViewportSize。
            AutoSizeToViewport = false;
            // 基类构造函数（在派生构造函数体之前执行）已经按「固定视口」把 Width/Height 设成了
            // 1×1 + 滚动条条带；这里必须清掉，否则画布会塌成 25×25 而不是铺满窗口。
            Width = double.NaN;
            Height = double.NaN;
            // 滚动条与滚动辅助按钮对图片查看器无意义（平移由手势完成）。
            // 注意：不要用 SetScrollBarsSuppressed —— 那会连墨迹的撤销/恢复按钮一起隐藏。
            ShowVerticalScrollBar = false;
            ShowHorizontalScrollBar = false;

            _imageTransform = new MatrixTransform(Matrix.Identity);
            _image = new Image
            {
                // 尺寸由 Width/Height 精确给定（= 图片像素数），缩放全靠 RenderTransform，
                // 因此不会出现 Stretch 反复重算尺寸导致的抖动。
                Stretch = Stretch.Fill,
                RenderTransform = _imageTransform,
                IsHitTestVisible = false,
                SnapsToDevicePixels = false,
            };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

            BackgroundLayer.Children.Add(_image);

            // 让指针由外层控件统一决定：WPF 的命中测试会从被命中元素向上找第一个非空 Cursor，
            // 内层 InkCanvas 若自带指针（笔形）就轮不到外层。这里清空它，由宿主按批注状态设置。
            InkSurface.Cursor = null;

            // 擦头足迹放在覆盖层（屏幕空间、不随图片缩放旋转），与主画布同一份实现。
            _eraserFootprint = new Ink_Canvas.Ink.EraserFootprint(OverlayLayer, this);

            // 视图手势走隧道路由，抢在 InkCanvas 之前处理。
            PreviewMouseDown += OnPreviewMouseDown;
            PreviewMouseMove += OnPreviewMouseMove;
            PreviewMouseUp += OnPreviewMouseUp;
            LostMouseCapture += (_, _) => EndPanGesture();
            PreviewMouseWheel += OnPreviewMouseWheel;

            // 触摸手势（单指平移 / 双指缩放平移）。
            // 注意挂在本控件（隧道阶段最外层）而<b>不</b>设 Handled：
            // 基类挂在 InkCanvas 上的多指抑制与手掌擦必须照常收到事件，
            // 这样「手指永远不会在图上留下误笔迹」的既有语义在查看器里同样成立。
            PreviewTouchDown += OnPreviewTouchDown;
            PreviewTouchMove += OnPreviewTouchMove;
            PreviewTouchUp += OnPreviewTouchUp;
            LostTouchCapture += OnPreviewTouchUp;

            // 擦头足迹跟随指针（鼠标 / 手写笔 / 触摸都要跟），离开视口即隐藏。
            PreviewMouseMove += (_, e) => UpdateEraserFootprint(e.GetPosition(this));
            PreviewStylusMove += (_, e) => UpdateEraserFootprint(e.GetPosition(this));
            MouseLeave += (_, _) => HideEraserFootprint();
            StylusLeave += (_, _) => HideEraserFootprint();

            // 主工具栏切走擦除工具时立刻收起擦头框（否则会留在屏幕上。
            // 宿主每 250ms 也会同步一次 EditingMode，但视觉上必须当场消失）。
            InkSurface.EditingModeChanged += (_, _) =>
            {
                if (InkSurface.EditingMode != InkCanvasEditingMode.EraseByPoint)
                    HideEraserFootprint();
            };
        }

        /// <summary>
        /// 刷新擦头足迹：**只有几何擦（面积擦）才显示**。
        /// <para>
        /// 与主画布口径一致 —— 主画布的擦头预览只在非「笔画擦」分支里显示，
        /// 线擦模式下不显示擦头框（那一擦是针对整条笔迹的，没有「范围」可言）。
        /// </para>
        /// <para>
        /// 尺寸直接取自 <see cref="IndependentCanvas.InkSurface"/> 上同步过来的
        /// <c>EraserShape</c>，因此预览框与真实擦除范围必定一致。
        /// </para>
        /// </summary>
        private void UpdateEraserFootprint(Point viewportPoint)
        {
            if (_pixelWidth <= 0) return;
            if (InkSurface.EditingMode != InkCanvasEditingMode.EraseByPoint)
            {
                HideEraserFootprint();
                return;
            }

            _eraserFootprint.ApplyShape(InkSurface.EraserShape);
            _eraserFootprint.MoveTo(viewportPoint);
            _eraserFootprint.Show();
        }

        /// <summary>隐藏擦头足迹（切工具、离开视口、换图时调用）。</summary>
        public void HideEraserFootprint()
        {
            if (_eraserFootprint.IsVisible) _eraserFootprint.Hide();
        }

        /// <summary>
        /// 擦头图片资源是否可用（窗口是否合并了与主画布共用的 <c>MW_Eraser.xaml</c>）。
        /// 供宿主在启动时做一次显式检查，避免擦头静默变成空白。
        /// </summary>
        public bool EraserFootprintResourceAvailable => _eraserFootprint.IsResourceAvailable();

        // ---------------- 对外状态 ----------------

        /// <summary>未经过「图像调整」的原始位图（调整面板的输入）。</summary>
        public BitmapSource OriginalImage => _originalImage;

        /// <summary>当前实际显示的位图（可能是调整后的结果）。</summary>
        public BitmapSource DisplayedImage => _image.Source as BitmapSource;

        /// <summary>图片像素宽度。</summary>
        public int ImagePixelWidth => _pixelWidth;

        /// <summary>图片像素高度。</summary>
        public int ImagePixelHeight => _pixelHeight;

        /// <summary>当前缩放倍率（1.0 = 原始大小）。</summary>
        public double ZoomScale => _zoom;

        /// <summary>当前旋转角度（度，正值为顺时针）。</summary>
        public double RotationDegrees => _rotation;

        /// <summary>是否处于「适应窗口」模式。</summary>
        public bool IsFitMode => _fitMode;

        /// <summary>当前视图矩阵：图片像素坐标 → 视口坐标（批注持久化的坐标换算依据）。</summary>
        public Matrix ViewMatrix => _imageTransform.Matrix;

        /// <summary>图片是否已完全落在视口内（图片小于视口时为 true）。</summary>
        public bool IsImageFullyVisible
        {
            get
            {
                var bounds = GetImageBoundsInView();
                if (bounds.IsEmpty) return true;
                return bounds.Left >= -0.5 && bounds.Top >= -0.5
                       && bounds.Right <= ActualWidth + 0.5 && bounds.Bottom <= ActualHeight + 0.5;
            }
        }

        // ---------------- 载入图片 ----------------

        /// <summary>
        /// 载入一张图片作为画布底图，并<b>重置批注面</b>：清空墨迹与撤销历史、旋转归零，
        /// 再按 <paramref name="fitToWindow"/> 决定初始缩放。
        /// <para>
        /// 「换图 = 换批注面」是刻意的：上一张图的批注若留在画布上，会被误认为属于新图。
        /// 需要恢复某张图的历史批注时，在调用本方法之后用 <see cref="ViewMatrix"/> 把
        /// 图片坐标的笔迹变换到视口坐标再加进 <see cref="IndependentCanvas.Strokes"/>，
        /// 然后调用 <see cref="IndependentCanvas.ResetInkHistory"/> 让历史从零开始。
        /// </para>
        /// </summary>
        public void SetImage(BitmapSource source, bool fitToWindow)
        {
            CancelZoomAnimation();
            HideEraserFootprint();
            ClearAllStrokes();
            ResetInkHistory();

            // 基类累积的渐隐层变换与图片自身的变换必须一起归零，否则二者会永久错位。
            ResetInkTransformWithoutMovingStrokes();
            _imageTransform.Matrix = Matrix.Identity;

            _originalImage = source;
            _image.Source = source;
            _pixelWidth = source?.PixelWidth ?? 0;
            _pixelHeight = source?.PixelHeight ?? 0;

            _image.Width = Math.Max(1, _pixelWidth);
            _image.Height = Math.Max(1, _pixelHeight);

            _rotation = 0;
            _translation = default;
            _zoom = 1.0;

            if (_pixelWidth <= 0 || _pixelHeight <= 0)
            {
                _fitMode = false;
                ViewChanged?.Invoke();
                return;
            }

            if (fitToWindow) FitToViewport();
            else SetActualSize();
        }

        /// <summary>替换显示用的位图（「图像调整」的结果），保持缩放/旋转/平移与墨迹不变。</summary>
        public void UpdateImageSurface(BitmapSource adjusted)
        {
            if (adjusted == null) return;
            _image.Source = adjusted;
        }

        /// <summary>把显示位图还原为原始位图（「图像调整」重置）。</summary>
        public void ResetImageSurface()
        {
            if (_originalImage != null) _image.Source = _originalImage;
        }

        /// <summary>视口尺寸变化时调用：刷新基类记录的视口大小，并按需重新适配或夹紧视图。</summary>
        public void OnViewportChanged(double width, double height)
        {
            if (double.IsNaN(width) || width <= 0) width = 1;
            if (double.IsNaN(height) || height <= 0) height = 1;

            SetViewportSize(width, height);
            if (_pixelWidth <= 0 || _pixelHeight <= 0) return;

            if (_fitMode) FitToViewport();
            else ClampAndApply();
        }

        // ---------------- 缩放 / 旋转 / 平移 ----------------

        /// <summary>以视口中心为锚点平滑放大一档（底栏按钮 / 快捷键）。</summary>
        public void AnimateZoomIn() => AnimateZoom(StepZoomFactor, ViewportCenter());

        /// <summary>以视口中心为锚点平滑缩小一档。</summary>
        public void AnimateZoomOut() => AnimateZoom(1.0 / StepZoomFactor, ViewportCenter());

        /// <summary>
        /// 平滑缩放到「当前目标倍率 × factor」，锚点下方的图片内容保持不动。
        /// <para>
        /// 与 <see cref="ZoomAt"/> 的区别：连续多次调用（滚轮连滚、长按按钮）会累积到同一个
        /// 目标值上并共用一段缓动动画，而不是每格都重新起一段 —— 这是滚轮缩放「顺滑」的关键。
        /// 缓动按 <c>1 − e^(−dt/τ)</c> 逐帧逼近目标，因此与帧率无关。
        /// </para>
        /// </summary>
        /// <param name="factor">相对倍率（&gt;1 放大）。</param>
        /// <param name="viewportAnchor">锚点（视口坐标），该点下的图片内容保持不动。</param>
        public void AnimateZoom(double factor, Point viewportAnchor)
        {
            if (_pixelWidth <= 0 || factor <= 0) return;

            double baseline = _zoomAnimating ? _zoomTarget : _zoom;
            double target = Clamp(baseline * factor, MinZoom, MaxZoom);
            if (Math.Abs(target - baseline) < 1e-9) return;

            _zoomTarget = target;
            _zoomAnchor = viewportAnchor;

            if (_zoomAnimating) return;

            _zoomAnimating = true;
            _zoomAnimLastUtc = DateTime.UtcNow;
            BeginInteractiveTransform();
            CompositionTarget.Rendering += OnZoomAnimationFrame;
        }

        /// <summary>中止未完成的平滑缩放（切图、适应窗口、手势接管时调用），保持当前实际倍率。</summary>
        public void CancelZoomAnimation()
        {
            _zoomTarget = _zoom;
            StopZoomAnimation();
        }

        private void OnZoomAnimationFrame(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            double dt = (now - _zoomAnimLastUtc).TotalSeconds;
            _zoomAnimLastUtc = now;
            if (dt <= 0) return;

            double current = _zoom;
            double target = _zoomTarget;
            if (Math.Abs(current) < 1e-12) { StopZoomAnimation(); return; }

            double eased = current + (target - current) * (1 - Math.Exp(-dt / ZoomEaseTau));
            bool converged = Math.Abs(eased - target) <= Math.Abs(target) * ZoomEaseSnapRatio;
            double next = converged ? target : eased;

            double step = next / current;
            if (Math.Abs(step - 1) > 1e-9) ZoomAt(_zoomAnchor, step);

            if (converged) StopZoomAnimation();
        }

        private void StopZoomAnimation()
        {
            if (!_zoomAnimating) return;
            _zoomAnimating = false;
            CompositionTarget.Rendering -= OnZoomAnimationFrame;
            EndInteractiveTransform();
        }

        private Point ViewportCenter()
            => new Point(Math.Max(1, ActualWidth) / 2, Math.Max(1, ActualHeight) / 2);

        /// <summary>以视口中心为锚点放大一档（立即生效，供不需要过渡的场景使用）。</summary>
        public void ZoomIn() => ZoomAt(ViewportCenter(), StepZoomFactor);

        /// <summary>以视口中心为锚点缩小一档。</summary>
        public void ZoomOut() => ZoomAt(ViewportCenter(), 1.0 / StepZoomFactor);

        /// <summary>
        /// 以 <paramref name="viewportAnchor"/> 为锚点缩放：该点下方的图片内容保持不动。
        /// </summary>
        public void ZoomAt(Point viewportAnchor, double factor)
        {
            if (_pixelWidth <= 0 || factor <= 0) return;

            double target = Clamp(_zoom * factor, MinZoom, MaxZoom);
            if (Math.Abs(target - _zoom) < 1e-9) return;

            // 锚点对应的图片坐标：缩放前后都必须落在同一个视口位置上。
            var imageAnchor = ViewportToImage(viewportAnchor);
            _zoom = target;
            _fitMode = false;
            RebuildViewKeepingPointFixed(imageAnchor, viewportAnchor);
        }

        /// <summary>适应窗口：整张图片完整可见并居中。</summary>
        public void FitToViewport()
        {
            if (_pixelWidth <= 0 || _pixelHeight <= 0) return;
            CancelZoomAnimation();

            double vw = Math.Max(1, ActualWidth);
            double vh = Math.Max(1, ActualHeight);

            // 旋转 90°/270° 时图片的视觉宽高互换，适配要按旋转后的包围盒算。
            double visualW = IsQuarterTurned ? _pixelHeight : _pixelWidth;
            double visualH = IsQuarterTurned ? _pixelWidth : _pixelHeight;

            double scale = Math.Min(vw / visualW, vh / visualH);
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1.0;
            _zoom = Clamp(scale, MinZoom, MaxZoom);
            _fitMode = true;

            CenterImage();
        }

        /// <summary>原始大小：1 图片像素 = 1 DIP，并居中。</summary>
        public void SetActualSize()
        {
            if (_pixelWidth <= 0 || _pixelHeight <= 0) return;
            CancelZoomAnimation();

            _zoom = 1.0;
            _fitMode = false;
            CenterImage();
        }

        /// <summary>把图片包围盒的几何中心对到视口中心。</summary>
        private void CenterImage()
        {
            double vw = Math.Max(1, ActualWidth);
            double vh = Math.Max(1, ActualHeight);

            var centerInImage = new Point(_pixelWidth / 2.0, _pixelHeight / 2.0);
            var centerAfterRotateScale = ScaleRotateOnly().Transform(centerInImage);
            _translation = new Vector(vw / 2 - centerAfterRotateScale.X, vh / 2 - centerAfterRotateScale.Y);

            ClampAndApply();
        }

        /// <summary>顺时针旋转 90°（绕当前可见图片包围盒的中心，避免内容跳出视野）。</summary>
        public void RotateClockwise() => RotateBy(90);

        /// <summary>逆时针旋转 90°。</summary>
        public void RotateCounterClockwise() => RotateBy(-90);

        /// <summary>按给定角度旋转（正值顺时针），旋转轴取当前图片包围盒中心。</summary>
        public void RotateBy(double degrees)
        {
            if (_pixelWidth <= 0) return;
            CancelZoomAnimation();

            var bounds = GetImageBoundsInView();
            var pivot = bounds.IsEmpty
                ? new Point(ActualWidth / 2, ActualHeight / 2)
                : new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            var imagePivot = ViewportToImage(pivot);

            _rotation = NormalizeAngle(_rotation + degrees);
            _fitMode = false;
            RebuildViewKeepingPointFixed(imagePivot, pivot);
        }

        /// <summary>平移视图（视口 DIP 增量）。</summary>
        public void PanBy(Vector delta)
        {
            if (_pixelWidth <= 0) return;
            if (delta.X == 0 && delta.Y == 0) return;

            _translation += delta;
            // 用户主动平移即视为脱离「适应窗口」。
            _fitMode = false;
            ClampAndApply();
        }

        /// <summary>视口坐标 → 图片像素坐标。</summary>
        public Point ViewportToImage(Point viewportPoint)
        {
            var inverse = _imageTransform.Matrix;
            if (!inverse.HasInverse) return viewportPoint;
            inverse.Invert();
            return inverse.Transform(viewportPoint);
        }

        /// <summary>图片像素坐标 → 视口坐标。</summary>
        public Point ImageToViewport(Point imagePoint) => _imageTransform.Matrix.Transform(imagePoint);

        /// <summary>某个视口点是否落在图片内容上（用于区分「图上书写」与「空白拖动窗口」）。</summary>
        public bool IsPointOverImage(Point viewportPoint)
        {
            if (_pixelWidth <= 0 || _pixelHeight <= 0) return false;

            var p = ViewportToImage(viewportPoint);
            return p.X >= 0 && p.Y >= 0 && p.X <= _pixelWidth && p.Y <= _pixelHeight;
        }

        // ---------------- 交互变形质量 ----------------

        /// <summary>开始交互（拖动/缩放/旋转）：降低重采样质量以保证流畅。</summary>
        public void BeginInteractiveTransform()
        {
            if (_interactive) return;
            _interactive = true;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.LowQuality);
        }

        /// <summary>结束交互：恢复高质量重采样。</summary>
        public void EndInteractiveTransform()
        {
            if (!_interactive) return;
            _interactive = false;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        }

        // ---------------- 视图矩阵 ----------------

        /// <summary>是否处于 90°/270° 的「立起来」状态（适配计算需要互换宽高）。</summary>
        private bool IsQuarterTurned
        {
            get
            {
                double a = NormalizeAngle(_rotation) % 180;
                return a > 45 && a < 135;
            }
        }

        private Matrix ScaleRotateOnly()
        {
            var m = new Matrix();
            m.Scale(_zoom, _zoom);
            m.Rotate(_rotation);
            return m;
        }

        /// <summary>按当前缩放/旋转 + 给定平移构造完整视图矩阵。</summary>
        private Matrix BuildViewMatrix(Vector translation)
        {
            var m = ScaleRotateOnly();
            m.Translate(translation.X, translation.Y);
            return m;
        }

        /// <summary>施加新的平移量，并把差异烘焙进墨迹与图片。</summary>
        private void ApplyTranslation(Vector translation)
        {
            _translation = translation;
            ApplyViewMatrix(BuildViewMatrix(translation));
        }

        /// <summary>
        /// 把「图片坐标 <paramref name="imageAnchor"/> ↔ 视口坐标 <paramref name="viewportAnchor"/>」
        /// 的对应关系固定住，据此反推平移量（缩放/旋转的锚点保持不动都靠这一步）。
        /// </summary>
        private void RebuildViewKeepingPointFixed(Point imageAnchor, Point viewportAnchor)
        {
            var mapped = ScaleRotateOnly().Transform(imageAnchor);
            _translation = new Vector(viewportAnchor.X - mapped.X, viewportAnchor.Y - mapped.Y);

            // 走夹紧而不是直接施加：捏合、以光标为中心缩放、旋转都必须同样遵守
            // 「图片不留缝」约束，否则手势会把手里的图片直接推出视野。
            ClampAndApply();
        }

        /// <summary>把视图矩阵同时应用到图片渲染与墨迹（墨迹走基类的点集变换）。</summary>
        private void ApplyViewMatrix(Matrix newMatrix)
        {
            var previous = _imageTransform.Matrix;

            // 视口空间的增量：先还原到「上一次的图片坐标」，再按新矩阵放回去。
            // 基类的渐隐层累积同一增量，因此二者始终一致（起点都是单位矩阵）。
            if (previous.HasInverse && !MatrixEquals(previous, newMatrix))
            {
                var delta = previous;
                delta.Invert();
                delta *= newMatrix;
                ApplyInkTransform(delta);
            }

            _imageTransform.Matrix = newMatrix;
            ViewChanged?.Invoke();
        }

        /// <summary>
        /// 把视图夹紧到「图片不留缝」：图片比视口小时整体居中，比视口大时不允许拖出空边。
        /// 这既是绝大多数看图软件的手感，也保证批注区域始终对着图片内容。
        /// </summary>
        private void ClampAndApply()
        {
            if (!ConstrainPanning)
            {
                ApplyTranslation(_translation);
                return;
            }

            var bounds = GetBoundsInView(BuildViewMatrix(_translation));
            if (bounds.IsEmpty)
            {
                ApplyTranslation(_translation);
                return;
            }

            double vw = Math.Max(1, ActualWidth);
            double vh = Math.Max(1, ActualHeight);
            var translation = _translation;

            if (bounds.Width <= vw)
            {
                translation.X += (vw - bounds.Width) / 2 - bounds.Left;
            }
            else if (bounds.Left > 0)
            {
                translation.X -= bounds.Left;
            }
            else if (bounds.Right < vw)
            {
                translation.X += vw - bounds.Right;
            }

            if (bounds.Height <= vh)
            {
                translation.Y += (vh - bounds.Height) / 2 - bounds.Top;
            }
            else if (bounds.Top > 0)
            {
                translation.Y -= bounds.Top;
            }
            else if (bounds.Bottom < vh)
            {
                translation.Y += vh - bounds.Bottom;
            }

            ApplyTranslation(translation);
        }

        /// <summary>当前视图下图片的轴对齐包围盒（视口坐标）。</summary>
        public Rect GetImageBoundsInView() => GetBoundsInView(_imageTransform.Matrix);

        private Rect GetBoundsInView(Matrix matrix)
        {
            if (_pixelWidth <= 0 || _pixelHeight <= 0) return Rect.Empty;

            var p0 = matrix.Transform(new Point(0, 0));
            var p1 = matrix.Transform(new Point(_pixelWidth, 0));
            var p2 = matrix.Transform(new Point(_pixelWidth, _pixelHeight));
            var p3 = matrix.Transform(new Point(0, _pixelHeight));

            double minX = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
            double maxX = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
            double minY = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
            double maxY = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        // ---------------- 鼠标手势 ----------------

        /// <summary>
        /// 该次按下是否应被视为「平移图片」：中键、右键、按住空格/Ctrl 的左键，
        /// 或者<b>非批注状态下的左键</b>（鼠标/漫游模式下拖动即平移图片，与触摸单指行为一致）。
        /// 批注状态下的左键落在图片上时留给 InkCanvas 书写。
        /// </summary>
        private bool IsPanGesture(MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Right) return true;
            if (e.ChangedButton != MouseButton.Left) return false;

            if (Keyboard.IsKeyDown(Key.Space)
                || Keyboard.IsKeyDown(Key.LeftCtrl)
                || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                return true;
            }

            return IsIdleForPanGestures;
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsPanGesture(e)) return;
            if (_pixelWidth <= 0) return;

            // 画布内部的交互控件（撤销/恢复圆按钮、提示条关闭按钮）必须优先拿到点击：
            // 隧道路由下本处理器先于它们执行，不做这个判断会把它们的点击整口吃掉
            // —— 非批注状态下「拖动即平移」会让撤消按钮彻底失效。
            if (IsInteractiveChild(e.OriginalSource as DependencyObject)) return;

            BeginPanGesture(e.GetPosition(this));
            e.Handled = true;
        }

        /// <summary>
        /// 该事件源是否位于画布内部的交互控件上（按钮/滑杆/滚动条等）。
        /// 用于把「拖动平移」与「点击内部控件」区分开。
        /// </summary>
        private bool IsInteractiveChild(DependencyObject source)
        {
            for (var node = source; node != null && !ReferenceEquals(node, this); node = GetParentNode(node))
            {
                if (node is ButtonBase || node is Slider || node is ScrollBar || node is TextBoxBase)
                    return true;
            }

            return false;
        }

        private static DependencyObject GetParentNode(DependencyObject node)
        {
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
            {
                var parent = VisualTreeHelper.GetParent(node);
                if (parent != null) return parent;
            }

            return LogicalTreeHelper.GetParent(node);
        }

        private void BeginPanGesture(Point position)
        {
            _panning = true;
            _panLastPoint = position;
            CancelZoomAnimation();

            // 关键：切到 None 才能真正阻止 InkCanvas 收集笔迹。
            // 只把 PreviewMouseDown 标记为 Handled 是不够的 —— WPF 的鼠标会在输入管理器
            // 层面被提升为触笔事件，InkCanvas 仍可能落墨。
            _editingModeBeforePan = InkSurface.EditingMode;
            InkSurface.EditingMode = InkCanvasEditingMode.None;

            BeginInteractiveTransform();
            CaptureMouse();
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_panning) return;

            var position = e.GetPosition(this);
            var delta = position - _panLastPoint;
            if (delta.X == 0 && delta.Y == 0) return;

            _panLastPoint = position;
            _translation += delta;
            _fitMode = false;
            ClampAndApply();
            e.Handled = true;
        }

        private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_panning) return;
            EndPanGesture();
            e.Handled = true;
        }

        private void EndPanGesture()
        {
            if (!_panning) return;
            _panning = false;

            InkSurface.EditingMode = _editingModeBeforePan;
            EndInteractiveTransform();
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_pixelWidth <= 0) return;

            double steps = e.Delta / 120.0;
            if (Math.Abs(steps) < 1e-6) return;

            // 平滑缩放：滚轮只更新目标倍率，实际变换由缓动逐帧逼近。
            AnimateZoom(Math.Pow(WheelZoomFactor, steps), e.GetPosition(this));
            e.Handled = true;
        }

        // ---------------- 触摸手势（单指平移 / 双指缩放平移） ----------------

        /// <summary>
        /// 触摸按下：只登记位置，<b>不</b>设 Handled ——
        /// 基类挂在 InkCanvas 上的多指抑制与手掌擦必须照常收到事件。
        /// </summary>
        private void OnPreviewTouchDown(object sender, TouchEventArgs e)
        {
            if (_pixelWidth <= 0) return;

            // 落在画布内部交互控件（撤销/恢复按钮）上的手指不参与平移缩放，
            // 否则「按住按钮稍微一滑」会既触发按钮又带动图片。
            if (IsInteractiveChild(e.OriginalSource as DependencyObject)) return;

            var position = e.GetTouchPoint(this).Position;
            _touchPoints[e.TouchDevice.Id] = position;

            if (_touchPoints.Count >= 2) BeginTouchGesture();
            else
            {
                _singleTouchLastPoint = position;
                CancelZoomAnimation();
            }
        }

        private void OnPreviewTouchMove(object sender, TouchEventArgs e)
        {
            int id = e.TouchDevice.Id;
            if (!_touchPoints.ContainsKey(id)) return;

            var position = e.GetTouchPoint(this).Position;
            _touchPoints[id] = position;

            // 擦除模式下手指同样要带出擦头框（此时 IsIdleForPanGestures 为 false，
            // 下面的平移分支不会执行，擦除交给 InkCanvas 原生擦除）。
            UpdateEraserFootprint(position);

            if (_touchPoints.Count >= 2)
            {
                if (!_touchGestureActive) BeginTouchGesture();
                UpdateTouchGesture();
                e.Handled = true;
                return;
            }

            // 单指：仅在非批注状态下平移图片；批注状态下把这一指留给书写。
            if (_touchPoints.Count == 1 && IsIdleForPanGestures)
            {
                var delta = position - _singleTouchLastPoint;
                _singleTouchLastPoint = position;
                if (delta.X == 0 && delta.Y == 0) return;

                if (!_singleTouchPanning)
                {
                    _singleTouchPanning = true;
                    BeginInteractiveTransform();
                }

                _translation += delta;
                _fitMode = false;
                ClampAndApply();
                e.Handled = true;
            }
        }

        private void OnPreviewTouchUp(object sender, TouchEventArgs e)
        {
            _touchPoints.Remove(e.TouchDevice.Id);

            if (_touchPoints.Count < 2) EndTouchGesture();
            if (_touchPoints.Count == 0) EndSingleTouchPan();
        }

        private void BeginTouchGesture()
        {
            _touchGestureActive = true;
            _singleTouchPanning = false;
            CancelZoomAnimation();
            BeginInteractiveTransform();

            TryGetTouchCentroidAndSpread(out _touchLastCentroid, out _touchLastSpread);
        }

        private void EndTouchGesture()
        {
            if (!_touchGestureActive) return;
            _touchGestureActive = false;
            EndInteractiveTransform();
        }

        private void EndSingleTouchPan()
        {
            if (!_singleTouchPanning) return;
            _singleTouchPanning = false;
            EndInteractiveTransform();
        }

        /// <summary>
        /// 双指手势：质心位移 = 平移，平均离散度之比 = 缩放。
        /// <para>
        /// 两者合并成一次「保持某个图片点不动」的视图重建，而不是先平移再缩放：
        /// 分两步做会让缩放锚点用到已经过期的质心，手指快速捏合时会出现内容「追不上」的漂移。
        /// </para>
        /// </summary>
        private void UpdateTouchGesture()
        {
            if (!TryGetTouchCentroidAndSpread(out var centroid, out double spread)) return;

            // 锚点必须用「上一次的质心」：此刻 _touchPoints 已经是新位置，
            // 只有 _touchLastCentroid 还代表上一步的落点。
            if (_touchLastSpread > 1 && spread > 1)
            {
                var imageAnchor = ViewportToImage(_touchLastCentroid);
                double ratio = spread / _touchLastSpread;
                _zoom = Clamp(_zoom * ratio, MinZoom, MaxZoom);
                _fitMode = false;
                RebuildViewKeepingPointFixed(imageAnchor, centroid);
            }

            _touchLastCentroid = centroid;
            _touchLastSpread = spread;
        }

        private bool TryGetTouchCentroidAndSpread(out Point centroid, out double spread)
        {
            centroid = default;
            spread = 0;
            if (_touchPoints.Count < 2) return false;

            double sumX = 0, sumY = 0;
            foreach (var point in _touchPoints.Values)
            {
                sumX += point.X;
                sumY += point.Y;
            }

            centroid = new Point(sumX / _touchPoints.Count, sumY / _touchPoints.Count);

            double sumSpread = 0;
            foreach (var point in _touchPoints.Values)
            {
                double dx = point.X - centroid.X;
                double dy = point.Y - centroid.Y;
                sumSpread += Math.Sqrt(dx * dx + dy * dy);
            }

            spread = sumSpread / _touchPoints.Count;
            return true;
        }

        // ---------------- 小工具 ----------------

        private static double Clamp(double value, double min, double max)
            => value < min ? min : value > max ? max : value;

        /// <summary>把角度规范化到 [0, 360)。</summary>
        public static double NormalizeAngle(double degrees)
        {
            double a = degrees % 360;
            if (a < 0) a += 360;
            return a;
        }

        private static bool MatrixEquals(Matrix a, Matrix b)
            => Math.Abs(a.M11 - b.M11) < 1e-12 && Math.Abs(a.M12 - b.M12) < 1e-12
               && Math.Abs(a.M21 - b.M21) < 1e-12 && Math.Abs(a.M22 - b.M22) < 1e-12
               && Math.Abs(a.OffsetX - b.OffsetX) < 1e-9 && Math.Abs(a.OffsetY - b.OffsetY) < 1e-9;
    }
}
