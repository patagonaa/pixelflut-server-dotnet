using System;

namespace PixelFlutServer.Mjpeg
{
    public static class StatsHub
    {
        private static object _syncLock = new object();
        private static ulong _receivedBytes;
        private static ulong _receivedPixels;
        private static ulong _sentPixels;
        private static int _pixelFlutConnectionCount;

        public static void Increment(ulong receivedBytes, ulong receivedPixels, ulong sentPixels)
        {
            lock (_syncLock)
            {
                _receivedBytes += receivedBytes;
                _receivedPixels += receivedPixels;
                _sentPixels += sentPixels;
            }
        }

        public static void SetConnectionCount(int pixelFlutConnectionCount)
        {
            _pixelFlutConnectionCount = pixelFlutConnectionCount;
        }

        public static void SetStats(Stats stats)
        {
            lock (_syncLock)
            {
                _receivedBytes += stats.ReceivedBytes;
                _receivedPixels += stats.ReceivedPixels;
                _sentPixels += stats.SentPixels;
            }
        }

        public static Stats GetStats()
        {
            return new Stats(_receivedBytes, _receivedPixels, _sentPixels, _pixelFlutConnectionCount);
        }
    }
}
