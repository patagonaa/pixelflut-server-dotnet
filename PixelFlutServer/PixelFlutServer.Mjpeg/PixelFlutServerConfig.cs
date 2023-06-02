namespace PixelFlutServer.Mjpeg
{
    public class PixelFlutServerConfig
    {
        public int PixelFlutPort { get; set; } = 1234;
        public int MjpegPort { get; set; } = 8080;

        public bool EnableNdi { get; set; } = false;
        /// <summary>
        /// Max FPS to output (lower = less bandwidth)
        /// </summary>
        public int MaxFps { get; set; } = 60;
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        /// <summary>
        /// MJPEG quality (lower = less bandwidth)
        /// </summary>
        public int JpegQualityPercent { get; set; } = 70;
        /// <summary>
        /// URI to redirect to when HTTP requests are detected on the pixelflut port
        /// </summary>
        public string HttpServerUri { get; set; }
        /// <summary>
        /// Directory where to save the canvas to so it can be loaded after a restart
        /// </summary>
        public string PersistPath { get; set; } = ".";
        /// <summary>
        /// Text to show in the lower left corner of the canvas
        /// </summary>
        public string AdditionalText { get; set; }
        public int AdditionalTextSize { get; set; } = 14;
    }
}
