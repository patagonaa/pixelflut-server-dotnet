using System.IO;
using System.Net;
using System.Threading;

namespace PixelFlutServer.Mjpeg
{
    public interface IPixelFlutHandler
    {
        void Handle(Stream stream, EndPoint endPoint, PixelBuffer pixelBuffer, SemaphoreSlim frameReadySemaphore, CancellationToken cancellationToken);
    }
}
