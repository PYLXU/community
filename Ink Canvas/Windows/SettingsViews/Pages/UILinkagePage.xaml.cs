using Ink_Canvas.Windows.SettingsViews.Helpers;
using System.Windows;
using Page = iNKORE.UI.WPF.Modern.Controls.Page;

namespace Ink_Canvas.Windows.SettingsViews.Pages
{
    public partial class UILinkagePage : Page
    {
        private bool _isLoaded = false;

        public UILinkagePage()
        {
            InitializeComponent();
            Loaded += UILinkagePage_Loaded;
            Unloaded += UILinkagePage_Unloaded;
        }

        private void UILinkagePage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
            _isLoaded = true;
        }

        private void UILinkagePage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
        }

        private void LoadSettings()
        {
            _isLoaded = false;
            var settings = SettingsManager.Settings.UILinkage;
            CardUILinkageMaster.IsOn = settings.IsEnabled;
            CardUILinkageWord.IsOn = settings.WordEnabled;
            CardUILinkageSumatraPdf.IsOn = settings.SumatraPdfEnabled;
            CardUILinkageRememberScroll.IsOn = settings.RememberScrollPosition;
            CardUILinkageAutoSaveInk.IsOn = settings.AutoSaveInk;
            CardUILinkageAutoLongScreenshot.IsOn = settings.AutoLongScreenshot;
            CardUILinkageDebug.IsOn = settings.IsDebugEnabled;
            UpdateAutomationCardsEnabledState();
            _isLoaded = true;
        }

        private void CardUILinkageMaster_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Settings.UILinkage.IsEnabled = CardUILinkageMaster.IsOn;
            SettingsManager.SaveSettingsToFile();
            UpdateAutomationCardsEnabledState();
        }

        private void CardUILinkageWord_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Settings.UILinkage.WordEnabled = CardUILinkageWord.IsOn;
            SettingsManager.SaveSettingsToFile();
            UpdateAutomationCardsEnabledState();
        }

        private void CardUILinkageSumatraPdf_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Settings.UILinkage.SumatraPdfEnabled = CardUILinkageSumatraPdf.IsOn;
            SettingsManager.SaveSettingsToFile();
            UpdateAutomationCardsEnabledState();
        }

        private void CardUILinkageRememberScroll_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Settings.UILinkage.RememberScrollPosition = CardUILinkageRememberScroll.IsOn;
            SettingsManager.SaveSettingsToFile();
        }

        private void CardUILinkageAutoSaveInk_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Settings.UILinkage.AutoSaveInk = CardUILinkageAutoSaveInk.IsOn;
            SettingsManager.SaveSettingsToFile();
        }

        private void CardUILinkageAutoLongScreenshot_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Settings.UILinkage.AutoLongScreenshot = CardUILinkageAutoLongScreenshot.IsOn;
            SettingsManager.SaveSettingsToFile();
        }

        private void CardUILinkageDebug_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Settings.UILinkage.IsDebugEnabled = CardUILinkageDebug.IsOn;
            SettingsManager.SaveSettingsToFile();
        }

        /// <summary>
        /// 「自动功能」组卡片跟随总开关与任一应用开关（滚动记忆/墨迹保存/长截屏对
        /// 所有支持联动的应用生效，任一应用开启即可用）。
        /// </summary>
        private void UpdateAutomationCardsEnabledState()
        {
            bool anyApp = CardUILinkageWord.IsOn || CardUILinkageSumatraPdf.IsOn;
            bool enabled = CardUILinkageMaster.IsOn && anyApp;
            CardUILinkageRememberScroll.IsEnabled = enabled;
            CardUILinkageAutoSaveInk.IsEnabled = enabled;
            CardUILinkageAutoLongScreenshot.IsEnabled = enabled;
        }
    }
}
