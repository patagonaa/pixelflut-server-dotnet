using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg
{
    public static class FrameHub
    {
        private static byte[] _currentFrame = null;
        private static SemaphoreSlim _frameSemaphore = new SemaphoreSlim(0, 1);

        public static async Task<byte[]> WaitForFrame(CancellationToken token)
        {
            await _frameSemaphore.WaitAsync(token);
            return _currentFrame;
        }

        public static void SetFrame(byte[] frame)
        {
            _currentFrame = frame;
            if (_frameSemaphore.CurrentCount == 0)
            {
                _frameSemaphore.Release();
            }
        }
    }
}
