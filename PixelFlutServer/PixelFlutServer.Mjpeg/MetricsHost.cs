using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PixelFlutServer.Mjpeg
{
    public class MetricsHost : IHostedService
    {
        private readonly MetricServer _server;
        private readonly ILogger _logger;

        public MetricsHost(ILogger<MetricsHost> logger)
        {
            _server = new MetricServer(8081);
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            new Thread(Main).Start();
            return Task.CompletedTask;
        }

        private void Main()
        {
            try
            {
                _server.Start();
            }
            catch (HttpListenerException ex)
            {
                _logger.LogWarning(ex, "Metrics Server could not be started");
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _server.StopAsync();
        }
    }
}
