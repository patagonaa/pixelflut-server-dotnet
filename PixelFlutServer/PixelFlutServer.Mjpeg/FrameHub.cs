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
            if (!LockUtils.WaitLockCancellable(ms => _frameEvent.WaitOne(ms), timeoutMs, token))
                throw new TimeoutException();
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
