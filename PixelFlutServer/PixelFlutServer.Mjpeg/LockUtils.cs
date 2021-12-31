using System;
using System.Threading;

namespace PixelFlutServer.Mjpeg
{
    class LockUtils
    {
        private const int _maxCancelWaitTime = 1000;
        public static bool WaitLockCancellable(Func<int, bool> lockFunc, int timeoutMs = Timeout.Infinite, CancellationToken cancellationToken = default)
        {
            var waitTimeTotal = 0;
            var loopWaitTime = timeoutMs == Timeout.Infinite ? _maxCancelWaitTime : Math.Min(_maxCancelWaitTime, timeoutMs);
            do
            {
                if (lockFunc(loopWaitTime))
                {
                    return true;
                }
                waitTimeTotal += loopWaitTime;
            } while ((timeoutMs == Timeout.Infinite || waitTimeTotal < timeoutMs) && !cancellationToken.IsCancellationRequested);
            return false;
        }
    }
}
