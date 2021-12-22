using System;
using System.Threading;

namespace PixelFlutServer.Mjpeg
{
    public static class FrameHub
    {
        private static byte[] _currentFrame = null;
        private static readonly SemaphoreSlim _frameSemaphore = new SemaphoreSlim(0, 1);

        public static byte[] WaitForFrame(CancellationToken token, int timeoutMs)
        {
            if(!_frameSemaphore.Wait(timeoutMs, token))
            {
                throw new TimeoutException();
            }
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
