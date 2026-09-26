using Ink_Canvas.Helpers;
using Ink_Canvas.Properties;
using Ink_Canvas.Windows.SettingsViews.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Page = iNKORE.UI.WPF.Modern.Controls.Page;

namespace Ink_Canvas.Windows.SettingsViews.Pages
{
    /// <summary>
    /// 「图片查看器」设置页：绑定常见图片格式到本程序、写入系统「打开方式」关联、
    /// 以及与查看器窗口行为相关的开关。
    /// </summary>
    public partial class ImageViewerPage : Page
    {
        private bool _isLoaded;

        /// <summary>格式分组 → 该组的复选框（重新绑定列表后重建）。</summary>
        private readonly List<(ImageViewerFormats.FormatGroup Group, CheckBox Box)> _formatRows
            = new List<(ImageViewerFormats.FormatGroup, CheckBox)>();

        /// <summary>重新渲染列表期间为 true，避免 CheckBox 事件回写设置。</summary>
        private bool _suppressFormatEvents;

        public ImageViewerPage()
        {
            InitializeComponent();
            Loaded += ImageViewerPage_Loaded;
            Unloaded += ImageViewerPage_Unloaded;
        }

        private Ink_Canvas.ImageViewerSettings ViewerSettings => SettingsManager.Settings.ImageViewer;

        private void ImageViewerPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
            _isLoaded = true;
        }

