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
            var mpvPlayerA = new MpvPlayer(NullLogger<MpvPlayer>.Instance);
            var mpvPlayerB = new MpvPlayer(NullLogger<MpvPlayer>.Instance);
            var playerA = new VideoPlayerViewModel(mpvPlayerA, NullLogger<VideoPlayerViewModel>.Instance);
            var playerB = new VideoPlayerViewModel(mpvPlayerB, NullLogger<VideoPlayerViewModel>.Instance);
            var appStatesService = new AppStatesService(NullLogger<AppStatesService>.Instance);
            appStatesService.LoadFile();
            return new MainWindowViewModel(appStatesService, playerA, playerB, null, null, NullLogger<MainWindowViewModel>.Instance);
        }

        [Fact]
        public void SyncPlaybackState_PlayThenPause_SetsPause()
        {
            var vm = CreateViewModel();

            // Simulate: playing → paused (primary player = PlayerA)
            vm.PlayerA.IsPaused = false;
            vm.PlayerA.IsPaused = true;

            Assert.Equal(GlobalPlaybackState.Pause, vm.GlobalPlaybackState);
        }

        [Fact]
        public void SyncPlaybackState_PauseThenPlay_SetsPlay()
        {
            var vm = CreateViewModel();
            vm.GlobalPlaybackState = GlobalPlaybackState.Pause;

            vm.PlayerA.IsPaused = false;

            Assert.Equal(GlobalPlaybackState.Play, vm.GlobalPlaybackState);
        }

        [Fact]
        public void SyncPlaybackState_WhenStopped_DoesNotChange()
        {
            var vm = CreateViewModel();
            vm.GlobalPlaybackState = GlobalPlaybackState.Stop;

            vm.PlayerA.IsPaused = false;
            vm.PlayerA.IsPaused = true;

            Assert.Equal(GlobalPlaybackState.Stop, vm.GlobalPlaybackState);
        }
    }
}
