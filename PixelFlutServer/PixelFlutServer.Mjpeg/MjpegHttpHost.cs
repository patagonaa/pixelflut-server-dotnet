﻿using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg
{
    class MjpegHttpHost : IHostedService
    {
        private readonly TcpListener _listener;
        private readonly ILogger<MjpegHttpHost> _logger;
        private CancellationTokenSource _cts = new();

        private ConcurrentDictionary<Guid, SemaphoreSlim> _frameWaitSemaphores = new();
        private byte[] _currentJpeg = null;

        private readonly int _width;
        private readonly int _height;
        private readonly int _bytesPerPixel;

        public MjpegHttpHost(ILogger<MjpegHttpHost> logger, IOptions<PixelFlutServerConfig> options)
        {
            var config = options.Value;

            _listener = TcpListener.Create(config.MjpegPort);
            _logger = logger;

            _width = config.Width;
            _height = config.Height;
            _bytesPerPixel = config.BytesPerPixel;
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
                while (!_cts.IsCancellationRequested)
                {
                    var frame = await FrameHub.WaitForFrame(_cts.Token);

                    var data = bitmap.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                    Marshal.Copy(frame, 0, data.Scan0, _width * _height * _bytesPerPixel);
                    bitmap.UnlockBits(data);
                    using (var ms = new MemoryStream())
                    {
                        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                        var encParams = new EncoderParameters() { Param = new[] { new EncoderParameter(Encoder.Quality, 70L) } };
                        bitmap.Save(ms, encoder, encParams);
                        _currentJpeg = ms.ToArray();
                    }

                    foreach (var semaphore in _frameWaitSemaphores.Values)
                    {
                        if (semaphore.CurrentCount == 0)
                            semaphore.Release();
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
                var endPoint = client.Client.RemoteEndPoint;
                var connectionId = Guid.NewGuid();
                _logger.LogInformation("HTTP Connection from {Endpoint}", endPoint);
                var frameWaitSemaphore = new SemaphoreSlim(0, 1);

                // output frame once at the start so the user can see something even when there's nothing currently flooding
                if (_currentJpeg != null)
                    frameWaitSemaphore.Release();

                _frameWaitSemaphores.TryAdd(connectionId, frameWaitSemaphore);
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
                        var splitCmd = cmd.Split(' ');

                        if (splitCmd[0] != "GET" || splitCmd[1] != "/")
                            return;

                        while (sr.ReadLine() != "")
                        {
                        }

                        sw.WriteLine("HTTP/1.1 200 OK");
                        sw.WriteLine("Content-Type: multipart/x-mixed-replace;boundary=thisisaboundary");
                        sw.WriteLine();

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

                            await sw.WriteLineAsync("--thisisaboundary");
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
                catch (IOException iex) when (iex.GetBaseException() is SocketException sex &&
                    (sex.SocketErrorCode == SocketError.ConnectionAborted || sex.SocketErrorCode == SocketError.ConnectionReset || sex.SocketErrorCode == SocketError.TimedOut))
                {
                    _logger.LogInformation("HTTP Connection {Endpoint} closed!", endPoint);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Something went wrong");
                }
                finally
                {
                    _frameWaitSemaphores.TryRemove(connectionId, out _);
                }
            }
        }
    }
}
