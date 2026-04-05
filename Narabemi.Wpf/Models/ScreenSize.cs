namespace Narabemi.Models
{
    public struct ScreenSize
    {
        public int Width { get; }
        public int Height { get; }

        public ScreenSize(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public override string ToString() => $"{Width}x{Height}";
    }
}
