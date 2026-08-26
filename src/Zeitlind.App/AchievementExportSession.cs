using Zeitlind.App.Games;
using Zeitlind.App.Infrastructure;
using Zeitlind.Core.Achievements;
using Zeitlind.Protocol.Metadata;

namespace Zeitlind.App;

internal static class AchievementExportSession
{
    private static readonly TimeSpan UidWaitAfterSnapshot = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LocalUidPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GracefulGameExitTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(
        GameSelection selection,
        string hookPath,
        AchievementCatalog catalog,
        ExportTarget? configuredTarget,
        string? outputDirectory
    )
    {
        var module = selection.Module;
        var captureAdapter = module.CreateCaptureAdapter(catalog, selection.Version);
        using var game = SuspendedGameProcess.Start(selection.ExecutablePath);
        await using var pipe = new HookPipeServer(game.ProcessId);

        game.Resume();
        RemoteHookInjector.Inject(game, hookPath, module.Descriptor.HookEntryPoint);

        ApplicationLog.WriteInfo(
            $"{module.Descriptor.DisplayName}已启动且 Hook 已加载。{captureAdapter.StartInstruction}"
        );
        ApplicationLog.WriteInfo("按 Ctrl+C 可取消");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var captured = await WaitForSnapshotAsync(pipe, game, captureAdapter, cancellation.Token);
            var snapshot = captured.Snapshot;
            var completedCount = snapshot.Records.Count(static record => record.IsCompleted);

            Console.WriteLine();
            var uidDisplay = captured.Uid?.ToString() ?? "null（未确认）";
            ApplicationLog.WriteInfo(
                $"快照获取完成：{module.Descriptor.DisplayName}，UID {uidDisplay}，"
                    + $"识别 {snapshot.Records.Count} 条成就记录，其中已完成 {completedCount} 条；"
                    + $"元数据命中 {snapshot.CatalogMatchCount} 条，未知 ID {snapshot.UnknownIdCount} 条"
            );

            ApplicationLog.WriteInfo($"正在关闭本次由 Zeitlind 启动的{module.Descriptor.DisplayName}...");
            try
            {
                var closedGracefully = await game.TryCloseGracefullyAsync(GracefulGameExitTimeout);
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

            var target = ExportSelectionFlow.Select(module, configuredTarget, cancellation.Token);
            var output = await AchievementExportWriter.WriteAsync(
                module,
                snapshot,
                captured.Uid,
                catalog,
                target,
                outputDirectory,
                cancellation.Token
            );

            Console.WriteLine();
            ApplicationLog.WriteInfo(module.BuildExportSummary(target, snapshot, catalog));
            ApplicationLog.WriteInfo($"{output.DisplayName}：{output.Path}");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<CapturedAchievementSnapshot> WaitForSnapshotAsync(
        HookPipeServer pipe,
        SuspendedGameProcess game,
        IGameCaptureAdapter adapter,
        CancellationToken cancellationToken
    )
    {
        var gameExit = game.WaitForExitAsync(CancellationToken.None);
        var connection = pipe.WaitForAuthenticatedConnectionAsync(cancellationToken);
        if (await Task.WhenAny(connection, gameExit) == gameExit)
        {
            throw new InvalidOperationException("游戏在 Hook 建立连接前退出");
        }

        await connection;
        ApplicationLog.WriteDebug("游戏内 Hook 已连接命名管道", writeToConsole: false);

        var ready = false;
        var packetCount = 0;
        AchievementSnapshot? pendingSnapshot = null;
        DateTimeOffset? uidDeadline = null;
        ulong? currentUid = null;
        using var pipeReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<HookMessage>? pendingRead = null;

        try
        {
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

                pendingRead ??= pipe.ReadMessageAsync(pipeReadCancellation.Token);
                var poll = Task.Delay(pollDelay, cancellationToken);
                var completed = await Task.WhenAny(pendingRead, gameExit, poll);
                if (completed == gameExit)
                {
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
            pipeReadCancellation.Cancel();
            if (pendingRead is not null)
            {
                try
                {
                    await pendingRead;
                }
                catch (Exception exception) when (exception is OperationCanceledException or EndOfStreamException)
                {
                    // The session is ending and owns this single outstanding pipe read.
                }
            }
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
