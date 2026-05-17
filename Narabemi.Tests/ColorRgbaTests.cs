using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Narabemi.Settings;
using Xunit;

namespace Narabemi.Tests
{
    public class ColorRgbaTests
    {
        // ---------------------------------------------------------------------------
        // ColorRgba.FromHex (existing behaviour, unchanged)
        // ---------------------------------------------------------------------------

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

        // ---------------------------------------------------------------------------
        // ColorRgba.TryFromHex
        // ---------------------------------------------------------------------------

        [Fact]
        public void TryFromHex_ValidRRGGBB_ReturnsTrueAndParsedColor()
        {
            var ok = ColorRgba.TryFromHex("#FF8800", out var color);
            Assert.True(ok);
            Assert.Equal(255, color.R);
            Assert.Equal(136, color.G);
            Assert.Equal(0, color.B);
            Assert.Equal(255, color.A);
        }

        [Fact]
        public void TryFromHex_ValidAARRGGBB_ReturnsTrueAndParsedColor()
        {
            var ok = ColorRgba.TryFromHex("#80FF0000", out var color);
            Assert.True(ok);
            Assert.Equal(255, color.R);
            Assert.Equal(0, color.G);
            Assert.Equal(0, color.B);
            Assert.Equal(128, color.A);
        }

        [Fact]
        public void TryFromHex_InvalidString_ReturnsFalseAndWhite()
        {
            var ok = ColorRgba.TryFromHex("not-a-color", out var color);
            Assert.False(ok);
            Assert.Equal(ColorRgba.White, color);
        }

        [Fact]
        public void TryFromHex_WrongLength_ReturnsFalseAndWhite()
        {
            var ok = ColorRgba.TryFromHex("#ABC", out var color);
            Assert.False(ok);
            Assert.Equal(ColorRgba.White, color);
        }

        // ---------------------------------------------------------------------------
        // ColorRgba.ToString
        // ---------------------------------------------------------------------------

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

        // ---------------------------------------------------------------------------
        // ColorRgbaJsonConverter — warning behaviour via AppStatesService
        //
        // We verify the converter's warning path through AppStatesService.LoadFile()
        // so the test project does not need to directly construct
        // ColorRgbaJsonConverter with its new callback constructor.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Minimal ILogger&lt;AppStatesService&gt; that captures warning messages.
        /// </summary>
        private sealed class CapturingLogger : ILogger<AppStatesService>
        {
            public readonly List<string> Warnings = new List<string>();

            IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
            bool ILogger.IsEnabled(LogLevel logLevel) => true;

            void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    Warnings.Add(formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();
                public void Dispose() { }
            }
        }

        /// <summary>
        /// Writes <paramref name="json"/> as the appstates.json that
        /// AppStatesService will read, runs LoadFile, then restores any
        /// previous file.
        /// </summary>
        private static AppStatesService LoadFromJson(string json, ILogger<AppStatesService> logger)
        {
            // AppStatesService looks for "appstates.json" relative to the process
            // working directory (or AppContext.BaseDirectory on old builds).
            // We write a temp file, copy it to the expected path, load, then clean up.
            var stateFile = Path.Combine(AppContext.BaseDirectory, "appstates.json");
            var backup = stateFile + ".bak";

            if (File.Exists(stateFile))
                File.Copy(stateFile, backup, overwrite: true);

            try
            {
                File.WriteAllText(stateFile, json);
                var service = new AppStatesService(logger);
                service.LoadFile();
                return service;
            }
            finally
            {
                File.Delete(stateFile);
                if (File.Exists(backup))
                {
                    File.Move(backup, stateFile, overwrite: true);
                }
            }
        }

        [Fact]
        public void Converter_InvalidColorInJson_LogsWarningAndDefaultsToWhite()
        {
            var json = "{ \"BlendBorderColor\": \"not-a-color\" }";

            var logger = new CapturingLogger();
            var service = LoadFromJson(json, logger);

            // The invalid color should have been replaced with White
            Assert.Equal(ColorRgba.White, service.Current!.BlendBorderColor);
            // And a warning should have been logged from the converter
            Assert.Contains(logger.Warnings, w => w.Contains("ColorRgbaJsonConverter"));
        }

        [Fact]
        public void Converter_ValidColorInJson_NoConverterWarning()
        {
            var json = "{ \"BlendBorderColor\": \"#FF8800\" }";

            var logger = new CapturingLogger();
            var service = LoadFromJson(json, logger);

            Assert.Equal(ColorRgba.FromHex("#FF8800"), service.Current!.BlendBorderColor);
            // No converter warning for a valid color
            Assert.DoesNotContain(logger.Warnings, w => w.Contains("ColorRgbaJsonConverter"));
        }
    }
}
