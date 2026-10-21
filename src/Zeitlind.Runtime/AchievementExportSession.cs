using System.Diagnostics;
using Zeitlind.Core.Achievements;
using Zeitlind.Protocol.Metadata;
using Zeitlind.Runtime.Games;
using Zeitlind.Runtime.Infrastructure;

namespace Zeitlind.Runtime;

internal static class AchievementExportSession
{
    private static readonly TimeSpan HookConnectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HookReadyTimeout = TimeSpan.FromSeconds(135);
    private static readonly TimeSpan UidWaitAfterSnapshot = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LocalUidPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GracefulGameExitTimeout = TimeSpan.FromSeconds(10);

    public static async Task<CapturedAchievementSnapshot> CaptureAsync(
        GameSelection selection,
        string hookPath,
        AchievementCatalog catalog,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var module = selection.Module;
        progress?.Report(new ExportProgress(ExportStage.Starting, $"正在启动{module.Descriptor.DisplayName}…"));
        var captureAdapter = module.CreateCaptureAdapter(catalog, selection.Version);
        using var game = SuspendedGameProcess.Start(selection.ExecutablePath);
        await using var pipe = new HookPipeServer(game.ProcessId);

        cancellationToken.ThrowIfCancellationRequested();
        game.Resume();
        progress?.Report(new ExportProgress(ExportStage.LoadingHook, "正在准备游戏数据读取…"));
        RemoteHookInjector.Inject(game, hookPath, module.Descriptor.HookEntryPoint, cancellationToken);

        ApplicationLog.WriteInfo($"{module.Descriptor.DisplayName}已启动且 Hook 已加载，正在等待连接");
        progress?.Report(new ExportProgress(ExportStage.ConnectingHook, "正在连接游戏，请稍候…"));

        var captured = await WaitForSnapshotAsync(pipe, game, captureAdapter, progress, cancellationToken);
        var snapshot = captured.Snapshot;
        var completedCount = snapshot.Records.Count(static record => record.IsCompleted);

        var uidDisplay = captured.Uid?.ToString() ?? "null（未确认）";
        ApplicationLog.WriteInfo(
            $"快照获取完成：{module.Descriptor.DisplayName}，UID {uidDisplay}，"
                + $"识别 {snapshot.Records.Count} 条成就记录，其中已完成 {completedCount} 条；"
                + $"元数据命中 {snapshot.CatalogMatchCount} 条，未知 ID {snapshot.UnknownIdCount} 条"
        );

        ApplicationLog.WriteInfo($"正在关闭本次由 Zeitlind 启动的{module.Descriptor.DisplayName}...");
        progress?.Report(new ExportProgress(ExportStage.ClosingGame, "成就已读取，正在关闭本次启动的游戏…"));
        try
        {
            var closedGracefully = await game.TryCloseGracefullyAsync(GracefulGameExitTimeout, cancellationToken);
            if (closedGracefully)
            {
                ApplicationLog.WriteInfo("游戏已正常退出");
            }
            else
            {
                ApplicationLog.WriteWarning("游戏未响应关闭请求，将强制关闭本次游戏进程及其子进程");
                game.Terminate(0);
                ApplicationLog.WriteInfo("游戏已强制关闭");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ApplicationLog.WriteWarningException("游戏未能正常退出，将尝试强制关闭", exception);
            try
            {
                game.Terminate(0);
                ApplicationLog.WriteInfo("游戏已强制关闭");
            }
            catch (Exception terminateException)
            {
                ApplicationLog.WriteWarningException("快照已取得，但强制关闭游戏也失败", terminateException);
                ApplicationLog.WriteWarning("仍可继续导出；Zeitlind 退出时 Job Object 会执行最终清理");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return captured;
    }

    private static async Task<CapturedAchievementSnapshot> WaitForSnapshotAsync(
        HookPipeServer pipe,
        SuspendedGameProcess game,
        IGameCaptureAdapter adapter,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
        connectionCancellation.CancelAfter(HookConnectionTimeout);
        var gameExit = game.WaitForExitAsync(sessionCancellation.Token);
        var connection = pipe.WaitForAuthenticatedConnectionAsync(connectionCancellation.Token);
        Task<HookMessage>? pendingRead = null;

        try
        {
            if (await Task.WhenAny(connection, gameExit) == gameExit)
            {
                await gameExit;
                throw new InvalidOperationException("游戏在 Hook 建立连接前退出");
            }

            try
            {
                await connection;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("等待游戏连接超时，请重试并查看运行日志", exception);
            }

            connectionCancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            ApplicationLog.WriteDebug("游戏内 Hook 已连接命名管道", writeToConsole: false);
            progress?.Report(new ExportProgress(ExportStage.ConnectingHook, "已连接游戏，正在准备成就读取…"));

            var ready = false;
            var hookConnectedAt = Stopwatch.GetTimestamp();
            var packetCount = 0;
            AchievementSnapshot? pendingSnapshot = null;
            DateTimeOffset? uidDeadline = null;
            ulong? currentUid = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (adapter.TryReadIdentity(out var identity) && identity.Uid != 0)
                {
                    UpdateUidFromLocalLog(ref currentUid, identity);
                }

                if (pendingSnapshot is not null && currentUid is not null)
                {
                    return new CapturedAchievementSnapshot(pendingSnapshot, currentUid.Value);
                }

                var pollDelay = LocalUidPollInterval;
                if (!ready)
                {
                    var remaining = HookReadyTimeout - Stopwatch.GetElapsedTime(hookConnectedAt);
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new TimeoutException("游戏已连接，但读取组件未能及时就绪，请查看运行日志");
                    }

                    if (remaining < pollDelay)
                    {
                        pollDelay = remaining;
                    }
                }

                if (pendingSnapshot is not null && currentUid is null)
                {
                    var remaining = uidDeadline!.Value - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        return CompleteWithoutUidOrThrow(pendingSnapshot, adapter);
                    }

                    if (remaining < pollDelay)
                    {
                        pollDelay = remaining;
                    }
                }

                pendingRead ??= pipe.ReadMessageAsync(sessionCancellation.Token);
                var poll = Task.Delay(pollDelay, sessionCancellation.Token);
                var completed = await Task.WhenAny(pendingRead, gameExit, poll);
                if (completed == gameExit)
                {
                    await gameExit;
                    if (pendingSnapshot is not null && adapter.CanExportWithoutConfirmedUid)
                    {
                        return CompleteWithoutUidOrThrow(pendingSnapshot, adapter);
                    }

                    var missing = pendingSnapshot is null ? "完整成就快照" : "当前游戏 UID";
                    throw new InvalidOperationException(
                        $"游戏在取得{missing}前退出；已检查 {packetCount} 个明文包；"
                            + $"{adapter.FormatDiagnostics()}；{adapter.FormatIdentityDiagnostics()}"
                    );
                }

                if (completed == poll)
                {
                    continue;
                }

                HookMessage message;
                try
                {
                    message = await pendingRead;
                    pendingRead = null;
                }
                catch (EndOfStreamException exception)
                {
                    if (pendingSnapshot is not null && adapter.CanExportWithoutConfirmedUid)
                    {
                        return CompleteWithoutUidOrThrow(pendingSnapshot, adapter);
                    }

                    throw new InvalidOperationException(
                        $"游戏内 Hook 提前关闭通信通道；已检查 {packetCount} 个明文包；"
                            + $"{adapter.FormatDiagnostics()}；{adapter.FormatIdentityDiagnostics()}",
                        exception
                    );
                }

                switch (message)
                {
                    case HookReadyMessage hookReady:
                        if (ready)
                        {
                            throw new InvalidDataException("Hook 重复发送了就绪确认");
                        }

                        ready = true;
                        ApplicationLog.WriteInfo("Hook 已就绪");
                        adapter.OnHookReady(hookReady);
                        ApplicationLog.WriteInfo(adapter.StartInstruction);
                        progress?.Report(new ExportProgress(ExportStage.WaitingForLogin, adapter.StartInstruction));
                        break;

                    case HookPacketMessage packet:
                        if (!ready)
                        {
                            throw new InvalidDataException("Hook 在就绪确认前发送了数据包");
                        }

                        packetCount++;
                        adapter.ObservePacket(packet.Packet);
                        if (packetCount == 1)
                        {
                            progress?.Report(
                                new ExportProgress(ExportStage.ReadingAchievements, "已连接游戏，正在等待完整成就数据…")
                            );
                            ApplicationLog.WriteInfo("已收到第一个包");
                            ApplicationLog.WriteDebug(
                                $"第一个包详情：命令 {packet.Packet.CommandId}，包体 {packet.Packet.Body.Length} bytes",
                                writeToConsole: true
                            );
                        }

                        if (
                            pendingSnapshot is null
                            && adapter.TryDecodeSnapshot(packet.Packet, out var snapshot)
                            && snapshot is not null
                        )
                        {
                            pendingSnapshot = snapshot;
                            ApplicationLog.WriteInfo("已确认完整成就快照结构");
                            ApplicationLog.WriteDebug(
                                $"成就记录结构详情：{adapter.FormatSnapshotDetails(snapshot)}",
                                writeToConsole: true
                            );
                            if (currentUid is null)
                            {
                                uidDeadline = DateTimeOffset.UtcNow + UidWaitAfterSnapshot;
                                ApplicationLog.WriteInfo("成就快照已取得；本地日志尚未写入 UID，继续等待最多 30 秒...");
                                progress?.Report(
                                    new ExportProgress(
                                        ExportStage.ConfirmingAccount,
                                        "成就已读取，正在确认当前账号 UID（最多 30 秒）…"
                                    )
                                );
                            }
                        }

                        if (pendingSnapshot is null && packetCount % 100 == 0)
                        {
                            ApplicationLog.WriteDebug(
                                $"已检查 {packetCount} 个包，继续等待完整成就快照；{adapter.FormatDiagnostics()}",
                                writeToConsole: true
                            );
                        }

                        break;

                    case HookErrorMessage error:
                        throw new InvalidOperationException($"游戏内 Hook 报错：{error.Error}");

                    default:
                        throw new InvalidDataException("收到无法识别的 Hook 消息");
                }
            }
        }
        finally
        {
            sessionCancellation.Cancel();
            await ObserveSessionTaskAsync(connection);
            if (pendingRead is not null)
            {
                await ObserveSessionTaskAsync(pendingRead);
            }
            await ObserveSessionTaskAsync(gameExit);
        }
    }

