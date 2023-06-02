using CommunityToolkit.HighPerformance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Threading;
using System.Threading.Tasks;
using VL.IO.NDI;
using VL.Lib.Basics.Video;

namespace PixelFlutServer.Mjpeg.Ndi
{
    internal class NdiHost : IHostedService
    {
        private readonly PixelFlutServerConfig _config;
        private readonly CancellationTokenSource _cts = new();
        private readonly FrameHub _frameHub;

        public NdiHost(IOptions<PixelFlutServerConfig> config, FrameHub frameHub)
        {
            _config = config.Value;
            _frameHub = frameHub;
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

        private void Worker()
        {
            using var sender = new Sender("Pixelflut!");

            var width = _config.Width;
            var height = _config.Height;
            var buffer = new BgrxPixel[height, width];
            var data = new Memory2D<BgrxPixel>(buffer);
            var vf = new VideoFrame<BgrxPixel>(data, FrameRate: (_config.MaxFps, 1));

            var registration = _frameHub.Register();

            while (!_cts.IsCancellationRequested)
            {
                if (sender.Connections == 0)
                {
                    Thread.Sleep(1000);
                    continue;
                }
                registration.WaitForFrame(_cts.Token, 1000);
                var frame = registration.GetCurrentFrame();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        var idx = (y * width + x) * Const.FrameBytesPerPixel;
                        buffer[y, x].B = frame[idx];
                        buffer[y, x].G = frame[idx + 1];
                        buffer[y, x].R = frame[idx + 2];
                    }
                }

                sender.Send(vf);
            }
        }
    }
}
