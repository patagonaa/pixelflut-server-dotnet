﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            await Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration(ConfigureAppConfiguration)
                .ConfigureServices(ConfigureServices)
                .ConfigureLogging(ConfigureLogging)
                .RunConsoleAsync();
        }

        private static void ConfigureLogging(HostBuilderContext ctx, ILoggingBuilder loggingBuilder)
        {
            loggingBuilder.ClearProviders();

            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .CreateLogger();
            loggingBuilder.AddSerilog(Log.Logger);
        }

        private static void ConfigureServices(HostBuilderContext ctx, IServiceCollection services)
        {
            services.AddSingleton<IPixelFlutHandler, PixelFlutSpanHandler>();
            services.AddHostedService<PixelFlutHost>();
            services.AddHostedService<MjpegHttpHost>();
        }

        private static void ConfigureAppConfiguration(HostBuilderContext ctx, IConfigurationBuilder configBuilder)
        {
            configBuilder
                .AddJsonFile("configs/appSettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
        }
    }
}
