using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg.PixelFlut
{
    public interface IPixelFlutHandler
    {
        Task Handle(Stream stream, EndPoint endPoint, PixelBuffer pixelBuffer, AutoResetEvent frameReadyEvent, CancellationToken cancellationToken);
    }
}
