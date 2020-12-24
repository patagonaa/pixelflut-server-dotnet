using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace PixelFlutServer.Mjpeg
{
    class PixelFlutSpanHandler : IPixelFlutHandler
    {
        private readonly ILogger<PixelFlutSpanHandler> _logger;

        public PixelFlutSpanHandler(ILogger<PixelFlutSpanHandler> logger)
        {
            _logger = logger;
        }

        public void Handle(Stream netstream, EndPoint endPoint, PixelBuffer pixelBuffer, SemaphoreSlim frameReadySemaphore, CancellationToken cancellationToken)
        {
            var width = pixelBuffer.Width;
            var height = pixelBuffer.Height;
            var bytesPerPixel = pixelBuffer.BytesPerPixel;
            var pixels = pixelBuffer.Buffer;

            var buffer = new Span<char>(new char[1000]);
            int bufferPos;
            using (var stream = new BufferedStream(netstream, 1_000_000))
            {
                var sr = new StreamReader(stream);
                var indices = new List<int>(16);

                while (!cancellationToken.IsCancellationRequested)
                {
                    bufferPos = 0;
                    indices.Clear();
                    indices.Add(0);
                    while (true)
                    {
                        var chr = stream.ReadByte();
                        if (chr == -1)
                            return;
                        buffer[bufferPos++] = (char)chr;

                        if (chr == ' ' || chr == '\n')
                        {
                            var idx = bufferPos;
                            indices.Add(idx);
                        }
                        if (chr == '\n')
                            break;
                    }

                    var firstPart = buffer.Slice(indices[0], indices[1] - indices[0] - 1);

                    if (firstPart[0] == 'P' && firstPart[1] == 'X')
                    {
                        if (indices.Count != 5 ||
                            !int.TryParse(buffer.Slice(indices[1], indices[2] - indices[1] - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var x) ||
                            !int.TryParse(buffer.Slice(indices[2], indices[3] - indices[2] - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var y))
                            throw new InvalidOperationException($"Invalid Line: {new string(buffer.Slice(0, bufferPos))}");
                        uint color;

                        var colorSpan = buffer.Slice(indices[3], indices[4] - indices[3] - 1);
                        //bool hasAlpha = colorSpan.Length == 8;
                        try
                        {
                            color = uint.Parse(colorSpan.Slice(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        }
                        catch (Exception)
                        {
                            throw new InvalidOperationException($"Invalid Line: {new string(buffer.Slice(0, bufferPos))}");
                        }

                        if (y < 0 || y >= height || x < 0 || x >= width)
                        {
                            _logger.LogWarning("Invliad coordinates from {EndPoint} at line {Line}", endPoint, new string(buffer.Slice(0, bufferPos)));
                            continue;
                        }

                        var pixelIndex = (y * width + x) * bytesPerPixel;
                        try
                        {
                            //if (hasAlpha)
                            //{
                            //    color >>= 8;
                            //}
                            pixels[pixelIndex] = (byte)(color & 0xFF);
                            pixels[pixelIndex + 1] = (byte)(color >> 8 & 0xFF);
                            pixels[pixelIndex + 2] = (byte)(color >> 16 & 0xFF);
                            if (frameReadySemaphore.CurrentCount == 0)
                            {
                                frameReadySemaphore.Release();
                            }
                        }
                        catch (Exception)
                        {
                            throw;
                        }

                    }
                    else if (new string(firstPart) == "SIZE")
                    {
                        stream.Write(Encoding.ASCII.GetBytes($"SIZE {width} {height}\n"));
                    }
                }
            }
        }
    }
}
