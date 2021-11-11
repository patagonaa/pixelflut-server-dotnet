using Microsoft.Extensions.Logging;
using Prometheus;
using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg.PixelFlut
{
    public class PixelFlutPipeHandler : IPixelFlutHandler
    {
        private readonly ILogger<PixelFlutSpanHandler> _logger;
        private static readonly Counter _pixelRecvCounter = Metrics.CreateCounter("pixelflut_pixels_received", "Total number of received pixels");
        private static readonly Counter _pixelSentCounter = Metrics.CreateCounter("pixelflut_pixels_sent", "Total number of sent pixels");
        private static readonly Counter _byteCounter = Metrics.CreateCounter("pixelflut_bytes_received", "Total number of received bytes");

        public PixelFlutPipeHandler(ILogger<PixelFlutSpanHandler> logger)
        {
            _logger = logger;
        }

        private ulong _handledRecvBytes = 0;
        private ulong _handledRecvPixels = 0;
        private ulong _handledSentPixels = 0;

        public async Task Handle(Stream stream, EndPoint endPoint, PixelBuffer pixelBuffer, SemaphoreSlim frameReadySemaphore, CancellationToken cancellationToken)
        {
            var handleTask = Handle(stream, PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 10*1024*1024)), endPoint, pixelBuffer, frameReadySemaphore, cancellationToken);

            System.Timers.Timer timer = new System.Timers.Timer();
            try
            {
                timer.Elapsed += FlushStats;
                timer.Interval = 5000;
                timer.AutoReset = true;
                timer.Start();

                await Task.WhenAll(handleTask);
            }
            finally
            {
                timer.Dispose();
            }
        }

        private async Task Handle(Stream stream, PipeReader reader, EndPoint endPoint, PixelBuffer pixelBuffer, SemaphoreSlim frameReadySemaphore, CancellationToken cancellationToken)
        {
            int offsetX = 0;
            int offsetY = 0;

            var encoding = Encoding.ASCII;

            var charBuffer = new char[1024];

            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = result.Buffer;
                while (TryReadLine(ref buffer, encoding, new Span<char>(charBuffer), out int writtenChars))
                {
                    ProcessLine(new Span<char>(charBuffer).Slice(0, writtenChars), encoding, ref offsetX, ref offsetY, stream, endPoint, pixelBuffer, frameReadySemaphore);
                }

                // Tell the PipeReader how much of the buffer has been consumed.
                reader.AdvanceTo(buffer.Start, buffer.End);

                // Stop reading if there's no more data coming.
                if (result.IsCompleted)
                {
                    break;
                }
            }

            // Mark the PipeReader as complete.
            await reader.CompleteAsync();
        }

        private void ProcessLine(ReadOnlySpan<char> chars, Encoding encoding, ref int offsetX, ref int offsetY, Stream stream, EndPoint endPoint, PixelBuffer pixelBuffer, SemaphoreSlim frameReadySemaphore)
        {
            var width = pixelBuffer.Width;
            var height = pixelBuffer.Height;
            var bytesPerPixel = pixelBuffer.BytesPerPixel;
            var pixels = pixelBuffer.Buffer;

            var spaceIndex = chars.IndexOf(' ');

            var command = chars.Slice(0, spaceIndex == -1 ? chars.Length : spaceIndex);

            if (spaceIndex == -1)
            {
                if (MemoryExtensions.Equals(command, "SIZE".AsSpan(), StringComparison.Ordinal))
                {
                    stream.Write(encoding.GetBytes($"SIZE {width} {height}\n"));
                    return;
                }
                else
                {
                    LogError(chars);
                    return;
                }
            }
            else
            {
                if (command.Length == 2 && command[0] == 'P' && command[1] == 'X')
                {
                    if(!TryGetInt(chars, ref spaceIndex, out var x) ||
                        !TryGetInt(chars, ref spaceIndex, out var y))
                    {
                        LogError(chars);
                        return;
                    }

                    if(spaceIndex == -1) // PX X Y
                    {
                        var pxIndex = (y * width + x) * bytesPerPixel;

                        if (y < 0 || y >= height || x < 0 || x >= width)
                        {
#if DEBUG
                                _logger.LogWarning("Invalid coordinates from {EndPoint} at line {Line}", endPoint, new string(chars));
#endif
                            return;
                        }

                        stream.Write(encoding.GetBytes($"PX {x} {y} {pixels[pxIndex] | pixels[pxIndex + 1] << 8 | pixels[pxIndex + 2] << 16:X6}\n"));
                        Interlocked.Increment(ref _handledSentPixels);
                        return;
                    }

                    if(!TryGetHexUInt(chars, ref spaceIndex, out var color))
                    {
                        LogError(chars);
                        return;
                    }

                    var xOut = offsetX + x;
                    var yOut = offsetY + y;

                    if (yOut < 0 || yOut >= height || xOut < 0 || xOut >= width)
                    {
#if DEBUG
                        _logger.LogWarning("Invalid coordinates from {EndPoint} at line {Line}", endPoint, new string(chars));
#endif
                        return;
                    }

                    byte alpha = 0xFF;
                    if ((color & 0xFF000000U) > 0)
                    {
                        alpha = (byte)(color & 0xFF);
                        color >>= 8;
                    }

                    byte r = (byte)(color & 0xFF);
                    byte g = (byte)(color >> 8 & 0xFF);
                    byte b = (byte)(color >> 16 & 0xFF);

                    var pixelIndex = (yOut * width + xOut) * bytesPerPixel;

                    if (alpha == 0xFF)
                    {
                        pixels[pixelIndex] = r;
                        pixels[pixelIndex + 1] = g;
                        pixels[pixelIndex + 2] = b;
                    }
                    else if (alpha == 0x00)
                    {
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
                    Interlocked.Increment(ref _handledRecvPixels);
                }
                else if (MemoryExtensions.Equals(command, "OFFSET".AsSpan(), StringComparison.Ordinal))
                {
                    if (!TryGetInt(chars, ref spaceIndex, out var x) ||
                        !TryGetInt(chars, ref spaceIndex, out var y))
                    {
                        LogError(chars);
                        return;
                    }

                    offsetX = x;
                    offsetY = y;
                }
                else
                {
                    LogError(chars);
                    return;
                }
            }
        }

        private void LogError(in ReadOnlySpan<char> line)
        {
            _logger.LogInformation("Invalid Line: {Line}", new string(line));
        }

        private bool TryGetInt(in ReadOnlySpan<char> chars, ref int spaceIndex, out int x)
        {
            if (spaceIndex < 0 || spaceIndex + 1 >= chars.Length)
            {
                spaceIndex = -1;
                x = -1;
                return false;
            }

            var findSpan = chars.Slice(spaceIndex + 1);
            var indexInFindSpan = findSpan.IndexOf(' ');
            if (indexInFindSpan == -1)
            {
                spaceIndex = -1;
                return int.TryParse(findSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out x);
            }
            spaceIndex += indexInFindSpan + 1;
            return int.TryParse(findSpan.Slice(0, indexInFindSpan), NumberStyles.Integer, CultureInfo.InvariantCulture, out x);
        }

        private bool TryGetHexUInt(in ReadOnlySpan<char> chars, ref int spaceIndex, out uint x)
        {
            if (spaceIndex < 0 || spaceIndex + 1 >= chars.Length)
            {
                spaceIndex = -1;
                x = 0xDEADBEEF;
                return false;
            }

            var findSpan = chars.Slice(spaceIndex + 1);
            var indexInFindSpan = findSpan.IndexOf(' ');
            if (indexInFindSpan == -1)
            {
                spaceIndex = -1;
                return uint.TryParse(findSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out x);
            }
            spaceIndex += indexInFindSpan + 1;
            return uint.TryParse(findSpan.Slice(0, indexInFindSpan), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out x);
        }

        private bool TryReadLine(ref ReadOnlySequence<byte> buffer, Encoding encoding, Span<char> charBuffer, out int writtenChars)
        {
            // Look for a EOL in the buffer.
            SequencePosition? position = buffer.PositionOf((byte)'\n');
            if (position == null)
            {
                writtenChars = 0;
                return false;
            }

            // Skip the line + the \n.
            var lineSequence = buffer.Slice(0, position.Value);

            writtenChars = encoding.GetChars(lineSequence, charBuffer);
            buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
            return true;
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
