using Microsoft.Extensions.Logging;
using Prometheus;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace PixelFlutServer.Mjpeg.PixelFlut
{
    public class PixelFlutSpanHandler : IPixelFlutHandler
    {
        private readonly ILogger<PixelFlutSpanHandler> _logger;
        private static readonly Counter _pixelRecvCounter = Metrics.CreateCounter("pixelflut_pixels_received", "Total number of received pixels");
        private static readonly Counter _pixelSentCounter = Metrics.CreateCounter("pixelflut_pixels_sent", "Total number of sent pixels");
        private static readonly Counter _byteCounter = Metrics.CreateCounter("pixelflut_bytes_received", "Total number of received bytes");

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
            var stream = new BufferedStream(netstream, 1_000_000);
            var indices = new List<int>(16);

            int offsetX = 0;
            int offsetY = 0;

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
                _byteCounter.Inc(bufferPos);

                var firstPart = buffer.Slice(indices[0], indices[1] - indices[0] - 1);

                if (firstPart[0] == 'P' && firstPart[1] == 'X')
                {
                    if (indices.Count < 4 ||
                        !int.TryParse(buffer.Slice(indices[1], indices[2] - indices[1] - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var x) ||
                        !int.TryParse(buffer.Slice(indices[2], indices[3] - indices[2] - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var y))
                        throw new InvalidOperationException($"Invalid Line: {new string(buffer.Slice(0, bufferPos))}");

                    if (indices.Count == 4) // PX 1337 1234\n
                    {
                        var pxIndex = (y * width + x) * bytesPerPixel;

                        if (y < 0 || y >= height || x < 0 || x >= width)
                        {
#if DEBUG
                            _logger.LogWarning("Invalid coordinates from {EndPoint} at line {Line}", endPoint, new string(buffer.Slice(0, bufferPos)));
#endif
                            continue;
                        }

                        netstream.Write(Encoding.ASCII.GetBytes($"PX {x} {y} {pixels[pxIndex] | pixels[pxIndex + 1] << 8 | pixels[pxIndex + 2] << 16:X6}\n"));
                        _pixelSentCounter.Inc();
                        continue;
                    }
                    else if (indices.Count == 5) // PX 1337 1234 FF00FF\n
                    {
                        var colorSpan = buffer.Slice(indices[3], indices[4] - indices[3] - 1);
                        //bool hasAlpha = colorSpan.Length == 8;
                        if (!uint.TryParse(colorSpan.Slice(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint color))
                        {
                            throw new InvalidOperationException($"Invalid Line: {new string(buffer.Slice(0, bufferPos))}");
                        }

                        byte alpha;
                        if(colorSpan.Length == 8)
                        {
                            if(!byte.TryParse(colorSpan.Slice(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out alpha))
                            {
                                throw new InvalidOperationException($"Invalid Line: {new string(buffer.Slice(0, bufferPos))}");
                            }
                        }
                        else
                        {
                            alpha = 0xFF;
                        }

                        var xOut = offsetX + x;
                        var yOut = offsetY + y;

                        if (yOut < 0 || yOut >= height || xOut < 0 || xOut >= width)
                        {
#if DEBUG
                            _logger.LogWarning("Invalid coordinates from {EndPoint} at line {Line}", endPoint, new string(buffer.Slice(0, bufferPos)));
#endif
                            continue;
                        }

                        byte r = (byte)(color & 0xFF);
                        byte g = (byte)(color >> 8 & 0xFF);
                        byte b = (byte)(color >> 16 & 0xFF);

                        var pixelIndex = (yOut * width + xOut) * bytesPerPixel;

                        if (alpha == 0x00)
                        {
                        }
                        else if (alpha == 0xFF)
                        {
                            pixels[pixelIndex] = r;
                            pixels[pixelIndex + 1] = g;
                            pixels[pixelIndex + 2] = b;
                        }
                        else
                        {
                            var oldR = pixels[pixelIndex];
                            var oldG = pixels[pixelIndex + 1];
                            var oldB = pixels[pixelIndex + 2];
                            var alphaFactor = alpha / 255.0;

                            var newR = (byte)(oldR * (1 - alphaFactor) + r * alphaFactor);
                            var newG = (byte)(oldG * (1 - alphaFactor) + g * alphaFactor);
                            var newB = (byte)(oldB * (1 - alphaFactor) + b * alphaFactor);

                            pixels[pixelIndex] = newR;
                            pixels[pixelIndex + 1] = newG;
                            pixels[pixelIndex + 2] = newB;
                        }


                        if (frameReadySemaphore.CurrentCount == 0)
                        {
                            try
                            {
                                frameReadySemaphore.Release();
                            }
                            catch (SemaphoreFullException)
                            {
                            }
                        }
                        _pixelRecvCounter.Inc();
                    }
                    else
                    {
                        throw new InvalidOperationException($"Invalid Line: {new string(buffer.Slice(0, bufferPos))}");
                    }
                }
                else
                {
                    var strFirstPart = new string(firstPart);
                    if (strFirstPart == "SIZE")
                    {
                        netstream.Write(Encoding.ASCII.GetBytes($"SIZE {width} {height}\n"));
                    }
                    else if (strFirstPart == "OFFSET")
                    {
                        if (indices.Count != 4 ||
                            !int.TryParse(buffer.Slice(indices[1], indices[2] - indices[1] - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var x) ||
                            !int.TryParse(buffer.Slice(indices[2], indices[3] - indices[2] - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var y))
                            throw new InvalidOperationException($"Invalid Line: {new string(buffer.Slice(0, bufferPos))}");

                        offsetX = x;
                        offsetY = y;
                    }
                }
            }
        }
    }
}