    private static async Task ObserveSessionTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is OperationCanceledException or EndOfStreamException)
        {
            // Cancellation and a closed pipe are expected while the session is ending.
        }
        catch (Exception exception)
        {
            ApplicationLog.WriteWarningException("结束采集后台任务时发生异常", exception);
        }
    }

    private static void UpdateUidFromLocalLog(ref ulong? currentUid, PlayerIdentityEvidence identity)
    {
        if (identity.Uid == 0)
        {
            return;
        }

        if (currentUid is null)
        {
            currentUid = identity.Uid;
            ApplicationLog.WriteInfo($"已从本地日志确认当前游戏 UID：{identity.Uid}");
            ApplicationLog.WriteDebug($"UID 确认详情：{identity.Detail}", writeToConsole: true);
            return;
        }

        if (currentUid.Value != identity.Uid)
        {
            ApplicationLog.WriteWarning($"本地日志显示账号已从 UID {currentUid.Value} 切换为 {identity.Uid}");
            ApplicationLog.WriteDebug($"UID 更新详情：{identity.Detail}", writeToConsole: true);
            currentUid = identity.Uid;
        }
    }

    private static CapturedAchievementSnapshot CompleteWithoutUidOrThrow(
        AchievementSnapshot snapshot,
        IGameCaptureAdapter adapter
    )
    {
        if (!adapter.CanExportWithoutConfirmedUid)
        {
            throw MissingUid(adapter);
        }

        ApplicationLog.WriteWarning("当前游戏 UID 未能通过强证据确认；将保留成就快照，需要 UID 的字段会写为 null");
        ApplicationLog.WriteDebug(
            $"{adapter.FormatDiagnostics()}；{adapter.FormatIdentityDiagnostics()}",
            writeToConsole: true
        );
        return new CapturedAchievementSnapshot(snapshot, null);
    }

    private static InvalidOperationException MissingUid(IGameCaptureAdapter adapter)
    {
        return new InvalidOperationException(
            $"已经取得完整成就快照，但本地日志在 30 秒内仍未写入可确认的当前 UID；"
                + $"{adapter.FormatDiagnostics()}；{adapter.FormatIdentityDiagnostics()}"
        );
    }
}

internal sealed record CapturedAchievementSnapshot(AchievementSnapshot Snapshot, ulong? Uid);
