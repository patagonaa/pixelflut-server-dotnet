using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Prometheus;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg.PixelFlut
{
    public class PixelFlutSpanHandler : IPixelFlutHandler
    {
        private readonly ILogger<PixelFlutSpanHandler> _logger;
        private readonly PixelFlutServerConfig _serverConfig;
        private static readonly Counter _pixelRecvCounter = Metrics.CreateCounter("pixelflut_pixels_received", "Total number of received pixels");
        private static readonly Counter _pixelSentCounter = Metrics.CreateCounter("pixelflut_pixels_sent", "Total number of sent pixels");
        private static readonly Counter _byteCounter = Metrics.CreateCounter("pixelflut_bytes_received", "Total number of received bytes");

        public PixelFlutSpanHandler(ILogger<PixelFlutSpanHandler> logger, IOptions<PixelFlutServerConfig> serverConfig)
        {
            _logger = logger;
            _serverConfig = serverConfig.Value;
        }

        private ulong _handledRecvBytes = 0;
        private ulong _handledRecvPixels = 0;
        private ulong _handledSentPixels = 0;

        public async Task Handle(Stream stream, EndPoint endPoint, PixelBuffer pixelBuffer, SemaphoreSlim frameReadySemaphore, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            var handleThread = new Thread(() =>
                {
                    try
                    {
                        HandleInternal(stream, endPoint, pixelBuffer, frameReadySemaphore, cancellationToken);
                        tcs.SetResult(true);
                    }
                    catch(Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
            handleThread.Priority = ThreadPriority.BelowNormal;
            handleThread.Start();
            await tcs.Task;
        }

        private void HandleInternal(Stream netstream, EndPoint endPoint, PixelBuffer pixelBuffer, SemaphoreSlim frameReadySemaphore, CancellationToken cancellationToken)
        {
            var width = pixelBuffer.Width;
            var height = pixelBuffer.Height;
            var bytesPerPixel = Const.FrameBytesPerPixel;
            var pixelsBgr = pixelBuffer.Buffer;

            Span<char> buffer = stackalloc char[1000];
            int bufferPos = 0;
            var stream = new StreamBufferWrapper(netstream, 1_048_576);
            var indicesCount = 0;
            var indices = new int[16];

            int offsetX = 0;
            int offsetY = 0;

            System.Timers.Timer timer = new System.Timers.Timer();
            try
            {
                timer.Elapsed += FlushStats;
                timer.Interval = 500;
                timer.AutoReset = true;
                timer.Start();

                while (!cancellationToken.IsCancellationRequested)
                {
                    bufferPos = 0;
                    indicesCount = 0;
                    indices[indicesCount++] = 0;
                    while (true)
                    {
                        var chr = stream.ReadByte();
                        if (chr == -1)
                            return;
                        buffer[bufferPos++] = (char)chr;

                        if (chr == ' ')
                        {
                            var idx = bufferPos;
                            indices[indicesCount++] = idx;
                        }
                        else if (chr == '\n' || chr == '\r')
                        {
                            var idx = bufferPos;
                            indices[indicesCount++] = idx;
                            break;
                        }
                    }
                    _handledRecvBytes += (ulong)bufferPos;

                    if (indicesCount < 2)
                    {
                        throw new InvalidLineException(endPoint, FormatLine(buffer[..bufferPos]));
                    }

                    var firstPart = buffer.Slice(indices[0], indices[1] - indices[0] - 1);

                    if (firstPart.Length == 0)
                    {
                        continue;
                    }
                    if (firstPart.Length < 2)
                    {
                        throw new InvalidLineException(endPoint, FormatLine(buffer[..bufferPos]));
                    }

                    if (firstPart[0] == 'P' && firstPart[1] == 'X')
                    {
                        if (indicesCount < 4 ||
                            !TryParseInt(buffer.Slice(indices[1], indices[2] - indices[1] - 1), out var x) ||
                            !TryParseInt(buffer.Slice(indices[2], indices[3] - indices[2] - 1), out var y))
                        {
                            throw new InvalidLineException(endPoint, FormatLine(buffer[..bufferPos]));
                        }

                        if (indicesCount == 4) // PX 1337 1234\n
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
                            _handledSentPixels++;
                            continue;
                        }
                        else if (indicesCount == 5) // PX 1337 1234 FF00FF[AA]\n
                        {
                            var colorSpan = buffer.Slice(indices[3], indices[4] - indices[3] - 1);
                            var hasAlpha = colorSpan.Length == 8;
                            if (!uint.TryParse(colorSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint color))
                            {
                                throw new InvalidLineException(endPoint, FormatLine(buffer[..bufferPos]));
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
                            _handledRecvPixels++;
                        }
                        else
                        {
                            throw new InvalidLineException(endPoint, FormatLine(buffer[..bufferPos]));
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
                            if (indicesCount != 4 ||
                                !TryParseInt(buffer.Slice(indices[1], indices[2] - indices[1] - 1), out var x) ||
                                !TryParseInt(buffer.Slice(indices[2], indices[3] - indices[2] - 1), out var y))
                            {
                                throw new InvalidLineException(endPoint, FormatLine(buffer[..bufferPos]));
                            }

                            offsetX = x;
                            offsetY = y;
                        }
                        else if (strFirstPart == "GET")
                        {
                            _logger.LogInformation("HTTP Request to Pixelflut Server from {EndPoint}: {Line}", endPoint, FormatLine(buffer.Slice(0, bufferPos)));

                            if (!string.IsNullOrEmpty(_serverConfig.HttpServerUri))
                            {
                                netstream.Write(Encoding.ASCII.GetBytes($"HTTP/1.0 302 Moved Temporarily\r\nLocation: {_serverConfig.HttpServerUri}\r\n\r\n"));
                            }
                            else
                            {
                                netstream.Write(Encoding.ASCII.GetBytes("HTTP/1.0 400 Bad Request\r\n\r\nThis is not a HTTP Server, go away."));
                            }
                            bufferPos = 0; // clear remaining buffer
                            return; // close connection
                        }
                        else if (strFirstPart == "HELP")
                        {
                            netstream.Write(Encoding.ASCII.GetBytes("HELP https://github.com/patagonaa/pixelflut-server-dotnet\n"));
                        }
                        else
                        {
                            throw new InvalidLineException(endPoint, FormatLine(buffer[..bufferPos]));
                        }
                    }
                }
            }
            catch (InvalidLineException ex)
            {
                netstream.Write(Encoding.UTF8.GetBytes($"Invalid Line: {ex.Message}. Go bother patagona if you think that is a mistake."));
                _logger.LogInformation("Invalid Line from {EndPoint}: {Line}", ex.EndPoint, ex.Message);
            }
            finally
            {
                if (timer != null)
                    timer.Dispose();
                if (bufferPos > 0)
                    _logger.LogInformation("Remaining Buffer: {RemainingBuffer}", FormatLine(buffer.Slice(0, bufferPos)));
            }
        }

        private static bool TryParseInt(ReadOnlySpan<char> span, out int num)
        {
            return int.TryParse(span, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out num);
        }

        private bool IsAllowedChar(char x)
        {
            return (x >= 'A' && x <= 'Z') || (x >= 'a' && x <= 'z') || (x >= '0' && x <= '9') || x == ' ' || x == '\r' || x == '\n';
        }

        private string FormatLine(Span<char> lineSpan)
        {
            var chars = lineSpan.ToArray();
            if (!chars.All(x => IsAllowedChar(x)))
            {
                return $"'{new string(chars.Select(x => IsAllowedChar(x) ? x : '?').ToArray()).TrimEnd('\n', '\r')}' ({string.Join(' ', chars.Select(x => ((byte)x).ToString("X2")))})";
            }
            else
            {
                var line = new string(lineSpan).TrimEnd('\n', '\r'); // remove newline at end
                return $"'{line}'";
            }
        }

        private void FlushStats(object sender, System.Timers.ElapsedEventArgs e)
        {
            var handledBytes = Interlocked.Exchange(ref _handledRecvBytes, 0);
            _byteCounter.Inc(handledBytes);
            var recvPixels = Interlocked.Exchange(ref _handledRecvPixels, 0);
            _pixelRecvCounter.Inc(recvPixels);
            var sentPixels = Interlocked.Exchange(ref _handledSentPixels, 0);
            _pixelSentCounter.Inc(sentPixels);

            StatsHub.Increment(handledBytes, recvPixels, sentPixels);
        }

        private class StreamBufferWrapper
        {
            private readonly Stream _stream;
            private readonly byte[] _buffer;
            private readonly int _bufferSize;
            private int _bufferPos;
            private int _bufferRemaining;

            public StreamBufferWrapper(Stream stream, int bufferSize)
            {
                _stream = stream;
                _buffer = new byte[bufferSize];
                _bufferSize = bufferSize;
                _bufferPos = 0;
                _bufferRemaining = 0;
            }

            public int ReadByte()
            {
                if (_bufferRemaining == 0)
                {
                    _bufferRemaining = _stream.Read(_buffer, 0, _bufferSize);
                    if (_bufferRemaining == 0)
                        return -1;
                    _bufferPos = 0;
                }

                _bufferRemaining--;
                return _buffer[_bufferPos++];
            }
        }
    }
}
