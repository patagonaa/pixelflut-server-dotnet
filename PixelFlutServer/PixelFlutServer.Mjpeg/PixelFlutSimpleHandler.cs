﻿using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net;
using System.Threading;

namespace PixelFlutServer.Mjpeg
{
    class PixelFlutSimpleHandler : IPixelFlutHandler
    {
        private readonly ILogger<PixelFlutSimpleHandler> _logger;

        public PixelFlutSimpleHandler(ILogger<PixelFlutSimpleHandler> logger)
        {
            _logger = logger;
        }

        public void Handle(Stream stream, EndPoint endPoint, PixelBuffer pixelBuffer, SemaphoreSlim frameReadySemaphore, CancellationToken cancellationToken)
        {
            var width = pixelBuffer.Width;
            var height = pixelBuffer.Height;
            var bytesPerPixel = pixelBuffer.BytesPerPixel;
            var pixels = pixelBuffer.Buffer;

            var sr = new StreamReader(stream);
            var sw = new StreamWriter(stream);
            sw.NewLine = "\n";
            sw.AutoFlush = true;

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = sr.ReadLine();
                if (line == null)
                    return;

                var splitLine = line.Split(' ');
                if (splitLine[0] == "PX")
                {
                    if (splitLine.Length != 4 || !int.TryParse(splitLine[1], out var x) || !int.TryParse(splitLine[2], out var y))
                        throw new InvalidOperationException($"Invalid Line: {line}");
                    uint color;
                    bool hasAlpha = splitLine[3].Length == 8;
                    try
                    {
                        color = Convert.ToUInt32(splitLine[3], 16);
                    }
                    catch (Exception)
                    {
                        throw new InvalidOperationException($"Invalid Line: {line}");
                    }

                    if (y < 0 || y >= height || x < 0 || x >= width)
                    {
                        _logger.LogWarning("Invliad coordinates at line {Line}", line);
                        continue;
                    }

                    var pixelIndex = (y * width + x) * bytesPerPixel;
                    try
                    {
                        if (hasAlpha)
                        {
                            color >>= 8;
                        }
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
                else if (splitLine[0] == "SIZE")
                {
                    sw.WriteLine($"SIZE {width} {height}");
                }
            }
        }
    }
}
