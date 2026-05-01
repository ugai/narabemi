namespace Narabemi.Testing
{
    /// <summary>Shared atomic counters for benchmark instrumentation.</summary>
    internal static class BenchCounters
    {
        /// <summary>Number of times GpuBlendSwapChainControl.PresentFrame completed successfully.</summary>
        public static volatile int Presents;
    }
}
