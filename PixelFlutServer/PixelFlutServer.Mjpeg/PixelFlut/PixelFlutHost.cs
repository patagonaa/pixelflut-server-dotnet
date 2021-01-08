using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Prometheus;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg.PixelFlut
{
    class PixelFlutHost : IHostedService
    {
        private readonly TcpListener _listener;
        private readonly ILogger<PixelFlutHost> _logger;
        private readonly IPixelFlutHandler _pixelFlutHandler;
        private readonly int _frameMs;
        private readonly int _width;
        private readonly int _height;
        private readonly int _bytesPerPixel;
        private readonly byte[] _pixels;
        private CancellationTokenSource _cts = new();
        private static SemaphoreSlim _frameSemaphore = new SemaphoreSlim(1, 1);
        private IList<PixelFlutConnectionInfo> _connectionInfos = new List<PixelFlutConnectionInfo>();
        private readonly Gauge _connectionCounter = Metrics.CreateGauge("pixelflut_connections", "Number of Pixelflut connections");

        public PixelFlutHost(ILogger<PixelFlutHost> logger, IPixelFlutHandler pixelFlutHandler, IOptions<PixelFlutServerConfig> options)
        {
            var config = options.Value;

            _listener = TcpListener.Create(config.PixelFlutPort);
            _logger = logger;
            _pixelFlutHandler = pixelFlutHandler;

            _frameMs = (int)(1000.0 / config.MaxFps);

            _width = config.Width;
            _height = config.Height;
            _bytesPerPixel = config.BytesPerPixel;

            _pixels = new byte[_width * _height * _bytesPerPixel];
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _listener.Start();
            Task.Factory.StartNew(() => PublishFrameWorker(), TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(() => ConnectionAcceptWorker(), TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(() => PrintStatsWorker(), TaskCreationOptions.LongRunning);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _listener.Stop();
            _cts.Cancel();
            return Task.CompletedTask;
        }

        private async Task PrintStatsWorker()
        {
            while (!_cts.IsCancellationRequested)
            {
                int connectionCount;
                lock (_connectionInfos)
                {
                    connectionCount = _connectionInfos.Count;
                }
                _connectionCounter.Set(connectionCount);
                _logger.LogDebug("PixelFlut Connections: {ConnectionCount}", connectionCount);
                await Task.Delay(10000);
            }
        }

        public async Task PublishFrameWorker()
        {
            var sw = new Stopwatch();

            while (!_cts.IsCancellationRequested)
            {
                sw.Restart();
                Thread.MemoryBarrier();
                await _frameSemaphore.WaitAsync();
                FrameHub.SetFrame(_pixels);

                await Task.Delay(Math.Max(0, _frameMs - (int)sw.ElapsedMilliseconds));
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
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                var connectionInfo = new PixelFlutConnectionInfo { EndPoint = client.Client.RemoteEndPoint };
                _logger.LogInformation("PixelFlut Connection from {Endpoint}", connectionInfo.EndPoint);
                lock (_connectionInfos)
                {
                    _connectionInfos.Add(connectionInfo);
                }

                try
                {
                    var buffer = new PixelBuffer
                    {
                        Width = _width,
                        Height = _height,
                        BytesPerPixel = _bytesPerPixel,
                        Buffer = _pixels
                    };
                    using (var stream = client.GetStream())
                    {
                        await _pixelFlutHandler.Handle(stream, connectionInfo.EndPoint, buffer, _frameSemaphore, _cts.Token);
                    }
                    _logger.LogInformation("PixelFlut connection {Endpoint} closed!", connectionInfo.EndPoint);
                }
                catch (IOException iex) when (iex.GetBaseException() is SocketException sex)
                {
                    _logger.LogInformation("PixelFlut connection {Endpoint} closed: {SocketErrorCode} / {ErrorCode}", connectionInfo.EndPoint, sex.SocketErrorCode, sex.ErrorCode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Something went wrong at PixelFlut connection {Endpoint}", connectionInfo.EndPoint);
                }
                finally
                {
                    _logger.LogDebug("PixelFlut connection {Endpoint} closed!", connectionInfo.EndPoint);
                    lock (_connectionInfos)
                    {
                        _connectionInfos.Remove(connectionInfo);
                    }
                }
            }
        }
    }
}
