using System.Globalization;

namespace Narabemi.Testing
{
    /// <summary>
    /// Parses --snapshot CLI arguments for visual test capture mode.
    /// Usage: Narabemi --snapshot --video-a path [--video-b path] [--seek 5.0] [-o snapshot.png]
    /// </summary>
    public sealed class SnapshotArgs
    {
        public bool IsSnapshotMode { get; init; }
        public string? VideoPathA { get; init; }
        public string? VideoPathB { get; init; }
        public double SeekSeconds { get; init; } = 5.0;
        public string OutputPath { get; init; } = "snapshot.png";

        public static SnapshotArgs Parse(string[]? args)
        {
            if (args is null || args.Length == 0)
                return new SnapshotArgs();

            bool snapshot = false;
            string? videoA = null, videoB = null;
            double seek = 5.0;
            string output = "snapshot.png";

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--snapshot":
                        snapshot = true;
                        break;
                    case "--video-a" when i + 1 < args.Length:
                        videoA = args[++i];
                        break;
                    case "--video-b" when i + 1 < args.Length:
                        videoB = args[++i];
                        break;
                    case "--seek" when i + 1 < args.Length:
                        double.TryParse(args[++i], CultureInfo.InvariantCulture, out seek);
                        break;
                    case "-o" or "--output" when i + 1 < args.Length:
                        output = args[++i];
                        break;
                }
            }

            return new SnapshotArgs
            {
                IsSnapshotMode = snapshot,
                VideoPathA = videoA,
                VideoPathB = videoB,
                SeekSeconds = seek,
                OutputPath = output,
            };
        }
    }
}
