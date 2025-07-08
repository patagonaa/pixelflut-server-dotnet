using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewTek.NDI;
using System.Threading;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg.Ndi
{
    internal class NdiHost : IHostedService
    {
        private readonly PixelFlutServerConfig _config;
        private readonly CancellationTokenSource _cts = new();
        private readonly FrameHub _frameHub;
        private readonly ILogger<NdiHost> _logger;

        public NdiHost(IOptions<PixelFlutServerConfig> config, FrameHub frameHub, ILogger<NdiHost> logger)
        {
            _config = config.Value;
            _frameHub = frameHub;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_config.EnableNdi)
            {
                var thread = new Thread(Worker);
                thread.Start();
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }

        private unsafe void Worker()
        {
            using var sender = new Sender("Pixelflut!");

            var width = _config.Width;
            var height = _config.Height;
            var pixels = width * height;
            var buffer = new byte[pixels * 4]; // bgra
            fixed (byte* bufptr = buffer)
            {
                var vf = new VideoFrame((nint)bufptr, width, height, width * 4, NewTek.NDIlib.FourCC_type_e.FourCC_type_BGRX, (float)width / height, _config.MaxFps, 1, NewTek.NDIlib.frame_format_type_e.frame_format_type_progressive);

                var registration = _frameHub.Register();

                while (!_cts.IsCancellationRequested)
                {
                    registration.WaitForFrame(_cts.Token, 1000);
                    var frame = registration.GetCurrentFrame();

                    int i = 0, j = 0;
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            buffer[i++] = frame[j++];
                            buffer[i++] = frame[j++];
                            buffer[i++] = frame[j++];
                            i++;
                        }
                    }

                    sender.Send(vf);
                    if (sender.Connections == 0)
                    {
                        _logger.LogInformation("No NDI connections, waiting...");
                        Thread.Sleep(1000);
                    }
                }
            }
        }
    }
}
