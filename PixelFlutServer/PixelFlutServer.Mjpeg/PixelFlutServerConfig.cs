namespace PixelFlutServer.Mjpeg
{
    class PixelFlutServerConfig
    {
        public int PixelFlutPort { get; set; } = 1234;
        public int MjpegPort { get; set; } = 8080;
        public double MaxFps { get; set; } = 60;
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public int BytesPerPixel { get; } = 720;
    }
}
