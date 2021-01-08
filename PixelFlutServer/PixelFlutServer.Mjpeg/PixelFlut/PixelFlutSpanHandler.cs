﻿using Microsoft.Extensions.Logging;
using Prometheus;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

        private ulong _handledRecvBytes = 0;
        private ulong _handledRecvPixels = 0;
        private ulong _handledSentPixels = 0;

        public async Task Handle(Stream stream, EndPoint endPoint, PixelBuffer pixelBuffer, SemaphoreSlim frameReadySemaphore, CancellationToken cancellationToken)
        {
            await Task.Factory.StartNew(() => HandleInternal(stream, endPoint, pixelBuffer, frameReadySemaphore, cancellationToken), TaskCreationOptions.LongRunning);
        }

        private void HandleInternal(Stream netstream, EndPoint endPoint, PixelBuffer pixelBuffer, SemaphoreSlim frameReadySemaphore, CancellationToken cancellationToken)
        {
            var width = pixelBuffer.Width;
            var height = pixelBuffer.Height;
            var bytesPerPixel = pixelBuffer.BytesPerPixel;
            var pixelsBgr = pixelBuffer.Buffer;

            var buffer = new Span<char>(new char[1000]);
            int bufferPos;
            var stream = new BufferedStream(netstream, 1_000_000);
            var indices = new List<int>(16);

            int offsetX = 0;
            int offsetY = 0;

            System.Timers.Timer timer = new System.Timers.Timer();
            try
            {
                timer.Elapsed += FlushStats;
                timer.Interval = 5000;
                timer.AutoReset = true;
                timer.Start();

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
                    Interlocked.Add(ref _handledRecvBytes, (ulong)bufferPos);

                    if (indices.Count < 2)
                    {
                        LogInvalidLine(endPoint, buffer, bufferPos);
                        continue;
                    }

                    var firstPart = buffer.Slice(indices[0], indices[1] - indices[0] - 1);

                    if (firstPart.Length < 2)
                    {
                        LogInvalidLine(endPoint, buffer, bufferPos);
                        continue;
                    }

                    if (firstPart[0] == 'P' && firstPart[1] == 'X')
                    {
                        if (indices.Count < 4 ||
                            !int.TryParse(buffer.Slice(indices[1], indices[2] - indices[1] - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var x) ||
                            !int.TryParse(buffer.Slice(indices[2], indices[3] - indices[2] - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var y))
                        {
                            LogInvalidLine(endPoint, buffer, bufferPos);
                            continue;
                        }

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

                            netstream.Write(Encoding.ASCII.GetBytes($"PX {x} {y} {pixelsBgr[pxIndex] | pixelsBgr[pxIndex + 1] << 8 | pixelsBgr[pxIndex + 2] << 16:X6}\n"));
                            Interlocked.Increment(ref _handledSentPixels);
                            continue;
                        }
                        else if (indices.Count == 5) // PX 1337 1234 FF00FF[AA]\n
                        {
                            var colorSpan = buffer.Slice(indices[3], indices[4] - indices[3] - 1);
                            var hasAlpha = colorSpan.Length == 8;
                            if (!uint.TryParse(colorSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint color))
                            {
                                LogInvalidLine(endPoint, buffer, bufferPos);
                                continue;
                            }

                            byte alpha = 0xFF;
                            if (hasAlpha)
                            {
                                alpha = (byte)(color & 0xFF);
                                color >>= 8;
                            }

                            var xOut = offsetX + x;
                            var yOut = offsetY + y;

                            if (yOut < 0 || yOut >= height || xOut < 0 || xOut >= width)
                            {
#if DEBUG
                                _logger.LogDebug("Invalid coordinates from {EndPoint} at line {Line}", endPoint, new string(buffer.Slice(0, bufferPos)));
#endif
                                continue;
                            }

                            byte b = (byte)(color & 0xFF);
                            byte g = (byte)(color >> 8 & 0xFF);
                            byte r = (byte)(color >> 16 & 0xFF);

                            var pixelIndex = (yOut * width + xOut) * bytesPerPixel;

                            if (alpha == 0x00)
                            {
                            }
                            else if (alpha == 0xFF)
                            {
                                pixelsBgr[pixelIndex] = b;
                                pixelsBgr[pixelIndex + 1] = g;
                                pixelsBgr[pixelIndex + 2] = r;
                            }
                            else
                            {
                                var oldB = pixelsBgr[pixelIndex];
                                var oldG = pixelsBgr[pixelIndex + 1];
                                var oldR = pixelsBgr[pixelIndex + 2];

                                var alphaFactor = alpha / 255.0;

                                var newB = (byte)(oldB * (1 - alphaFactor) + b * alphaFactor);
                                var newG = (byte)(oldG * (1 - alphaFactor) + g * alphaFactor);
                                var newR = (byte)(oldR * (1 - alphaFactor) + r * alphaFactor);

                                pixelsBgr[pixelIndex] = newB;
                                pixelsBgr[pixelIndex + 1] = newG;
                                pixelsBgr[pixelIndex + 2] = newR;
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
                            Interlocked.Increment(ref _handledRecvPixels);
                        }
                        else
                        {
                            LogInvalidLine(endPoint, buffer, bufferPos);
                            continue;
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
                            {
                                LogInvalidLine(endPoint, buffer, bufferPos);
                                continue;
                            }

                            offsetX = x;
                            offsetY = y;
                        }
                        else
                        {
                            LogInvalidLine(endPoint, buffer, bufferPos);
                            continue;
                        }
                    }
                }
            }
            finally
            {
                if (timer != null)
                    timer.Dispose();
            }
        }

        private void LogInvalidLine(EndPoint endPoint, Span<char> buffer, int bufferPos)
        {
            var line = new string(buffer.Slice(0, bufferPos - 1)); // remove newline at end
            _logger.LogInformation("Invalid Line from {EndPoint}: {Line}", endPoint, line);
        }

        private void FlushStats(object sender, System.Timers.ElapsedEventArgs e)
        {
            var handledBytes = Interlocked.Exchange(ref _handledRecvBytes, 0);
            _byteCounter.Inc(handledBytes);
            var recvPixels = Interlocked.Exchange(ref _handledRecvPixels, 0);
            _pixelRecvCounter.Inc(recvPixels);
            var sentPixels = Interlocked.Exchange(ref _handledSentPixels, 0);
            _pixelSentCounter.Inc(sentPixels);
        }
    }
}
