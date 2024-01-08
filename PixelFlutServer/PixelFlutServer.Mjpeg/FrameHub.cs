using Microsoft.Extensions.Options;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PixelFlutServer.Mjpeg
{
    public class FrameHub
    {
        private readonly byte[] _currentFrameIn;
        private readonly byte[] _currentFrameOut;
        private readonly Lazy<Font> _font;
        private readonly Image<Bgr24> _image;
        private readonly PixelFlutServerConfig _config;
        private readonly List<FrameRegistration> _registrations = new();

        public FrameHub(IOptions<PixelFlutServerConfig> config)
        {
            _config = config.Value;
            _currentFrameIn = new byte[_config.Width * _config.Height * Const.FrameBytesPerPixel];
            _currentFrameOut = new byte[_config.Width * _config.Height * Const.FrameBytesPerPixel];
            _font = new Lazy<Font>(() => GetFont());
            _image = new Image<Bgr24>(_config.Width, _config.Height);
        }

        private Font GetFont()
        {
            var fc = new FontCollection();
            fc.AddSystemFonts();

            FontFamily family;
            if (!fc.TryGet("Consolas", out family) && !fc.TryGet("Hack", out family))
            {
                throw new Exception("Font not found");
            }

            return family.CreateFont(_config.AdditionalTextSize);
        }

        public FrameRegistration Register()
        {
            var reg = new FrameRegistration(_currentFrameOut);
            _registrations.Add(reg);
            return reg;
        }

        public void SetFrame(byte[] frame)
        {
            if (frame.Length != _currentFrameIn.Length)
                throw new ArgumentException("Invalid Frame length!");

            if (!string.IsNullOrEmpty(_config.AdditionalText))
            {
                Array.Copy(frame, _currentFrameIn, frame.Length);

                _image.ProcessPixelRows(accessor =>
                {
                    var frameSpan = MemoryMarshal.Cast<byte, Bgr24>(_currentFrameIn);

                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var rowAccessor = accessor.GetRowSpan(y);
                        frameSpan.Slice(y * accessor.Width, accessor.Width).CopyTo(rowAccessor);
                    }
                });

                var textOptions = new TextOptions(_font.Value)
                {
                    TextAlignment = TextAlignment.Start,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Origin = new PointF(0, _config.Height)
                };
                var textMeasured = TextMeasurer.Measure(_config.AdditionalText, textOptions);
                _image.Mutate(x => x
                    .Fill(Color.Black, new RectangleF(0, _config.Height - textMeasured.Height, textMeasured.Width, textMeasured.Height))
                    .DrawText(textOptions, _config.AdditionalText, Color.White));

                _image.CopyPixelDataTo(_currentFrameOut);
            }
            else
            {
                Array.Copy(frame, _currentFrameOut, frame.Length);
            }

            foreach (var registration in _registrations)
            {
                registration.AnnounceFrame();
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

            internal void AnnounceFrame()
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
