using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg
{
    class PixelFlutHost : IHostedService
    {
        private readonly TcpListener _listener;
        private readonly ILogger<PixelFlutHost> _logger;
        private CancellationTokenSource _cts = new();
        private static SemaphoreSlim _frameSemaphore = new SemaphoreSlim(0, 1);

        private const int _width = 1920;
        private const int _height = 1080;
        private const int _bitsPerPixel = 3;

        private byte[] _pixels = new byte[_width * _height * _bitsPerPixel];

        public PixelFlutHost(ILogger<PixelFlutHost> logger)
        {
            _listener = TcpListener.Create(1234);
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _listener.Start();
            Task.Factory.StartNew(() => PublishFrameWorker(), TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(() => ConnectionAcceptWorker(), TaskCreationOptions.LongRunning);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _listener.Stop();
            _cts.Cancel();
            return Task.CompletedTask;
        }

        public async Task PublishFrameWorker()
        {
            while (!_cts.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                Thread.MemoryBarrier();
                await _frameSemaphore.WaitAsync();
                FrameHub.SetFrame((byte[])_pixels.Clone());
                sw.Stop();

                await Task.Delay(Math.Max(0, 33 - (int)sw.ElapsedMilliseconds));
            }
        }

        public async Task ConnectionAcceptWorker()
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Factory.StartNew(() => ConnectionHandler(client), TaskCreationOptions.LongRunning);
            }
        }

        private async void ConnectionHandler(TcpClient client)
        {
            using (client)
            {
                var endPoint = client.Client.RemoteEndPoint;
                _logger.LogInformation("Connection from {Endpoint}", endPoint);

                try
                {
                    using (var stream = client.GetStream())
                    {
                        var sr = new StreamReader(stream);
                        var sw = new StreamWriter(stream);
                        sw.NewLine = "\n";
                        sw.AutoFlush = true;

                        while (!_cts.IsCancellationRequested)
                        {
                            var line = await sr.ReadLineAsync();
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

                                if(y < 0 || y >= _height || x < 0 || x >= _width)
                                {
                                    _logger.LogWarning("Invliad coordinates at line {Line}", line);
                                    continue;
                                }

                                var pixelIndex = (y * _width + x) * _bitsPerPixel;
                                try
                                {
                                    if (hasAlpha)
                                    {
                                        color >>= 8;
                                    }
                                    _pixels[pixelIndex] = (byte)(color & 0xFF);
                                    _pixels[pixelIndex + 1] = (byte)(color >> 8 & 0xFF);
                                    _pixels[pixelIndex + 2] = (byte)(color >> 16 & 0xFF);
                                    if (_frameSemaphore.CurrentCount == 0)
                                    {
                                        _frameSemaphore.Release();
                                    }
                                }
                                catch (Exception)
                                {
                                    throw;
                                }

                            }
                            else if (splitLine[0] == "SIZE")
                            {
                                sw.WriteLine($"SIZE {_width} {_height}");
                            }
                        }
                    }
                }
                catch (IOException iex) when (iex.GetBaseException() is SocketException sex &&
                    (sex.SocketErrorCode == SocketError.ConnectionAborted || sex.SocketErrorCode == SocketError.ConnectionReset || sex.SocketErrorCode == SocketError.TimedOut))
                {
                    _logger.LogInformation("Connection {Endpoint} closed!", endPoint);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Something went wrong");
                }
            }

        }
    }
}
