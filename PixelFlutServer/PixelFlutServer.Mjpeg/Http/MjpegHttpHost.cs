using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Prometheus;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg.Http
{
    class MjpegHttpHost : IHostedService
    {
        private const string _boundaryString = "thisisaboundary";
        private readonly PixelFlutServerConfig _config;
        private readonly TcpListener _listener;
        private readonly ILogger<MjpegHttpHost> _logger;
        private readonly CancellationTokenSource _cts = new();
        private byte[] _currentJpeg = null;

        private readonly int _width;
        private readonly int _height;
        private readonly int _bytesPerPixel;
        private readonly IList<MjpegConnectionInfo> _connectionInfos = new List<MjpegConnectionInfo>();
        private readonly Gauge _connectionCounter = Metrics.CreateGauge("mjpeg_http_connections", "Number of MJPEG HTTP connections");
        private readonly Counter _connectionCounterTotal = Metrics.CreateCounter("mjpeg_http_connections_total", "Number of MJPEG HTTP connections since this instance started");

        public MjpegHttpHost(ILogger<MjpegHttpHost> logger, IOptions<PixelFlutServerConfig> options)
        {
            _config = options.Value;

            _listener = TcpListener.Create(_config.MjpegPort);
            _logger = logger;

            _width = _config.Width;
            _height = _config.Height;
            _bytesPerPixel = _config.BytesPerPixel;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _listener.Start();
            Task.Factory.StartNew(() => ConnectionAcceptWorker(), TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(() => GetFrameWorker(), TaskCreationOptions.LongRunning);
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
                _logger.LogDebug("HTTP Connections: {ConnectionCount}", connectionCount);
                await Task.Delay(10000);
            }
        }

        private async Task GetFrameWorker()
        {
            using (var bitmap = new Bitmap(_width, _height, PixelFormat.Format24bppRgb))
            {
                using (var ms = new MemoryStream())
                {
                    var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    long quality = _config.JpegQualityPercent;
                    var encParams = new EncoderParameters { Param = new[] { new EncoderParameter(Encoder.Quality, quality) } };

                    while (!_cts.IsCancellationRequested)
                    {
                        byte[] frame = null;

                        // send frame every now and then even if there's no new one available to give the TcpClient a chance to clean up old connections
                        try
                        {
                            frame = await FrameHub.WaitForFrame(_cts.Token, 10000);
                        }
                        catch (TimeoutException)
                        {
                        }
                        catch (OperationCanceledException)
                        {
                        }

                        if (frame != null)
                        {
                            var data = bitmap.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                            Marshal.Copy(frame, 0, data.Scan0, _width * _height * _bytesPerPixel);
                            bitmap.UnlockBits(data);

                            ms.Position = 0;
                            ms.SetLength(0);
                            bitmap.Save(ms, encoder, encParams);
                            _currentJpeg = ms.ToArray();
                        }
                        lock (_connectionInfos)
                        {
                            foreach (var info in _connectionInfos)
                            {
                                var semaphore = info.FrameWaitSemaphore;
                                try
                                {
                                    if (semaphore.CurrentCount == 0)
                                        semaphore.Release();
                                }
                                catch (SemaphoreFullException)
                                {
                                }
                            }
                        }
                    }
                }
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

        public async void ConnectionHandler(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;

                var connectionInfo = new MjpegConnectionInfo { EndPoint = client.Client.RemoteEndPoint, FrameWaitSemaphore = new SemaphoreSlim(0, 1) };
                _logger.LogInformation("HTTP Connection from {Endpoint}", connectionInfo.EndPoint);
                lock (_connectionInfos)
                {
                    _connectionInfos.Add(connectionInfo);
                }
                _connectionCounterTotal.Inc();

                var frameWaitSemaphore = connectionInfo.FrameWaitSemaphore;

                // output frame once at the start so the user can see something even when there's nothing currently flooding
                if (_currentJpeg != null)
                    frameWaitSemaphore.Release();
                try
                {
                    using (var stream = client.GetStream())
                    {
                        var sr = new StreamReader(stream);
                        var sw = new StreamWriter(stream);
                        sw.NewLine = "\r\n";

                        var cmd = sr.ReadLine();
                        if (cmd == null)
                            return;

                        _logger.LogDebug(cmd);
                        var splitCmd = cmd.Split(' ');

                        if (splitCmd.Length != 3 || splitCmd[0] != "GET")
                        {
                            _logger.LogInformation("Invalid HTTP Request: {Request}", cmd);
                            await LogHeaders(sr);
                            await sw.WriteLineAsync("HTTP/1.0 400 Bad Request");
                            await sw.WriteLineAsync("");
                            await sw.FlushAsync();
                        }
                        else if (splitCmd[1] != "/")
                        {
                            _logger.LogInformation("Invalid HTTP Request: {Request}", cmd);
                            await LogHeaders(sr);
                            await sw.WriteLineAsync("HTTP/1.0 404 Not Found");
                            await sw.WriteLineAsync("");
                            await sw.FlushAsync();
                        }
                        else
                        {
                            await LogHeaders(sr);
                            await sw.WriteLineAsync("HTTP/1.1 200 OK");

                            await sw.WriteLineAsync($"Content-Type: multipart/x-mixed-replace;boundary={_boundaryString}");
                            await sw.WriteLineAsync();

                            var first = true;

                            while (!_cts.IsCancellationRequested)
                            {
                                // this makes sure the first frame is sent twice, workaround for chromium bug
                                // https://bugs.chromium.org/p/chromium/issues/detail?id=527446
                                if (first && _currentJpeg != null)
                                {
                                    first = false;
                                }
                                else
                                {
                                    await frameWaitSemaphore.WaitAsync();
                                }

                                var frame = _currentJpeg;

                                await sw.WriteLineAsync($"--{_boundaryString}");
                                await sw.WriteLineAsync("Content-Type: image/jpeg");
                                await sw.WriteLineAsync($"Content-Length: {frame.Length}");
                                await sw.WriteLineAsync();
                                await sw.FlushAsync();

                                await stream.WriteAsync(frame);

                                await sw.WriteLineAsync();
                                await sw.FlushAsync();
                            }
                        }
                    }
                    _logger.LogInformation("HTTP connection {Endpoint} closed!", connectionInfo.EndPoint);
                }
                catch (IOException iex) when (iex.GetBaseException() is SocketException sex)
                {
                    _logger.LogInformation("HTTP connection {Endpoint} closed: {SocketErrorCode} / {ErrorCode}", connectionInfo.EndPoint, sex.SocketErrorCode, sex.ErrorCode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Something went wrong at HTTP connection {Endpoint}", connectionInfo.EndPoint);
                }
                finally
                {
                    _logger.LogDebug("HTTP connection {Endpoint} closed!", connectionInfo.EndPoint);
                    lock (_connectionInfos)
                    {
                        _connectionInfos.Remove(connectionInfo);
                    }
                }
            }
        }

        private async Task LogHeaders(StreamReader sr)
        {
            string headerLine;
            while (!string.IsNullOrEmpty(headerLine = await sr.ReadLineAsync()))
            {
                if (headerLine.StartsWith("User-Agent: ", StringComparison.OrdinalIgnoreCase) ||
                    headerLine.StartsWith("Referer: ", StringComparison.OrdinalIgnoreCase) ||
                    headerLine.StartsWith("X-Forwarded-For: ", StringComparison.OrdinalIgnoreCase) ||
                    headerLine.StartsWith("Host: ", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(headerLine);
                }
                else
                {
                    _logger.LogDebug(headerLine);
                }
            }
        }
    }
}
