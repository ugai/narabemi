using Microsoft.Extensions.Logging.Abstractions;
using Narabemi.Mpv;
using Narabemi.Settings;
using Narabemi.ViewModels;
using Xunit;

namespace Narabemi.Tests
{
    public class MainWindowViewModelTests
    {
        private static MainWindowViewModel CreateViewModel()
        {
            var mpvPlayer = new MpvPlayer(NullLogger<MpvPlayer>.Instance);
            var playerVm = new VideoPlayerViewModel(mpvPlayer, NullLogger<VideoPlayerViewModel>.Instance);
            var appStatesService = new AppStatesService(NullLogger<AppStatesService>.Instance);
            appStatesService.LoadFile();
            return new MainWindowViewModel(appStatesService, playerVm, NullLogger<MainWindowViewModel>.Instance);
        }

        [Fact]
        public void SyncPlaybackState_PlayThenPause_SetsPause()
        {
            var vm = CreateViewModel();

            // Simulate: playing → paused
            vm.PlayerViewModel.IsPaused = false;  // now playing
            vm.PlayerViewModel.IsPaused = true;   // now paused

            Assert.Equal(GlobalPlaybackState.Pause, vm.GlobalPlaybackState);
        }

        [Fact]
        public void SyncPlaybackState_PauseThenPlay_SetsPlay()
        {
            var vm = CreateViewModel();
            vm.GlobalPlaybackState = GlobalPlaybackState.Pause;

            vm.PlayerViewModel.IsPaused = false;

            Assert.Equal(GlobalPlaybackState.Play, vm.GlobalPlaybackState);
        }

        [Fact]
        public void SyncPlaybackState_WhenStopped_DoesNotChange()
        {
            var vm = CreateViewModel();
            vm.GlobalPlaybackState = GlobalPlaybackState.Stop;

            // Ensure IsPaused actually changes (default is true, so set false first)
            vm.PlayerViewModel.IsPaused = false;
            vm.PlayerViewModel.IsPaused = true;

            Assert.Equal(GlobalPlaybackState.Stop, vm.GlobalPlaybackState);
        }
    }
}
