using Narabemi.Testing;
using Xunit;

namespace Narabemi.Tests
{
    public class SnapshotArgsTests
    {
        [Fact]
        public void Parse_NoArgs_ReturnsNotSnapshotMode()
        {
            var args = SnapshotArgs.Parse([]);
            Assert.False(args.IsSnapshotMode);
        }

        [Fact]
        public void Parse_SnapshotFlag_EnablesSnapshotMode()
        {
            var args = SnapshotArgs.Parse(["--snapshot"]);
            Assert.True(args.IsSnapshotMode);
        }

        [Fact]
        public void Parse_AllArgs_Roundtrip()
        {
            var args = SnapshotArgs.Parse(
            [
                "--snapshot",
                "--video-a", @"C:\videos\a.mp4",
                "--video-b", @"C:\videos\b.mp4",
                "--seek", "10.5",
                "-o", "out/test.png",
            ]);

            Assert.True(args.IsSnapshotMode);
            Assert.Equal(@"C:\videos\a.mp4", args.VideoPathA);
            Assert.Equal(@"C:\videos\b.mp4", args.VideoPathB);
            Assert.Equal(10.5, args.SeekSeconds, precision: 10);
            Assert.Equal("out/test.png", args.OutputPath);
        }

        [Fact]
        public void Parse_Defaults_AreReasonable()
        {
            var args = SnapshotArgs.Parse(["--snapshot"]);
            Assert.Equal(5.0, args.SeekSeconds);
            Assert.Equal("snapshot.png", args.OutputPath);
            Assert.Null(args.VideoPathA);
            Assert.Null(args.VideoPathB);
        }

        [Fact]
        public void Parse_NullArgs_ReturnsNotSnapshotMode()
        {
            var args = SnapshotArgs.Parse(null);
            Assert.False(args.IsSnapshotMode);
        }

        [Fact]
        public void Parse_OutputLongFlag()
        {
            var args = SnapshotArgs.Parse(["--snapshot", "--output", "result.png"]);
            Assert.Equal("result.png", args.OutputPath);
        }
    }
}
