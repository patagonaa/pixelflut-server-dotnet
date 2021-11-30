using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Prometheus;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg.Http
{
    class MjpegHttpHost : IHostedService
    {
        private const string _boundaryString = "thisisaboundary";
        private readonly PixelFlutServerConfig _config;
        private readonly HttpListener _listener;
        private readonly ILogger<MjpegHttpHost> _logger;
        private readonly CancellationTokenSource _cts = new();
        private byte[] _currentJpeg = null;

        private readonly int _width;
        private readonly int _height;
        private readonly int _bytesPerPixel;
        private readonly IList<MjpegConnectionInfo> _mjpegConnectionInfos = new List<MjpegConnectionInfo>();

        private readonly Gauge _connectionGauge = Metrics.CreateGauge("http_connections", "Number of HTTP connections", "endpoint");
        private readonly Counter _connectionCounter = Metrics.CreateCounter("http_connections_total", "Number of HTTP connections since this instance started", "endpoint");

        public MjpegHttpHost(ILogger<MjpegHttpHost> logger, IOptions<PixelFlutServerConfig> options)
        {
            _config = options.Value;

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_config.MjpegPort}/");
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
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _listener.Stop();
            _cts.Cancel();
            return Task.CompletedTask;
        }

        private async Task GetFrameWorker()
        {
            using (var bitmap = new Bitmap(_width, _height, PixelFormat.Format24bppRgb))
            {
                using (var ms = new MemoryStream())
                {
                    var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    long quality = _config.JpegQualityPercent;
                    var encParams = new EncoderParameters { Param = new[] { new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality) } };

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
                        lock (_mjpegConnectionInfos)
                        {
                            foreach (var info in _mjpegConnectionInfos)
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
                var context = await _listener.GetContextAsync();
                _ = Task.Factory.StartNew(() => ConnectionHandler(context), TaskCreationOptions.LongRunning);
            }
        }

        private async Task ConnectionHandler(HttpListenerContext context)
        {
            const string streamEndpoint = "/stream.jpg";
            const string htmlEndpoint = "/";
            const string statsEndpoint = "/stats.json";

            var handledEndpoints = new[] { streamEndpoint, htmlEndpoint, statsEndpoint };

            var endpointForMetrics = handledEndpoints.FirstOrDefault(x => x == context.Request.Url.LocalPath) ?? "other";

            _connectionCounter.WithLabels(endpointForMetrics).Inc();
            using (_connectionGauge.WithLabels(endpointForMetrics).TrackInProgress())
            {
                try
                {
                    var forwardedFor = context.Request.Headers.Get("X-Forwarded-For");

                    var endPointForLog = forwardedFor == null ? context.Request.RemoteEndPoint.ToString() : $"{context.Request.RemoteEndPoint} ({forwardedFor})";
                    
                    _logger.LogInformation("HTTP Connection from {Endpoint}: {HttpMethod} {Path}", endPointForLog, context.Request.HttpMethod, context.Request.Url.LocalPath);
                    switch (context.Request.Url.LocalPath)
                    {
                        case streamEndpoint:
                            await ConnectionHandlerMJpeg(context);
                            break;
                        case htmlEndpoint:
                            {
                                context.Response.ContentType = "text/html";
                                using var outputStream = context.Response.OutputStream;
                                using (var htmlStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PixelFlutServer.Mjpeg.Http.index.html"))
                                {
                                    await htmlStream.CopyToAsync(outputStream);
                                }
                            }
                            break;
                        case statsEndpoint:
                            {
                                context.Response.ContentType = "application/json";
                                using var outputStream = context.Response.OutputStream;
                                await outputStream.WriteAsync(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(StatsHub.GetStats())));
                            }
                            break;
                        default:
                            {
                                context.Response.StatusCode = 404;
                                using var outputStream = context.Response.OutputStream;
                                await outputStream.WriteAsync(Encoding.UTF8.GetBytes("<h1>Not Found</h1>"));
                            }
                            break;
                    }
                    _logger.LogDebug("HTTP connection {Endpoint} closed!", context.Request.RemoteEndPoint);
                }
                catch (IOException iex) when (iex.GetBaseException() is SocketException sex)
                {
                    _logger.LogInformation("HTTP connection {Endpoint} closed: SocketError {SocketErrorCode} / Error {ErrorCode}", context.Request.RemoteEndPoint, sex.SocketErrorCode, sex.ErrorCode);
                }
                catch (HttpListenerException lex)
                {
                    _logger.LogInformation("HTTP connection {Endpoint} closed: Error {ErrorCode}", context.Request.RemoteEndPoint, lex.ErrorCode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Something went wrong at HTTP connection {Endpoint}", context.Request.RemoteEndPoint);
                }
            }
        }
        private async Task ConnectionHandlerMJpeg(HttpListenerContext context)
        {
            var connectionInfo = new MjpegConnectionInfo { EndPoint = context.Request.RemoteEndPoint, FrameWaitSemaphore = new SemaphoreSlim(0, 1) };

            lock (_mjpegConnectionInfos)
            {
                _mjpegConnectionInfos.Add(connectionInfo);
            }

            var frameWaitSemaphore = connectionInfo.FrameWaitSemaphore;

            // output frame once at the start so the user can see something even when there's nothing currently flooding
            if (_currentJpeg != null)
                frameWaitSemaphore.Release();
            try
            {
                context.Response.ContentType = "multipart/x-mixed-replace;boundary=" + _boundaryString;
                context.Response.SendChunked = true;
                using (var stream = context.Response.OutputStream)
                {
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

                        await stream.WriteAsync(Encoding.UTF8.GetBytes($"--{_boundaryString}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n"));
                        await stream.WriteAsync(frame);
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("\r\n"));
                        await stream.FlushAsync();
                    }
                }
            }
            finally
            {
                lock (_mjpegConnectionInfos)
                {
                    _mjpegConnectionInfos.Remove(connectionInfo);
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
