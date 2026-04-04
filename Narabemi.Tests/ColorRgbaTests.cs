using Narabemi.Settings;
using Xunit;

namespace Narabemi.Tests
{
    public class ColorRgbaTests
    {
        [Fact]
        public void FromHex_ParsesRRGGBB()
        {
            var c = ColorRgba.FromHex("#FF8800");
            Assert.Equal(255, c.R);
            Assert.Equal(136, c.G);
            Assert.Equal(0, c.B);
            Assert.Equal(255, c.A);
        }

        [Fact]
        public void FromHex_ParsesAARRGGBB()
        {
            var c = ColorRgba.FromHex("#80FF0000");
            Assert.Equal(255, c.R);
            Assert.Equal(0, c.G);
            Assert.Equal(0, c.B);
            Assert.Equal(128, c.A);
        }

        [Fact]
        public void FromHex_InvalidString_ReturnsWhite()
        {
            Assert.Equal(ColorRgba.White, ColorRgba.FromHex("invalid"));
        }

        [Fact]
        public void ToString_OpaqueColor_ReturnsRRGGBB()
        {
            var c = new ColorRgba(255, 128, 0, 255);
            Assert.Equal("#FF8000", c.ToString());
        }

        [Fact]
        public void ToString_TranslucentColor_ReturnsAARRGGBB()
        {
            var c = new ColorRgba(255, 0, 0, 128);
            Assert.Equal("#80FF0000", c.ToString());
        }
    }
}
