# pixelflut-server-dotnet
This is a Pixelflut (see [here](https://github.com/defnull/pixelflut) or [here](https://cccgoe.de/wiki/Pixelflut) for details) server written in C# taking advantage of the relatively new performance-focused C# 7.2 feature `Span<T>` to reduce memory allocations during handling of Pixelflut traffic. On my system (i7-7820HQ, 32GB DDR4-2400) it is able to handle about 1 GBit/s per connection/thread (4-5 GBit/s total).

## Features
- Supported commands:
    - `SIZE\n` to get the current canvas size
    - `PX X Y RRGGBB\n` / `PX X Y RRGGBBAA\n` to set the color for the pixel at the given position
    - `PX X Y\n` to retrieve the pixel color at the given position 
    - `OFFSET X Y\n` to set an offset to apply to pixels sent in the future
- simple output viewing via MJPEG (supported on all major browsers)
- Prometheus metrics (pixels sent/received, bytes received, current number of Pixelflut and HTTP connections, etc.)

## Usage
If you're using Docker, `docker-compose up --build -d` should get you up and running in a few seconds.

If (for some reason) you don't want to use Docker, you can either run this on Windows using Visual Studio 2019 or on Linux using .NET SDK 5 and `libgdiplus`
