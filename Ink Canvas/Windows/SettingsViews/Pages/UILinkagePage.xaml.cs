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
            CardUILinkageDebug.IsOn = settings.IsDebugEnabled;
            _isLoaded = true;
        }

        private void CardUILinkageMaster_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Settings.UILinkage.IsEnabled = CardUILinkageMaster.IsOn;
            SettingsManager.SaveSettingsToFile();
        }

        private void CardUILinkageWord_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Settings.UILinkage.WordEnabled = CardUILinkageWord.IsOn;
            SettingsManager.SaveSettingsToFile();
        }

        private void CardUILinkageDebug_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            SettingsManager.Settings.UILinkage.IsDebugEnabled = CardUILinkageDebug.IsOn;
            SettingsManager.SaveSettingsToFile();
        }
    }
}