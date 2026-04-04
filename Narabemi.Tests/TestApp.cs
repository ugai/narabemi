using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Narabemi.Tests.TestAppBuilder))]

namespace Narabemi.Tests
{
    public class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<TestApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    public class TestApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }
    }
}
