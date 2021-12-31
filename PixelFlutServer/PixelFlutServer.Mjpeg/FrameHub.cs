using System;
using System.Threading;

namespace PixelFlutServer.Mjpeg
{
    public static class FrameHub
    {
        private static byte[] _currentFrame = null;
        private static readonly ManualResetEventSlim _frameEvent = new ManualResetEventSlim(false);

        public static byte[] WaitForFrame(CancellationToken token, int timeoutMs)
        {
            if(!_frameEvent.Wait(timeoutMs, token))
            {
                throw new TimeoutException();
            }
            _frameEvent.Reset();
            return _currentFrame;
        }

        public static void SetFrame(byte[] frame)
        {
            if (_currentFrame == null)
                _currentFrame = new byte[frame.Length];
            Array.Copy(frame, _currentFrame, frame.Length);
            _frameEvent.Set();
        }
    }
}
