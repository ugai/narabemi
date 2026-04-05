using System.Collections.Generic;
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
        public void ApplyTo_Then_ApplyFrom_RoundTrips_AllFields()
        {
            var service = new AppStatesService(NullLogger<AppStatesService>.Instance);
            service.LoadFile();

            service.Current!.Loop = true;
            service.Current.AutoSync = false;
            service.Current.MainPlayerIndex = 1;
            service.Current.VideoPathList.Clear();
            service.Current.VideoPathList.AddRange(new[] { "path/a.mp4", "path/b.mp4" });
            service.Current.BlendBorderWidth = 2.5;
            service.Current.BlendBorderColor = new ColorRgba(255, 0, 128, 255);

            var target = new StubTarget();
            service.ApplyTo(target);

            Assert.True(target.Loop);
            Assert.False(target.AutoSync);
            Assert.Equal(1, target.MainPlayerIndex);
            Assert.Equal("path/a.mp4", target.StatePlayers[0].VideoPath);
            Assert.Equal("path/b.mp4", target.StatePlayers[1].VideoPath);
            Assert.Equal(2.5, target.BlendBorderWidth);
            Assert.Equal(new ColorRgba(255, 0, 128, 255), target.BlendBorderColor);

            target.Loop = false;
            target.BlendBorderColor = new ColorRgba(0, 255, 0, 255);

            service.ApplyFrom(target);

            Assert.False(service.Current.Loop);
            Assert.Equal(new ColorRgba(0, 255, 0, 255), service.Current.BlendBorderColor);
        }
    }
}
