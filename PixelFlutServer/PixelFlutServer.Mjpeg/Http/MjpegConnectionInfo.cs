using System.Net;
using System.Threading;

namespace PixelFlutServer.Mjpeg.Http
{
    class MjpegConnectionInfo
    {
        public EndPoint EndPoint { get; set; }
        public SemaphoreSlim FrameWaitSemaphore { get; set; }
    }
}
