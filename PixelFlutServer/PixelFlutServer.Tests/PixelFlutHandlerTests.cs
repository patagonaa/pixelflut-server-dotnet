using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using PixelFlutServer.Mjpeg;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PixelFlutServer.Tests
{
    public class PixelFlutHandlerTests
    {
        private MemoryStream _testData;

        [SetUp]
        public void Setup()
        {
            _testData = new MemoryStream();
            var rand = new Random();
            using (var sw = new StreamWriter(_testData, leaveOpen: true))
            {
                sw.NewLine = "\n";
                for (int i = 0; i < 10_000_000; i++)
                {
                    sw.WriteLine($"PX {rand.Next(4000)} {rand.Next(4000)} {rand.Next() % 0xFFFFFF:X6}");
                }
            }
        }

        [TearDown]
        public void TearDown()
        {
            _testData.Dispose();
        }

        [Test]
        public void Test_Handlers_EqualResult()
        {
            var width = 4000;
            var height = 4000;
            var bytesPerPixel = 3;

            var expectedBuffer = new PixelBuffer
            {
                Buffer = new byte[width * height * bytesPerPixel],
                Width = width,
                Height = height,
                BytesPerPixel = bytesPerPixel
            };

            var simpleHandler = new PixelFlutSimpleHandler(NullLogger<PixelFlutSimpleHandler>.Instance);

            _testData.Position = 0;
            simpleHandler.Handle(_testData, null, expectedBuffer, new SemaphoreSlim(1, 1), CancellationToken.None);

            var suts = new List<IPixelFlutHandler>
            {
                new PixelFlutSpanHandler(NullLogger<PixelFlutSpanHandler>.Instance)
            };

            foreach (var sut in suts)
            {
                _testData.Position = 0;
                var actualBuffer = new PixelBuffer
                {
                    Buffer = new byte[width * height * bytesPerPixel],
                    Width = width,
                    Height = height,
                    BytesPerPixel = bytesPerPixel
                };

                sut.Handle(_testData, null, actualBuffer, new SemaphoreSlim(1, 1), CancellationToken.None);

                CollectionAssert.AreEqual(expectedBuffer.Buffer, actualBuffer.Buffer);
            }
        }

        [Test]
        public void Test_Handlers_Performance()
        {
            var width = 4000;
            var height = 4000;
            var bytesPerPixel = 3;

            var suts = new List<IPixelFlutHandler>
            {
                new PixelFlutSimpleHandler(NullLogger<PixelFlutSimpleHandler>.Instance),
                new PixelFlutSpanHandler(NullLogger<PixelFlutSpanHandler>.Instance)
            };

            foreach (var sut in suts)
            {
                _testData.Position = 0;
                var actualBuffer = new PixelBuffer
                {
                    Buffer = new byte[width * height * bytesPerPixel],
                    Width = width,
                    Height = height,
                    BytesPerPixel = bytesPerPixel
                };

                var sw = Stopwatch.StartNew();
                sut.Handle(_testData, null, actualBuffer, new SemaphoreSlim(1, 1), CancellationToken.None);
                sw.Stop();
                Console.WriteLine($"{sut.GetType()}: {sw.ElapsedMilliseconds}ms");
            }
        }
    }
}