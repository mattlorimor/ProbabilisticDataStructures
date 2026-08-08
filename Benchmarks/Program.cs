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
        public static void Main(string[] args) =>
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
