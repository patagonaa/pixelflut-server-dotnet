FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /app
# copy csproj only so restored project will be cached
COPY PixelFlutServer/PixelFlutServer.Mjpeg/PixelFlutServer.Mjpeg.csproj /app/PixelFlutServer.Mjpeg/
RUN dotnet restore PixelFlutServer.Mjpeg/PixelFlutServer.Mjpeg.csproj
COPY PixelFlutServer/PixelFlutServer.Mjpeg /app/PixelFlutServer.Mjpeg
RUN dotnet publish -c Release PixelFlutServer.Mjpeg/PixelFlutServer.Mjpeg.csproj -o /app/build

FROM mcr.microsoft.com/dotnet/runtime:6.0
WORKDIR /app
RUN apt-get update
RUN apt-get install -y --allow-unauthenticated fonts-hack-ttf
COPY --from=build /app/build/ ./
ENTRYPOINT ["dotnet", "PixelFlutServer.Mjpeg.dll"]
