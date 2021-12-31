using System;
using System.Threading;

namespace PixelFlutServer.Mjpeg
{
    public static class FrameHub
    {
        private static byte[] _currentFrame = null;
        private static readonly AutoResetEvent _frameEvent = new AutoResetEvent(false);

        public static byte[] WaitForFrame(CancellationToken token, int timeoutMs)
        {
            var waitTimeTotal = 0;
            var loopWaitTime = Math.Min(1000, timeoutMs);
            do
            {
                if (_frameEvent.WaitOne(loopWaitTime))
                {
                    return _currentFrame;
                }
                waitTimeTotal += loopWaitTime;
            } while (waitTimeTotal < timeoutMs && !token.IsCancellationRequested);

            throw new TimeoutException();
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
