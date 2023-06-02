using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Prometheus;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
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
        private readonly FrameHub _frameHub;
        private readonly CancellationTokenSource _cts = new();
        private byte[] _currentJpeg = null;

        private readonly int _width;
        private readonly int _height;
        private readonly IList<MjpegConnectionInfo> _mjpegConnectionInfos = new List<MjpegConnectionInfo>();

        private readonly Gauge _connectionGauge = Metrics.CreateGauge("http_connections", "Number of running HTTP requests", "endpoint");
        private readonly Counter _connectionCounter = Metrics.CreateCounter("http_connections_total", "Number of HTTP requests since this instance started", "endpoint");

        public MjpegHttpHost(ILogger<MjpegHttpHost> logger, IOptions<PixelFlutServerConfig> options, FrameHub frameHub)
        {
            _config = options.Value;

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_config.MjpegPort}/");
            _logger = logger;
            _frameHub = frameHub;
            _width = _config.Width;
            _height = _config.Height;
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

        private void GetFrameWorker()
        {
            using (var image = new Image<Bgr24>(_config.Width, _config.Height))
            {

                using (var ms = new MemoryStream())
                {
                    var encoder = new JpegEncoder() { Quality = _config.JpegQualityPercent };

                    var registration = _frameHub.Register();

                    while (!_cts.IsCancellationRequested)
                    {
                        // send frame every now and then even if there's no new one available to give the TcpClient a chance to clean up old connections
                        registration.WaitForFrame(_cts.Token, 10000);

                        var frame = registration.GetCurrentFrame();

                        image.ProcessPixelRows(accessor =>
                        {
                            for (int y = 0; y < accessor.Height; y++)
                            {
                                var rowSpan = accessor.GetRowSpan(y);
                                for (int x = 0; x < accessor.Width; x++)
                                {
                                    var sourceIdx = (y * _config.Width + x) * Const.FrameBytesPerPixel;
                                    rowSpan[x].B = frame[sourceIdx];
                                    rowSpan[x].G = frame[sourceIdx + 1];
                                    rowSpan[x].R = frame[sourceIdx + 2];
                                }
                            }
                        });

                        if (!string.IsNullOrWhiteSpace(_config.AdditionalText))
                        {
                            throw new NotSupportedException("Additional Text not supported yet");
                            // using (var g = Graphics.FromImage(bitmap))
                            // {
                            //     var font = new Font("Consolas", _config.AdditionalTextSize);
                            //     var brush = new SolidBrush(Color.White);
                            //     var textSize = g.MeasureString(_config.AdditionalText, font);
                            //     var sizeX = (int)Math.Ceiling(textSize.Width);
                            //     var sizeY = (int)Math.Ceiling(textSize.Height);

                            //     g.FillRectangle(new SolidBrush(Color.Black), 0, _height - sizeY, sizeX, sizeY);
                            //     g.DrawString(_config.AdditionalText, font, brush, 0, _height - sizeY);
                            // }
                        }

                        ms.Position = 0;
                        ms.SetLength(0);
                        image.SaveAsJpeg(ms, encoder);
                        _currentJpeg = ms.ToArray();

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
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Factory.StartNew(() => ConnectionHandler(context), TaskCreationOptions.LongRunning);
                }
                catch (ObjectDisposedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (_cts.IsCancellationRequested)
                        throw;
                    _logger.LogError(ex, "Unhandled error while accepting HTTP connection");
                }
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
                    var urlPath = context.Request.Url.LocalPath;
                    var logLevel = urlPath == statsEndpoint ? LogLevel.Debug : LogLevel.Information;
                    _logger.Log(logLevel, "HTTP Connection from {Endpoint}: {HttpMethod} {Path}", context.Request.RemoteEndPoint, context.Request.HttpMethod, urlPath);
                    LogHeaders(logLevel, context.Request);
                    switch (urlPath)
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
                            await frameWaitSemaphore.WaitAsync(_cts.Token);
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

        private void LogHeaders(LogLevel logLevel, HttpListenerRequest request)
        {
            var headersToLog = new[] { "User-Agent", "Referer", "X-Forwarded-For", "Host" };
            foreach (var headerKey in request.Headers.AllKeys)
            {
                var headerValue = request.Headers[headerKey];
                if (headersToLog.Any(x => headerKey.Equals(x, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.Log(logLevel, "{HeaderKey}: {HeaderValue}", headerKey, headerValue);
                }
                else
                {
                    _logger.LogDebug("{HeaderKey}: {HeaderValue}", headerKey, headerValue);
                }
            }
        }
    }
}
