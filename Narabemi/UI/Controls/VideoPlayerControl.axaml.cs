using System;
using Avalonia.Controls;
using Narabemi.Mpv;
using Narabemi.ViewModels;

namespace Narabemi.UI.Controls
{
    public partial class VideoPlayerControl : UserControl
    {
        public VideoPlayerControl()
        {
            InitializeComponent();

            var mpvView = this.FindControl<MpvVideoView>("MpvView");
            if (mpvView is not null)
            {
                mpvView.HandleReady += OnNativeHandleReady;
            }
        }

        private void OnNativeHandleReady(IntPtr handle)
        {
            if (DataContext is VideoPlayerViewModel vm)
            {
                vm.InitMpv(handle);
            }
        }
    }
}
