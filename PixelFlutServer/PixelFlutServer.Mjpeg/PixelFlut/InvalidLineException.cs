using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg.PixelFlut
{
    internal class InvalidLineException : Exception
    {

        public InvalidLineException(EndPoint ep, string message)
            : base(message)
        {
            EndPoint = ep;
        }

        public EndPoint EndPoint { get; }
    }
}
