namespace PixelFlutServer.Mjpeg
{
    public class PixelFlutServerConfig
    {
        public int PixelFlutPort { get; set; } = 1234;
        public int MjpegPort { get; set; } = 8080;
        public double MaxFps { get; set; } = 60;
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public int JpegQualityPercent { get; set; } = 70;
        public string HttpServerUri { get; set; }
        public string PersistPath { get; set; } = ".";
        public string AdditionalText { get; set; }
        public int AdditionalTextSize { get; set; } = 14;
        public int NetworkBufferSize { get; set; } = 1048576;
    }
}
