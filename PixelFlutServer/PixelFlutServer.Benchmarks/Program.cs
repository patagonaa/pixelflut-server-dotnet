using BenchmarkDotNet.Running;

namespace PixelFlutServer.Benchmarks
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var summary = BenchmarkRunner.Run<BenchmarkClass>();
        }
    }
}
