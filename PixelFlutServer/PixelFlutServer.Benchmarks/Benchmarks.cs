using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PixelFlutServer.Mjpeg;
using PixelFlutServer.Mjpeg.PixelFlut;
using System;
using System.IO;
using System.Threading;

namespace PixelFlutServer.Benchmarks
{
    [MemoryDiagnoser]
    public class BenchmarkClass
    {
        [Params(100_000, 10_000_000, 100_000_000)]
        public int N;

        private MemoryStream _testData;
        private PixelBuffer _outputBuffer;

        [GlobalSetup]
        public void Setup()
        {
            var width = 4000;
            var height = 4000;

            _testData = new MemoryStream();
            var rand = new Random();
            using (var sw = new StreamWriter(_testData, leaveOpen: true))
            {
                sw.NewLine = "\n";
                for (int i = 0; i < N; i++)
                {
                    sw.WriteLine($"PX {rand.Next(width)} {rand.Next(height)} {rand.Next() % 0xFFFFFF:X6}");
                }
            }

            _outputBuffer = new PixelBuffer
            {
                Buffer = new byte[width * height * Const.FrameBytesPerPixel],
                Width = width,
                Height = height
            };
        }

        [GlobalCleanup]
        public void TearDown()
        {
            _testData.Dispose();
        }

        private void RunTest(IPixelFlutHandler sut)
        {
            sut.Handle(_testData, null, _outputBuffer, new AutoResetEvent(false), CancellationToken.None).Wait();
        }

        [Benchmark]
        public void Simple() => RunTest(new PixelFlutSimpleHandler(NullLogger<PixelFlutSimpleHandler>.Instance));

        [Benchmark]
        public void Pipe() => RunTest(new PixelFlutPipeHandler(NullLogger<PixelFlutSpanHandler>.Instance));

        [Benchmark(Baseline = true)]
        public void Span() => RunTest(new PixelFlutSpanHandler(NullLogger<PixelFlutSpanHandler>.Instance, Options.Create(new PixelFlutServerConfig())));
    }
}
