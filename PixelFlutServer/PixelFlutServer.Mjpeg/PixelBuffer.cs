namespace PixelFlutServer.Mjpeg
{
    public class PixelBuffer
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int BytesPerPixel { get; set; }
        public byte[] Buffer { get; set; }
    }
}
