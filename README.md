# pixelflut-server-dotnet
This is a Pixelflut server (see [here](https://github.com/defnull/pixelflut) or [here](https://cccgoe.de/wiki/Pixelflut) for details) written in C# taking advantage of the relatively new performance-focused C# 7.2 feature `Span<T>` to reduce memory allocations during handling of Pixelflut traffic. On my system (i7-7820HQ, 32GB DDR4-2400) it is able to handle about 1 GBit/s per connection/thread (4-5 GBit/s total).

## Features
- Supported commands:
    - `SIZE` to get the current canvas size
    - `PX X Y RRGGBB` / `PX X Y RRGGBBAA` to set the color for the pixel at the given position
    - `PX X Y` to retrieve the pixel color at the given position 
    - `OFFSET X Y` to set an offset to apply to pixels sent in the future
    - `HELP` to get a short help text about pixelflut
- simple output viewing via MJPEG (supported on all major browsers)
- optional NDI output
- Prometheus metrics (pixels sent/received, bytes received, current number of Pixelflut and HTTP connections, etc.)

## Usage
If you're using Docker, `docker-compose up --build -d` should get you up and running in a few seconds.

If  you don't want to use Docker, you can either run this on Windows using Visual Studio or on Windows/Linux/macOS using [.NET SDK](https://docs.microsoft.com/de-de/dotnet/core/install/linux). Settings can also be set via environment variables.

Installation on Ubuntu (e.g. 22.04):
```bash
sudo apt update && sudo apt install dotnet-sdk-6.0
```

Example startup with HTTP port 8080, Pixelflut port 1234 (for more config options see [PixelFlutServerConfig.cs](https://github.com/patagonaa/pixelflut-server-dotnet/blob/main/PixelFlutServer/PixelFlutServer.Mjpeg/PixelFlutServerConfig.cs)):
```bash
cd PixelFlutServer/PixelFlutServer.Mjpeg
MjpegPort=8080 PixelFlutPort=1234 dotnet run -c Release
```
