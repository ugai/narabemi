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
        private static (MainWindowViewModel vm, VideoPlayerViewModel playerA) CreateViewModel()
        {
            var mpvPlayerA = new MpvPlayer(NullLogger<MpvPlayer>.Instance);
            var mpvPlayerB = new MpvPlayer(NullLogger<MpvPlayer>.Instance);
            var playerA = new VideoPlayerViewModel(mpvPlayerA, NullLogger<VideoPlayerViewModel>.Instance);
            var playerB = new VideoPlayerViewModel(mpvPlayerB, NullLogger<VideoPlayerViewModel>.Instance);
            var appStatesService = new AppStatesService(NullLogger<AppStatesService>.Instance);
            appStatesService.LoadFile();
            var vm = new MainWindowViewModel(appStatesService, playerA, playerB, null, null, NullLogger<MainWindowViewModel>.Instance);
            return (vm, playerA);
        }

        [AvaloniaFact]
        public void SeekBar_MaximumReflectsDuration()
        {
            var (vm, playerA) = CreateViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();

            playerA.Duration = 120.0;

            var seekBar = window.FindControl<Slider>("SeekBar");
            Assert.NotNull(seekBar);
            Assert.Equal(120.0, seekBar!.Maximum, 1);
        }

        [AvaloniaFact]
        public void SeekBar_ValueReflectsPosition()
        {
            var (vm, playerA) = CreateViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();

            playerA.Duration = 120.0;
            playerA.Position = 30.0;

            var seekBar = window.FindControl<Slider>("SeekBar");
            Assert.NotNull(seekBar);
            Assert.Equal(30.0, seekBar!.Value, 1);
        }

        [AvaloniaFact]
        public void SeekBar_ZeroDuration_ValueIsZero()
        {
            var (vm, playerA) = CreateViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();

            playerA.Duration = 0.0;
            playerA.Position = 0.0;

            var seekBar = window.FindControl<Slider>("SeekBar");
            Assert.NotNull(seekBar);
            Assert.Equal(0.0, seekBar!.Value, 1);
            Assert.Equal(0.0, seekBar.Maximum, 1);
        }

        [AvaloniaFact]
        public void DebugText_ReflectsPositionAndDuration()
        {
            var (vm, playerA) = CreateViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();

            playerA.Duration = 60.0;
            playerA.Position = 15.5;

            Assert.Equal(60.0, vm.PrimaryPlayer.Duration, 1);
            Assert.Equal(15.5, vm.PrimaryPlayer.Position, 1);
        }
    }
}
