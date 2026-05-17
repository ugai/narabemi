using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Narabemi.Settings;
using Xunit;

namespace Narabemi.Tests
{
    public class AppStatesServiceTests
    {
        private sealed class StubPlayer : IAppStatePlayerTarget
        {
            public string VideoPath { get; set; } = string.Empty;
            public double Speed { get; set; } = 1.0;
            public double TimeOffset { get; set; }
        }

        private sealed class StubTarget : IAppStateTarget
        {
            public bool Loop { get; set; }
            public bool AutoSync { get; set; }
            public int MainPlayerIndex { get; set; }
            public IList<IAppStatePlayerTarget> StatePlayers { get; } = new List<IAppStatePlayerTarget>
            {
                new StubPlayer(),
                new StubPlayer(),
            };
            public double BlendBorderWidth { get; set; }
            public ColorRgba BlendBorderColor { get; set; }
            public double BlendRatio { get; set; }
            public int BlendMode { get; set; }
        }

        [Fact]
        public void FilePath_IsRelativeToInstallDirectory()
        {
            // AppStatesService.FilePath must be anchored to AppContext.BaseDirectory, not CWD.
            // This ensures the file is always found/written next to the EXE regardless of the
            // working directory from which the process was launched.
            var expected = Path.Combine(AppContext.BaseDirectory, "appstates.json");
            Assert.Equal(expected, AppStatesService.FilePath, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void ApplyTo_Then_ApplyFrom_RoundTrips_AllFields()
        {
            var service = new AppStatesService(NullLogger<AppStatesService>.Instance);
            service.LoadFile();

            service.Current!.Loop = true;
            service.Current.AutoSync = false;
            service.Current.MainPlayerIndex = 1;
            service.Current.VideoPathList.Clear();
            service.Current.VideoPathList.AddRange(new[] { "path/a.mp4", "path/b.mp4" });
            service.Current.PlayerSpeedList.Clear();
            service.Current.PlayerSpeedList.AddRange(new[] { 2.0, 0.5 });
            service.Current.PlayerTimeOffsetList.Clear();
            service.Current.PlayerTimeOffsetList.AddRange(new[] { 0.0, -1.5 });
            service.Current.BlendBorderWidth = 2.5;
            service.Current.BlendBorderColor = new ColorRgba(255, 0, 128, 255);
            service.Current.WindowWidth  = 1920;
            service.Current.WindowHeight = 1080;
            service.Current.WindowX      = 100;
            service.Current.WindowY      = 200;
            service.Current.IsWindowMaximized = true;

            var target = new StubTarget();
            service.ApplyTo(target);

            Assert.True(target.Loop);
            Assert.False(target.AutoSync);
            Assert.Equal(1, target.MainPlayerIndex);
            Assert.Equal("path/a.mp4", target.StatePlayers[0].VideoPath);
            Assert.Equal("path/b.mp4", target.StatePlayers[1].VideoPath);
            Assert.Equal(2.0, target.StatePlayers[0].Speed);
            Assert.Equal(0.5, target.StatePlayers[1].Speed);
            Assert.Equal(0.0, target.StatePlayers[0].TimeOffset);
            Assert.Equal(-1.5, target.StatePlayers[1].TimeOffset);
            Assert.Equal(2.5, target.BlendBorderWidth);
            Assert.Equal(new ColorRgba(255, 0, 128, 255), target.BlendBorderColor);

            target.Loop = false;
            target.BlendBorderColor = new ColorRgba(0, 255, 0, 255);
            target.StatePlayers[0].Speed = 1.5;
            target.StatePlayers[1].TimeOffset = 3.0;

            service.ApplyFrom(target);

            Assert.False(service.Current.Loop);
            Assert.Equal(new ColorRgba(0, 255, 0, 255), service.Current.BlendBorderColor);
            Assert.Equal(1.5, service.Current.PlayerSpeedList[0]);
            Assert.Equal(3.0, service.Current.PlayerTimeOffsetList[1]);

            // Window state is not managed via IAppStateTarget; verify ApplyFrom does not clobber it.
            Assert.Equal(1920, service.Current.WindowWidth);
            Assert.Equal(1080, service.Current.WindowHeight);
            Assert.Equal(100, service.Current.WindowX);
            Assert.Equal(200, service.Current.WindowY);
            Assert.True(service.Current.IsWindowMaximized);
        }
    }
}
