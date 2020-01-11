using BenchmarkDotNet.Running;

namespace Samples.Benchmarks
{
    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run<DictionaryVsArrayIndexer>();
        }
    }
}
