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
    /// </summary>
    public sealed class IndependentCanvas : UserControl
    {
        /// <summary>向外延伸的滚动条条带厚度（DIP）。</summary>
        private const double ScrollBarThickness = 24;

        private static readonly TimeSpan ScrollAnimDuration = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan NoticeAutoCloseDelay = TimeSpan.FromSeconds(10);

        private readonly Grid _rootGrid;
        private readonly Border _contentHost;
        private readonly InkCanvas _inkCanvas;
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

            // 无内部填充色：保持透明，避免影响鼠标模式下的穿透视觉。
            _contentHost = new Border
            {
                Child = _inkCanvas,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
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
            double targetW = _viewportW + (_showVerticalBar ? ScrollBarThickness : 0);
            double targetH = _viewportH + (_showHorizontalBar ? ScrollBarThickness : 0);
            if (Width != targetW) Width = targetW;
            if (Height != targetH) Height = targetH;

            UpdateOverlayVisibility();
        }

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

            if (!animate || _snapNextScrollSync)
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
            ApplyToScrollBar(_verticalScrollBar, 0, _viewportW, 1);
            ApplyToScrollBar(_horizontalScrollBar, 0, _viewportW, 1);
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
            if (_inkCanvas.Strokes.Count == 0) return;

            var matrix = new Matrix(1, 0, 0, 1, shift.X, shift.Y);
            foreach (Stroke stroke in _inkCanvas.Strokes)
                stroke.Transform(matrix, false);
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
        /// 裁剪内容宿主（墨迹、边框）的被遮挡区域。仅影响渲染（OpacityMask 实现），
        /// 不影响命中测试：被短暂遮挡（如录屏软件覆盖层）时输入仍由本画布接收，
        /// 墨迹不会误落到主画布。传 null 清除裁剪。
        /// </summary>
        public void SetClip(Geometry clip)
        {
            if (clip == null)
            {
                if (_contentHost.OpacityMask == null) return;
                _contentHost.OpacityMask = null;
                return;
            }

            // 白色几何 = 可见区域，其余透明 = 遮挡区域；几何坐标即内容宿主本地坐标。
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
        /// 清空当前可视区域内的墨迹（可见区域即本画布当前视口），保留视口之外的墨迹。
        /// 因滚动同步通过 stroke.Transform 令墨迹始终处于「当前视口坐标」，故只需与
        /// (0,0,视口宽,视口高) 求交即可。清除动作进入本画布撤销历史，可撤销恢复。
        /// </summary>
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
        /// 独立画布专用的轻量墨迹撤销/恢复历史：监听 StrokesChanged 记录增/删操作，
        /// 撤销即反向应用。仅作用于本画布，不影响主画布 TimeMachine。
        /// </summary>
        private sealed class IndependentInkHistory
        {
            private const int MaxHistoryCount = 200;

            private readonly InkCanvas _surface;
            private readonly System.Collections.Generic.List<StrokeCollectionChangedEventArgs> _undo
                = new System.Collections.Generic.List<StrokeCollectionChangedEventArgs>();
            private readonly System.Collections.Generic.List<StrokeCollectionChangedEventArgs> _redo
                = new System.Collections.Generic.List<StrokeCollectionChangedEventArgs>();
            private bool _applying;

            public event Action HistoryChanged;

            public IndependentInkHistory(InkCanvas surface)
            {
                _surface = surface;
                surface.Strokes.StrokesChanged += OnStrokesChanged;
            }

            public bool CanUndo => _undo.Count > 0;
            public bool CanRedo => _redo.Count > 0;

            private void OnStrokesChanged(object sender, StrokeCollectionChangedEventArgs e)
            {
                if (_applying) return;
                if ((e.Added?.Count ?? 0) == 0 && (e.Removed?.Count ?? 0) == 0) return;

                _undo.Add(new StrokeCollectionChangedEventArgs(
                    e.Added != null ? new StrokeCollection(e.Added) : new StrokeCollection(),
                    e.Removed != null ? new StrokeCollection(e.Removed) : new StrokeCollection()));
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
                    if (op.Added.Count > 0) _surface.Strokes.Remove(op.Added);
                    if (op.Removed.Count > 0) _surface.Strokes.Add(op.Removed);
                    _redo.Add(op);
                }
                finally
                {
                    _applying = false;
                }
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
                    if (op.Removed.Count > 0) _surface.Strokes.Remove(op.Removed);
                    if (op.Added.Count > 0) _surface.Strokes.Add(op.Added);
                    _undo.Add(op);
                }
                finally
                {
                    _applying = false;
                }
                HistoryChanged?.Invoke();
            }

            public void Clear()
            {
                if (_undo.Count == 0 && _redo.Count == 0) return;
                _undo.Clear();
                _redo.Clear();
                HistoryChanged?.Invoke();
            }
        }
    }
}
