namespace PixelFlutServer.Mjpeg
{
    public class Stats
    {
        public ulong ReceivedBytes { get; }
        public ulong ReceivedPixels { get; }
        public ulong SentPixels { get; }
        public int PixelFlutConnections { get; set; }

        public Stats(ulong receivedBytes, ulong receivedPixels, ulong sentPixels, int pixelFlutConnections)
        {
            ReceivedBytes = receivedBytes;
            ReceivedPixels = receivedPixels;
            SentPixels = sentPixels;
            PixelFlutConnections = pixelFlutConnections;
        }
    }
}
