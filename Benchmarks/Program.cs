using BenchmarkDotNet.Running;

namespace Benchmarks
{
    /// <summary>
    /// Entry point. Run all suites with:
    ///   dotnet run -c Release --project Benchmarks -- --filter '*'
    /// or a single one with, for example:
    ///   dotnet run -c Release --project Benchmarks -- --filter '*HashKernel*'
    /// </summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            // Accuracy studies answer "which is more accurate" rather than "how fast",
            // so they do not run under BenchmarkDotNet and are dispatched here.
            if (args.Length >= 2 && args[0] == "study" && args[1] == "hll-bias")
            {
                HyperLogLogBiasStudy.Run(args[2..]);
                return;
            }

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }
    }
}
