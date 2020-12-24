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
        private readonly IPixelFlutHandler _pixelFlutHandler;
        private CancellationTokenSource _cts = new();
        private static SemaphoreSlim _frameSemaphore = new SemaphoreSlim(0, 1);

        private const int _width = 1920;
        private const int _height = 1080;
        private const int _bytesPerPixel = 3;

        private byte[] _pixels = new byte[_width * _height * _bytesPerPixel];

        public PixelFlutHost(ILogger<PixelFlutHost> logger, IPixelFlutHandler pixelFlutHandler)
        {
            _listener = TcpListener.Create(1234);
            _logger = logger;
            _pixelFlutHandler = pixelFlutHandler;
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
            var sw = new Stopwatch();

            while (!_cts.IsCancellationRequested)
            {
                sw.Restart();
                Thread.MemoryBarrier();
                await _frameSemaphore.WaitAsync();
                FrameHub.SetFrame(_pixels);

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

        private void ConnectionHandler(TcpClient client)
        {
            using (client)
            {
                var endPoint = client.Client.RemoteEndPoint;
                _logger.LogInformation("Connection from {Endpoint}", endPoint);

                try
                {
                    var buffer = new PixelBuffer {
                        Width = _width,
                        Height = _height,
                        BytesPerPixel = _bytesPerPixel,
                        Buffer = _pixels
                    };
                    _pixelFlutHandler.Handle(client.GetStream(), endPoint, buffer, _frameSemaphore, _cts.Token);
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
