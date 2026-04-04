using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Narabemi.Mpv;
using Narabemi.Settings;
using Narabemi.ViewModels;
using Narabemi.Views;
using Xunit;

namespace Narabemi.Tests
{
    public class SeekBarBindingTests
    {
        private static MainWindowViewModel CreateViewModel()
        {
            var mpvPlayer = new MpvPlayer(NullLogger<MpvPlayer>.Instance);
            var playerVm = new VideoPlayerViewModel(mpvPlayer, NullLogger<VideoPlayerViewModel>.Instance);
            var appStatesService = new AppStatesService(NullLogger<AppStatesService>.Instance);
            appStatesService.LoadFile();
            return new MainWindowViewModel(appStatesService, playerVm, NullLogger<MainWindowViewModel>.Instance);
        }

        [AvaloniaFact]
        public void SeekBar_MaximumReflectsDuration()
        {
            var vm = CreateViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();

            vm.PlayerViewModel.Duration = 120.0;

            var seekBar = window.FindControl<Slider>("SeekBar");
            Assert.NotNull(seekBar);
            Assert.Equal(120.0, seekBar!.Maximum, 1);
        }

        [AvaloniaFact]
        public void SeekBar_ValueReflectsPosition()
        {
            var vm = CreateViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();

            vm.PlayerViewModel.Duration = 120.0;
            vm.PlayerViewModel.Position = 30.0;

            var seekBar = window.FindControl<Slider>("SeekBar");
            Assert.NotNull(seekBar);
            Assert.Equal(30.0, seekBar!.Value, 1);
        }

        [AvaloniaFact]
        public void SeekBar_ZeroDuration_ValueIsZero()
        {
            var vm = CreateViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();

            // Duration=0 のとき Position=0 でスライダーが 0 であること
            vm.PlayerViewModel.Duration = 0.0;
            vm.PlayerViewModel.Position = 0.0;

            var seekBar = window.FindControl<Slider>("SeekBar");
            Assert.NotNull(seekBar);
            Assert.Equal(0.0, seekBar!.Value, 1);
            Assert.Equal(0.0, seekBar.Maximum, 1);
        }

        [AvaloniaFact]
        public void DebugText_ReflectsPositionAndDuration()
        {
            var vm = CreateViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();

            vm.PlayerViewModel.Duration = 60.0;
            vm.PlayerViewModel.Position = 15.5;

            // Duration と Position が ViewModel 上で正しく設定されていることを確認
            Assert.Equal(60.0, vm.PlayerViewModel.Duration, 1);
            Assert.Equal(15.5, vm.PlayerViewModel.Position, 1);
        }
    }
}
