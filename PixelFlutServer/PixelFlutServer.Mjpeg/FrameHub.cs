using System;
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
            if (_currentFrame == null)
                _currentFrame = new byte[frame.Length];
            Array.Copy(frame, _currentFrame, frame.Length);
            if (_frameSemaphore.CurrentCount == 0)
            {
                _frameSemaphore.Release();
            }
        }
    }
}
