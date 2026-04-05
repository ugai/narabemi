using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Narabemi.UI.Windows
{
    /// <summary>
    /// VersionWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class VersionWindow : Window
    {
        public VersionWindow(VersionWindowViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            Utils.FixModernWpfSizeSizeToContentUIGlitch(this);
        }

        private void CloseCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e) => Close();
        private void CloseCommandBinding_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
    }

    [INotifyPropertyChanged]
    public partial class VersionWindowViewModel
    {
        [ObservableProperty]
        private string versionText = string.Empty;

        [ObservableProperty]
        private Uri siteUrl = new Uri("about:blank");

        [RelayCommand]
        private void OpenUrl(Uri uri)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });
        }
    }
}
