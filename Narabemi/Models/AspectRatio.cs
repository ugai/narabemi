using FFmpeg.AutoGen;

namespace Narabemi.Models
{
    public struct AspectRatio : IRational<double>
    {
        public double Numerator { get; }
        public double Denominator { get; }

        public AspectRatio(double numerator, double denominator)
        {
            Numerator = numerator == 0.0 ? 1.0 : numerator;
            Denominator = denominator == 0.0 ? 1.0 : denominator;
        }
        public AspectRatio(AVRational rational) : this(rational.num, rational.den) { }

        public const char Delimiter = ':';
        public override string ToString() => $"{Numerator}{Delimiter}{Denominator}";
    }
}
