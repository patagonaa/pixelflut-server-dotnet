using System;

namespace PixelFlutServer.Mjpeg
{
    public static class StatsHub
    {
        private static object _syncLock = new object();
        private static ulong _receivedBytes;
        private static ulong _receivedPixels;
        private static ulong _sentPixels;

        public static void Increment(ulong receivedBytes, ulong receivedPixels, ulong sentPixels)
        {
            lock (_syncLock)
            {
                _receivedBytes += receivedBytes;
                _receivedPixels += receivedPixels;
                _sentPixels += sentPixels;
            }
        }

        public static Stats GetStats()
        {
            return new Stats(_receivedBytes, _receivedPixels, _sentPixels);
        }
    }
}
