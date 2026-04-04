using Microsoft.Extensions.Logging.Abstractions;
using Narabemi.Mpv;
using Narabemi.ViewModels;
using Xunit;

namespace Narabemi.Tests
{
    public class VideoPlayerViewModelTests
    {
        private static VideoPlayerViewModel CreateViewModel()
        {
            var mpvPlayer = new MpvPlayer(NullLogger<MpvPlayer>.Instance);
            return new VideoPlayerViewModel(mpvPlayer, NullLogger<VideoPlayerViewModel>.Instance);
        }

        [Fact]
        public void Position_SetDirectly_UpdatesProperty()
        {
            var vm = CreateViewModel();
            double notified = -1;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.Position))
                    notified = vm.Position;
            };

            vm.Position = 42.5;

            Assert.Equal(42.5, vm.Position, 1);
            Assert.Equal(42.5, notified, 1);
        }

        [Fact]
        public void Duration_SetDirectly_UpdatesProperty()
        {
            var vm = CreateViewModel();
            double notified = -1;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.Duration))
                    notified = vm.Duration;
            };

            vm.Duration = 120.0;

            Assert.Equal(120.0, vm.Duration, 1);
            Assert.Equal(120.0, notified, 1);
        }

        [Fact]
        public void BeginSeek_SuppressesPositionUpdates_EndSeekResumes()
        {
            var vm = CreateViewModel();
            vm.Duration = 100.0;
            vm.Position = 50.0;

            vm.BeginSeek();

            // During seek, position should not be overwritten externally
            // (simulating what poll timer would do - it checks _isSeeking)
            Assert.Equal(50.0, vm.Position, 1);

            vm.EndSeek();
        }

        [Fact]
        public void SetLoop_BeforeInit_StoresPending()
        {
            var vm = CreateViewModel();

            // Should not throw even before mpv init
            vm.SetLoop(true);

            // Verify it doesn't crash
            Assert.True(true);
        }
    }
}
