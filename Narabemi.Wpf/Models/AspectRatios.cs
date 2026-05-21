namespace Narabemi.Models
{
    public static class AspectRatios
    {
        public static readonly AspectRatio Ratio_1_1 = new(1.0, 1.0);
        public static readonly AspectRatio Ratio_16_9 = new(16.0, 9.0);
        public static readonly AspectRatio Ratio_4_3 = new(4.0, 3.0);

        public static readonly AspectRatio[] All = new[]
        {
            Ratio_1_1,
            Ratio_16_9,
            Ratio_4_3,
        };
    }
}