        private void ImageViewerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
        }

        // ---------------- 载入 ----------------

        private void LoadSettings()
        {
            _isLoaded = false;

            var settings = ViewerSettings;
            // 兼容旧配置：把绑定列表规范化（去重、含点、小写、按清单顺序）。
            ViewerSettings.BoundExtensions = ImageViewerFormats.NormalizeBoundList(ViewerSettings.BoundExtensions);

            CardImageViewerMaster.IsOn = ViewerSettings.IsEnabled;
            CardRegisterFileAssociation.IsOn = ViewerSettings.RegisterFileAssociation;
            CardMultiInstance.IsOn = ViewerSettings.MultiInstance;
            CardShowInTaskbar.IsOn = ViewerSettings.ShowInTaskbar;
            CardRememberWindowPlacement.IsOn = ViewerSettings.RememberWindowPlacement;
            CardOpenFitToWindow.IsOn = ViewerSettings.FitToWindowOnOpen;
            CardAutoSaveAnnotations.IsOn = ViewerSettings.AutoSaveInk;

            UpdateBackgroundColorPreview();
            RebuildFormatList();
            UpdateEnabledState();
            UpdateAssociationStatus();

            _isLoaded = true;
        }

        /// <summary>总开关决定其余卡片是否可用；未启用时给出显式提示。</summary>
        private void UpdateEnabledState()
        {
            bool enabled = CardImageViewerMaster.IsOn;
            InfoBarDisabled.IsOpen = !enabled;
        }

        /// <summary>按固化的常见格式清单重建勾选列表（不会写入设置）。</summary>
        private void RebuildFormatList()
        {
            _suppressFormatEvents = true;
            try
            {
                FormatListPanel.Children.Clear();
                _formatRows.Clear();

                var bound = new HashSet<string>(ViewerSettings.BoundExtensions ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

                foreach (var group in ImageViewerFormats.CommonGroups)
                {
                    var checkBox = new CheckBox
                    {
                        Tag = group,
                        Margin = new Thickness(0, 2, 0, 2),
                        IsEnabled = group.IsDecodable,
                        VerticalContentAlignment = VerticalAlignment.Center,
                    };

                    string text = $"{group.DisplayName}（{group.ExtensionsText}）";
                    if (!group.IsDecodable) text += ImageViewerStrings.FormatsUnsupportedSuffix;
                    checkBox.Content = text;

                    int boundCount = group.Extensions.Count(bound.Contains);
                    // 三态仅用于「部分绑定」的显示；点击时会被强制回落到确定状态。
                    checkBox.IsThreeState = true;
                    checkBox.IsChecked = boundCount == 0 ? false : boundCount == group.Extensions.Count ? true : (bool?)null;
                    checkBox.Click += FormatCheckBox_Click;

                    FormatListPanel.Children.Add(checkBox);
                    _formatRows.Add((group, checkBox));
                }
            }
            finally
            {
                _suppressFormatEvents = false;
            }
        }

        // ---------------- 总开关 ----------------

        private void CardImageViewerMaster_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;

            ViewerSettings.IsEnabled = CardImageViewerMaster.IsOn;
            SettingsManager.SaveSettingsToFile();
            UpdateEnabledState();

            // 打开时若已绑定格式且开启了「写入关联」，顺手补齐注册表；关闭时不动注册表，
            // 避免用户只是临时关掉查看器就把系统关联一起拆掉（拆除有专门的按钮）。
            if (ViewerSettings.IsEnabled && ViewerSettings.RegisterFileAssociation && ViewerSettings.BoundExtensions.Count > 0)
            {
                ApplyAssociations(showResult: false);
            }

            UpdateAssociationStatus();
        }

        // ---------------- 格式绑定 ----------------

        private void FormatCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded || _suppressFormatEvents) return;
            if (sender is not CheckBox checkBox || checkBox.Tag is not ImageViewerFormats.FormatGroup group) return;

            // Click 在 WPF 里是**状态已经切换之后**才触发的，所以此刻的 IsChecked 就是用户意图，
            // 不能再取反 —— 之前写成取反，导致「点一下勾上、处理器马上又把它取消」，表现为勾不上。
            // 三态（部分绑定显示 null）时 IsChecked != true，按「取消该组」处理并把值落回确定的 false，
            // 避免用户点出一个既非选也非不选的状态。
            bool target = checkBox.IsChecked == true;
            checkBox.IsChecked = target;

            SetGroupBound(group, target);
        }

        /// <summary>把一组扩展名同时加入 / 移出绑定列表（同时维护注册表关联）。</summary>
        private void SetGroupBound(ImageViewerFormats.FormatGroup group, bool bound)
        {
            var settings = ViewerSettings;
            var list = new HashSet<string>(ViewerSettings.BoundExtensions ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var extension in group.Extensions)
            {
                if (bound)
                {
                    if (!group.IsDecodable) continue;
                    list.Add(extension);
                }
                else
                {
                    list.Remove(extension);
                }
            }

            ViewerSettings.BoundExtensions = ImageViewerFormats.NormalizeBoundList(list);
            SettingsManager.SaveSettingsToFile();

            if (ViewerSettings.IsEnabled && ViewerSettings.RegisterFileAssociation)
            {
                foreach (var extension in group.Extensions)
                {
                    if (bound && group.IsDecodable)
                        FileAssociationManager.RegisterImageViewerAssociation(
                            extension, ImageViewerFormats.DisplayNameFor(extension));
                    else if (!bound)
                        FileAssociationManager.UnregisterImageViewerAssociation(extension);
                }
            }

            RebuildFormatList();
            UpdateAssociationStatus();
        }

        private void BtnSelectAllFormats_Click(object sender, RoutedEventArgs e)
        {
            var settings = ViewerSettings;
            ViewerSettings.BoundExtensions = ImageViewerFormats.BindableExtensions.ToList();
            SettingsManager.SaveSettingsToFile();

            RebuildFormatList();
            if (ViewerSettings.IsEnabled && ViewerSettings.RegisterFileAssociation) ApplyAssociations(showResult: false);
            UpdateAssociationStatus();
        }

        private void BtnClearAllFormats_Click(object sender, RoutedEventArgs e)
        {
            var settings = ViewerSettings;
            var previous = (ViewerSettings.BoundExtensions ?? new List<string>()).ToList();
            ViewerSettings.BoundExtensions = new List<string>();
            SettingsManager.SaveSettingsToFile();

            RebuildFormatList();
            if (ViewerSettings.IsEnabled && ViewerSettings.RegisterFileAssociation)
            {
                foreach (var extension in previous)
                    FileAssociationManager.UnregisterImageViewerAssociation(extension);
            }

            UpdateAssociationStatus();
        }

        private void BtnAddCustomFormat_Click(object sender, RoutedEventArgs e) => TryAddCustomFormat();

        private void TextBoxCustomFormat_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            TryAddCustomFormat();
            e.Handled = true;
        }

        private void TryAddCustomFormat()
        {
            string raw = TextBoxCustomFormat.Text;
            if (string.IsNullOrWhiteSpace(raw)) return;

            string extension = ImageViewerFormats.Normalize(raw);
            // 形如 ".png"：至少「点 + 一个字符」，且不含路径分隔符与空格。
            bool invalid = extension.Length < 2
                           || extension.IndexOfAny(new[] { '\\', '/', ' ', '*', '?', '"', '<', '>', '|', ':' }) >= 0;
            if (invalid)
            {
                ShowPageMessage(ImageViewerStrings.CustomFormatInvalid, iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Warning);
                return;
            }

            var settings = ViewerSettings;
            var list = new HashSet<string>(ViewerSettings.BoundExtensions ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (!list.Add(extension))
            {
                ShowPageMessage(ImageViewerStrings.CustomFormatDuplicate, iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Informational);
                return;
            }

            ViewerSettings.BoundExtensions = ImageViewerFormats.NormalizeBoundList(list);
            SettingsManager.SaveSettingsToFile();
            TextBoxCustomFormat.Text = string.Empty;

            RebuildFormatList();
            if (ViewerSettings.IsEnabled && ViewerSettings.RegisterFileAssociation)
            {
                FileAssociationManager.RegisterImageViewerAssociation(
                    extension, ImageViewerFormats.DisplayNameFor(extension));
            }

            ShowPageMessage(
                string.Format(ImageViewerStrings.CustomFormatAdded, extension),
                ImageViewerFormats.IsDecodable(extension)
                    ? iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Success
                    : iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Warning);
            UpdateAssociationStatus();
        }

        // ---------------- 系统关联 ----------------

        private void CardRegisterFileAssociation_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;

            ViewerSettings.RegisterFileAssociation = CardRegisterFileAssociation.IsOn;
            SettingsManager.SaveSettingsToFile();

            if (ViewerSettings.IsEnabled && ViewerSettings.RegisterFileAssociation) ApplyAssociations(showResult: false);
            UpdateAssociationStatus();
        }

        private void BtnApplyAssociation_Click(object sender, RoutedEventArgs e)
        {
            if (ViewerSettings.BoundExtensions.Count == 0)
            {
                ShowPageMessage(ImageViewerStrings.AssociationNoneBound, iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Warning);
                return;
            }

            ApplyAssociations(showResult: true);
            UpdateAssociationStatus();
        }

        /// <summary>把当前绑定列表整体写入注册表。</summary>
        private void ApplyAssociations(bool showResult)
        {
            var bound = ViewerSettings.BoundExtensions ?? new List<string>();
            var result = FileAssociationManager.ApplyImageViewerAssociations(
                bound, ImageViewerFormats.DisplayNameFor);

            if (showResult)
            {
                ShowPageMessage(
                    string.Format(ImageViewerStrings.AssociationWriteResult, result.Succeeded, result.Failed),
                    result.Failed == 0
                        ? iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Success
                        : iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Warning);
            }
        }

        private void BtnRemoveAssociation_Click(object sender, RoutedEventArgs e)
        {
            // 用「联合」清理：既清当前绑定，也清历史上绑定过、后来被取消的格式可能残留的条目。
            var candidates = new HashSet<string>(ViewerSettings.BoundExtensions ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var extension in ImageViewerFormats.BindableExtensions) candidates.Add(extension);

            FileAssociationManager.RemoveAllImageViewerAssociations(candidates);
            ShowPageMessage(ImageViewerStrings.AssociationRemoved, iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Success);
            UpdateAssociationStatus();
        }

        private void BtnOpenDefaultApps_Click(object sender, RoutedEventArgs e)
        {
            if (!FileAssociationManager.OpenDefaultAppsSettings())
            {
                ShowPageMessage(ImageViewerStrings.DefaultAppHint, iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Warning);
            }
        }

        /// <summary>刷新底部状态行：已绑定哪些格式、其中多少已写入注册表。</summary>
        private void UpdateAssociationStatus()
        {
            var bound = ViewerSettings.BoundExtensions ?? new List<string>();
            if (bound.Count == 0)
            {
                TextAssociationStatus.Text = ImageViewerStrings.NoFormatsBound;
                return;
            }

            var written = new List<string>();
            foreach (var extension in bound)
            {
                if (FileAssociationManager.IsImageViewerAssociationRegistered(extension)) written.Add(extension);
            }

            string boundText = string.Join("、", bound);
            string status = written.Count == 0
                ? ImageViewerStrings.AssociationStatusNotWritten
                : $"{ImageViewerStrings.AssociationStatusWritten} {written.Count}/{bound.Count}";

            TextAssociationStatus.Text = $"{boundText}\n{status}";
        }

        // ---------------- 查看器窗口行为 ----------------

        private void CardMultiInstance_Toggled(object sender, RoutedEventArgs e)
        {
            Persist(s => s.MultiInstance = CardMultiInstance.IsOn);
        }

        private void CardShowInTaskbar_Toggled(object sender, RoutedEventArgs e)
        {
            Persist(s => s.ShowInTaskbar = CardShowInTaskbar.IsOn);
            // 已打开的窗口立即跟随（否则要等下次开窗才生效）。
            Ink_Canvas.Windows.ImageViewerManager.ApplyAppearanceToOpenWindows();
        }

        private void CardRememberWindowPlacement_Toggled(object sender, RoutedEventArgs e)
        {
            Persist(s => s.RememberWindowPlacement = CardRememberWindowPlacement.IsOn);
        }

        private void CardOpenFitToWindow_Toggled(object sender, RoutedEventArgs e)
        {
            Persist(s => s.FitToWindowOnOpen = CardOpenFitToWindow.IsOn);
        }

        private void CardAutoSaveAnnotations_Toggled(object sender, RoutedEventArgs e)
        {
            Persist(s => s.AutoSaveInk = CardAutoSaveAnnotations.IsOn);
        }

        private void Persist(Action<Ink_Canvas.ImageViewerSettings> apply)
        {
            if (!_isLoaded) return;
            apply(ViewerSettings);
            SettingsManager.SaveSettingsToFile();
        }

        // ---------------- 背景色 ----------------

        private void BtnPickBackgroundColor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var current = ParseColor(ViewerSettings.BackgroundColor, Color.FromRgb(0x1E, 0x1E, 0x1E));
                using (var dialog = new System.Windows.Forms.ColorDialog
                {
                    AllowFullOpen = true,
                    FullOpen = true,
                    AnyColor = true,
                    Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
                })
                {
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                    var picked = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
                    ViewerSettings.BackgroundColor = $"#FF{picked.R:X2}{picked.G:X2}{picked.B:X2}";
                    SettingsManager.SaveSettingsToFile();
                    UpdateBackgroundColorPreview();

                    // 立即应用到已经打开的查看器窗口，所见即所得。
                    Ink_Canvas.Windows.ImageViewerManager.ApplyAppearanceToOpenWindows();
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"选择图片查看器背景色失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>把留白区域背景色恢复为默认值（并把已打开的查看器窗口一起刷新）。</summary>
        private void BtnResetBackgroundColor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ViewerSettings.BackgroundColor = Ink_Canvas.ImageViewerSettings.DefaultBackgroundColor;
                SettingsManager.SaveSettingsToFile();
                UpdateBackgroundColorPreview();
                Ink_Canvas.Windows.ImageViewerManager.ApplyAppearanceToOpenWindows();
                ShowPageMessage(ImageViewerStrings.ResetBackgroundColorHint,
                    iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"恢复图片查看器默认背景色失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        private void UpdateBackgroundColorPreview()
        {
            try
            {
                BorderBackgroundColorPreview.Background = new SolidColorBrush(
                    ParseColor(ViewerSettings.BackgroundColor,
                        ImageViewerFormats.ParseColorOr(Ink_Canvas.ImageViewerSettings.DefaultBackgroundColor,
                            Color.FromRgb(0x1E, 0x1E, 0x1E))));
            }
            catch
            {
                BorderBackgroundColorPreview.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            }
        }

        /// <summary>把 <c>#AARRGGBB</c> / <c>#RRGGBB</c> / 颜色名解析为颜色，失败返回回退值。</summary>
        internal static Color ParseColor(string value, Color fallback)
            => ImageViewerFormats.ParseColorOr(value, fallback);

        // ---------------- 测试打开 ----------------

        private void BtnTestOpenImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = ImageViewerStrings.TestOpenImage,
                    Filter = ImageViewerFormats.BuildOpenFileDialogFilter(ViewerSettings),
                    CheckFileExists = true,
                    Multiselect = false,
                };

                string initialDirectory = ViewerSettings.LastFolder;
                if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
                    dialog.InitialDirectory = initialDirectory;

                if (dialog.ShowDialog() != true) return;

                Ink_Canvas.Windows.ImageViewerManager.OpenImage(dialog.FileName);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"测试打开图片失败: {ex.Message}", LogHelper.LogType.Error);
                ShowPageMessage(ex.Message, iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity.Error);
            }
        }

        // ---------------- 页内提示 ----------------

        /// <summary>在页面顶部显示一条临时提示。</summary>
        private void ShowPageMessage(string message, iNKORE.UI.WPF.Modern.Controls.InfoBarSeverity severity)
        {
            if (string.IsNullOrEmpty(message)) return;

            try
            {
                InfoBarMessage.Severity = severity;
                InfoBarMessage.Message = message;
                InfoBarMessage.IsOpen = true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"显示图片查看器设置提示失败: {message} / {ex.Message}", LogHelper.LogType.Warning);
            }
        }
    }
}
