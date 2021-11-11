namespace PixelFlutServer.Mjpeg
{
    public class Stats
    {
        public ulong ReceivedBytes { get; }
        public ulong ReceivedPixels { get; }
        public ulong SentPixels { get; }

        public Stats(ulong receivedBytes, ulong receivedPixels, ulong sentPixels)
        {
            ReceivedBytes = receivedBytes;
            ReceivedPixels = receivedPixels;
            SentPixels = sentPixels;
        }
    }
}
