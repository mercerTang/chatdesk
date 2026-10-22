using ChatServer.Services;

namespace ChatServer.Services;

/// <summary>定时执行数据保留清理：启动后先跑一次，之后每 6 小时一次。</summary>
public class RetentionHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RetentionHostedService> _logger;

    public RetentionHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<RetentionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = _configuration.GetValue("Retention:Days", RetentionService.DefaultRetentionDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
                await retention.PurgeExpiredAsync(DateTimeOffset.UtcNow, retentionDays, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "过期数据清理失败");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
