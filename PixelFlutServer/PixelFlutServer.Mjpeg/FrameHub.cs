using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Threading;

namespace PixelFlutServer.Mjpeg
{
    public class FrameHub
    {
        private readonly byte[] _currentFrame;
        private readonly PixelFlutServerConfig _config;
        private List<FrameRegistration> _registrations = new();

        public FrameHub(IOptions<PixelFlutServerConfig> config)
        {
            _config = config.Value;
            _currentFrame = new byte[_config.Width * _config.Height * 3];
        }

        public FrameRegistration Register()
        {
            var reg = new FrameRegistration(_currentFrame);
            _registrations.Add(reg);
            return reg;
        }

        public void SetFrame(byte[] frame)
        {
            if (frame.Length != _currentFrame.Length)
                throw new ArgumentException("Invalid Frame length!");

            Array.Copy(frame, _currentFrame, frame.Length);
            foreach (var registration in _registrations)
            {
                registration.AnnonunceFrame();
            }
        }

        public class FrameRegistration
        {
            private readonly SemaphoreSlim _semaphore = new(1, 1);
            private readonly byte[] _frame;

            public FrameRegistration(byte[] frame)
            {
                _frame = frame;
            }

            public bool WaitForFrame(CancellationToken token, int timeoutMs)
            {
                return _semaphore.Wait(timeoutMs, token);
            }

            public byte[] GetCurrentFrame()
            {
                return _frame;
            }

            internal void AnnonunceFrame()
            {
                try
                {
                    if (_semaphore.CurrentCount == 0)
                    {
                        _semaphore.Release();
                    }
                }
                catch (SemaphoreFullException)
                {
                }
            }
        }
    }
}
