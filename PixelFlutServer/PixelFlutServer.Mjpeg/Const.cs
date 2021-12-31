using System.Drawing;
using System.Drawing.Imaging;

namespace PixelFlutServer.Mjpeg
{
    public class Const
    {
        public static readonly PixelFormat FramePixelFormat = PixelFormat.Format24bppRgb;
        public static readonly int FrameBytesPerPixel = Image.GetPixelFormatSize(FramePixelFormat) / 8;
    }
}
