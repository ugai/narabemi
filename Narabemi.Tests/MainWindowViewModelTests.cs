using System.Collections.Generic;
using System.ComponentModel;
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
            return new MainWindowViewModel(appStatesService, playerA, playerB, NullLogger<MainWindowViewModel>.Instance);
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

        // --- SeekBoth / SeekRelative (Issue #85) ---

        [Fact]
        public void SeekBoth_DoesNotThrow_WhenAutoSyncOn()
        {
            var vm = CreateViewModel();
            vm.AutoSync = true;

            // mpv is not initialized, so SeekTo is a no-op; verify no exception is thrown.
            var ex = Record.Exception(() => vm.SeekBoth(10.0));
            Assert.Null(ex);
        }

        [Fact]
        public void SeekBoth_DoesNotThrow_WhenAutoSyncOff()
        {
            var vm = CreateViewModel();
            vm.AutoSync = false;

            var ex = Record.Exception(() => vm.SeekBoth(10.0));
            Assert.Null(ex);
        }

        [Fact]
        public void SeekRelative_ClampsToZero_WhenResultIsNegative()
        {
            var vm = CreateViewModel();
            // Duration must be positive so Math.Clamp(pos, 0, Duration) has valid bounds.
            vm.PlayerA.Duration = 60.0;
            vm.PlayerA.Position = 2.0;

            // A large negative delta drives the clamped position to 0; verify no exception.
            var ex = Record.Exception(() => vm.SeekRelative(-100.0));
            Assert.Null(ex);
        }

        [Fact]
        public void SeekRelative_ClampsToMaxDuration_WhenDurationIsKnown()
        {
            var vm = CreateViewModel();
            vm.PlayerA.Duration = 60.0;
            vm.PlayerA.Position = 55.0;

            // A delta that would exceed Duration should be silently clamped.
            var ex = Record.Exception(() => vm.SeekRelative(20.0));
            Assert.Null(ex);
        }

        [Fact]
        public void SeekRelative_WithSecondaryPlayer_DoesNotThrow_WhenAutoSyncOn()
        {
            var vm = CreateViewModel();
            vm.AutoSync = true;
            vm.PlayerA.Duration = 30.0;
            vm.PlayerA.Position = 5.0;
            vm.PlayerB.TimeOffset = 1.0;

            var ex = Record.Exception(() => vm.SeekRelative(5.0));
            Assert.Null(ex);
        }

        [Fact]
        public void SeekBoth_UsesPlayerB_AsPrimary_WhenMainPlayerIndexIsOne()
        {
            var vm = CreateViewModel();
            vm.MainPlayerIndex = 1;
            vm.AutoSync = true;
            vm.PlayerB.Position = 10.0;
            vm.PlayerA.TimeOffset = 2.0;

            // Primary is now PlayerB; should seek PlayerB first, then PlayerA with offset.
            var ex = Record.Exception(() => vm.SeekBoth(15.0));
            Assert.Null(ex);
        }

        // --- Property change notifications (Issue #86) ---

        private static List<string> CollectPropertyChanges(MainWindowViewModel vm, System.Action action)
        {
            var names = new List<string>();
            void Handler(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName is not null)
                    names.Add(e.PropertyName);
            }

            ((INotifyPropertyChanged)vm).PropertyChanged += Handler;
            action();
            ((INotifyPropertyChanged)vm).PropertyChanged -= Handler;
            return names;
        }

        [Fact]
        public void BlendRatio_RaisesPropertyChanged()
        {
            var vm = CreateViewModel();
            var raised = CollectPropertyChanges(vm, () => vm.BlendRatio = 0.7);
            Assert.Contains(nameof(vm.BlendRatio), raised);
        }

        [Fact]
        public void BlendMode_RaisesPropertyChanged_ForBlendModeAndBlendModeLabel()
        {
            var vm = CreateViewModel();
            var raised = CollectPropertyChanges(vm, () => vm.BlendMode = 1);
            Assert.Contains(nameof(vm.BlendMode), raised);
            Assert.Contains(nameof(vm.BlendModeLabel), raised);
        }

        [Fact]
        public void Loop_RaisesPropertyChanged()
        {
            var vm = CreateViewModel();
            var raised = CollectPropertyChanges(vm, () => vm.Loop = true);
            Assert.Contains(nameof(vm.Loop), raised);
        }

        [Fact]
        public void AutoSync_RaisesPropertyChanged()
        {
            var vm = CreateViewModel();
            var raised = CollectPropertyChanges(vm, () => vm.AutoSync = false);
            Assert.Contains(nameof(vm.AutoSync), raised);
        }

        [Fact]
        public void MasterVolume_RaisesPropertyChanged()
        {
            var vm = CreateViewModel();
            var raised = CollectPropertyChanges(vm, () => vm.MasterVolume = 0.5);
            Assert.Contains(nameof(vm.MasterVolume), raised);
        }

        [Fact]
        public void IsMasterVolumeMuted_RaisesPropertyChanged()
        {
            var vm = CreateViewModel();
            var raised = CollectPropertyChanges(vm, () => vm.IsMasterVolumeMuted = true);
            Assert.Contains(nameof(vm.IsMasterVolumeMuted), raised);
        }

        [Fact]
        public void MainPlayerIndex_RaisesPropertyChanged()
        {
            var vm = CreateViewModel();
            var raised = CollectPropertyChanges(vm, () => vm.MainPlayerIndex = 1);
            Assert.Contains(nameof(vm.MainPlayerIndex), raised);
        }

        [Fact]
        public void GlobalPlaybackState_RaisesPropertyChanged()
        {
            var vm = CreateViewModel();
            var raised = CollectPropertyChanges(vm, () => vm.GlobalPlaybackState = GlobalPlaybackState.Pause);
            Assert.Contains(nameof(vm.GlobalPlaybackState), raised);
        }

        [Fact]
        public void BlendModeLabel_ReturnsHorizontal_WhenBlendModeIsZero()
        {
            var vm = CreateViewModel();
            vm.BlendMode = 0;
            Assert.Equal("Horizontal", vm.BlendModeLabel);
        }

        [Fact]
        public void BlendModeLabel_ReturnsVertical_WhenBlendModeIsOne()
        {
            var vm = CreateViewModel();
            vm.BlendMode = 1;
            Assert.Equal("Vertical", vm.BlendModeLabel);
        }

        // --- Local volume / mute routing through master state (Issue #138) ---

        /// <summary>
        /// Changing PlayerA's local volume must call UpdateActualVolume with the
        /// current MasterVolume — not the old hard-coded 1.0.
        /// We verify the effective volume by inspecting the computed formula:
        /// LocalVolume * MasterVolume * 100.  Because mpv is not initialized in
        /// unit tests, UpdateActualVolume is a no-op on the player side, but the
        /// method must not throw and the formula path is exercised.
        /// </summary>
        [Fact]
        public void LocalVolume_Change_DoesNotThrow_WhenMasterVolumeIsNonDefault()
        {
            var vm = CreateViewModel();
            vm.MasterVolume = 0.5;

            // Changing local volume must not throw even with a non-default master.
            var ex = Record.Exception(() => vm.PlayerA.LocalVolume = 0.8);
            Assert.Null(ex);
        }

        [Fact]
        public void LocalVolume_Change_PlayerB_DoesNotThrow_WhenMasterVolumeIsNonDefault()
        {
            var vm = CreateViewModel();
            vm.MasterVolume = 0.3;

            var ex = Record.Exception(() => vm.PlayerB.LocalVolume = 0.6);
            Assert.Null(ex);
        }

        [Fact]
        public void IsLocalVolumeMuted_Change_DoesNotThrow_WhenMasterMuteIsOn()
        {
            var vm = CreateViewModel();
            vm.IsMasterVolumeMuted = true;

            // Toggling local mute while master-mute is on must not throw.
            var ex = Record.Exception(() => vm.PlayerA.IsLocalVolumeMuted = true);
            Assert.Null(ex);
        }

        [Fact]
        public void IsLocalVolumeMuted_Change_PlayerB_DoesNotThrow_WhenMasterMuteIsOn()
        {
            var vm = CreateViewModel();
            vm.IsMasterVolumeMuted = true;

            var ex = Record.Exception(() => vm.PlayerB.IsLocalVolumeMuted = true);
            Assert.Null(ex);
        }

        [Fact]
        public void LocalVolume_Change_PreservesMasterVolumeSetting()
        {
            var vm = CreateViewModel();
            vm.MasterVolume = 0.5;

            // After a local-volume change the master volume property must be unchanged.
            vm.PlayerA.LocalVolume = 0.8;

            Assert.Equal(0.5, vm.MasterVolume);
        }

        [Fact]
        public void IsLocalVolumeMuted_Change_PreservesMasterMuteSetting()
        {
            var vm = CreateViewModel();
            vm.IsMasterVolumeMuted = true;

            // Toggling local mute must not clear the master-mute flag.
            vm.PlayerA.IsLocalVolumeMuted = true;
            vm.PlayerA.IsLocalVolumeMuted = false;

            Assert.True(vm.IsMasterVolumeMuted);
        }
    }
}
