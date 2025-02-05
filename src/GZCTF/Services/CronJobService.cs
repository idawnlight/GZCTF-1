﻿using System.Threading.Channels;
using GZCTF.Repositories;
using GZCTF.Repositories.Interface;
using GZCTF.Services.Cache;
using GZCTF.Services.Interface;
using Microsoft.Extensions.Caching.Distributed;

namespace GZCTF.Services;

public class CronJobService(IServiceScopeFactory provider, ILogger<CronJobService> logger) : IHostedService, IDisposable
{
    Timer? _timer;

    public void Dispose()
    {
        _timer?.Dispose();
        GC.SuppressFinalize(this);
    }

    public Task StartAsync(CancellationToken token)
    {
        _timer = new Timer(Execute, null, TimeSpan.Zero, TimeSpan.FromMinutes(3));
        logger.SystemLog("定时任务已启动", TaskStatus.Success, LogLevel.Debug);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token)
    {
        _timer?.Change(Timeout.Infinite, 0);
        logger.SystemLog("定时任务已停止", TaskStatus.Exit, LogLevel.Debug);
        return Task.CompletedTask;
    }

    async Task ContainerChecker(AsyncServiceScope scope)
    {
        var containerRepo = scope.ServiceProvider.GetRequiredService<IContainerRepository>();
        var containerService = scope.ServiceProvider.GetRequiredService<IContainerManager>();

        foreach (Models.Data.Container container in await containerRepo.GetDyingContainers())
        {
            await containerService.DestroyContainerAsync(container);
            await containerRepo.RemoveContainer(container);
            logger.SystemLog($"移除到期容器 [{container.ContainerId}]", TaskStatus.Success, LogLevel.Debug);
        }
    }

    async Task BootstrapCache(AsyncServiceScope scope)
    {
        var gameRepo = scope.ServiceProvider.GetRequiredService<IGameRepository>();
        var upcoming = await gameRepo.GetUpcomingGames();

        if (upcoming.Length <= 0)
            return;

        var channelWriter = scope.ServiceProvider.GetRequiredService<ChannelWriter<CacheRequest>>();
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();

        foreach (var game in upcoming)
        {
            var key = CacheKey.ScoreBoard(game);
            var value = await cache.GetAsync(key);
            if (value is null)
            {
                await channelWriter.WriteAsync(ScoreboardCacheHandler.MakeCacheRequest(game));
                logger.SystemLog($"比赛 #{key} 即将开始，积分榜缓存已加入缓存队列", TaskStatus.Success, LogLevel.Debug);
            }
        }
    }

    async void Execute(object? state)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        await ContainerChecker(scope);
        await BootstrapCache(scope);
    }
}