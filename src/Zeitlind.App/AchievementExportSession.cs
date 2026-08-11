using Zeitlind.App.Games;
using Zeitlind.App.Infrastructure;
using Zeitlind.Core.Achievements;
using Zeitlind.Protocol.Metadata;

namespace Zeitlind.App;

internal static class AchievementExportSession
{
    private static readonly TimeSpan UidWaitAfterSnapshot = TimeSpan.FromSeconds(30);

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
            ApplicationLog.WriteInfo(
                $"快照获取完成：{module.Descriptor.DisplayName}，UID {captured.Uid}，"
                    + $"识别 {snapshot.Records.Count} 条成就记录，其中已完成 {completedCount} 条；"
                    + $"元数据命中 {snapshot.CatalogMatchCount} 条，未知 ID {snapshot.UnknownIdCount} 条"
            );

            ApplicationLog.WriteInfo($"正在关闭本次由 Zeitlind 启动的{module.Descriptor.DisplayName}...");
            try
            {
                game.Terminate(0);
                ApplicationLog.WriteInfo("游戏已关闭");
            }
            catch (Exception exception)
            {
                ApplicationLog.WriteWarningException("快照已取得，但主动关闭游戏失败", exception);
                ApplicationLog.WriteWarning("仍可继续导出；Zeitlind 退出时会再次尝试关闭游戏");
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
        uint currentUid = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationTokenSource? uidWaitCancellation = null;
            var readCancellationToken = cancellationToken;
            if (pendingSnapshot is not null && currentUid == 0)
            {
                var remaining = uidDeadline!.Value - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw MissingUid(adapter);
                }

                uidWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                uidWaitCancellation.CancelAfter(remaining);
                readCancellationToken = uidWaitCancellation.Token;
            }

            HookMessage message;
            try
            {
                var read = pipe.ReadMessageAsync(readCancellationToken);
                if (await Task.WhenAny(read, gameExit) == gameExit)
                {
                    var missing = pendingSnapshot is null ? "完整成就快照" : "当前游戏 UID";
                    throw new InvalidOperationException(
                        $"游戏在取得{missing}前退出；已检查 {packetCount} 个明文包；{adapter.FormatDiagnostics()}"
                    );
                }

                message = await read;
            }
            catch (OperationCanceledException)
                when (pendingSnapshot is not null && !cancellationToken.IsCancellationRequested)
            {
                throw MissingUid(adapter);
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidOperationException(
                    $"游戏内 Hook 提前关闭通信通道；已检查 {packetCount} 个明文包；{adapter.FormatDiagnostics()}",
                    exception
                );
            }
            finally
            {
                uidWaitCancellation?.Dispose();
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

                case HookUidMessage uid:
                    if (!ready)
                    {
                        throw new InvalidDataException("Hook 在就绪确认前发送了 UID");
                    }

                    if (uid.Uid != currentUid)
                    {
                        currentUid = uid.Uid;
                        if (currentUid != 0)
                        {
                            ApplicationLog.WriteInfo($"已读取当前游戏 UID：{currentUid}");
                        }
                    }

                    if (pendingSnapshot is not null && currentUid != 0)
                    {
                        return new CapturedAchievementSnapshot(pendingSnapshot, currentUid);
                    }

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

                    if (packet.Uid != 0 && packet.Uid != currentUid)
                    {
                        currentUid = packet.Uid;
                        ApplicationLog.WriteInfo($"已读取当前游戏 UID：{currentUid}");
                    }

                    if (
                        adapter.TryDecodeIdentity(packet.Packet, out var decodedUid, out var detail)
                        && decodedUid != 0
                        && decodedUid != currentUid
                    )
                    {
                        currentUid = decodedUid;
                        ApplicationLog.WriteInfo($"已从登录响应取得当前游戏 UID：{currentUid}");
                        ApplicationLog.WriteDebug($"UID 识别详情：{detail}", writeToConsole: true);
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
                        if (currentUid != 0)
                        {
                            return new CapturedAchievementSnapshot(snapshot, currentUid);
                        }

                        uidDeadline = DateTimeOffset.UtcNow + UidWaitAfterSnapshot;
                        ApplicationLog.WriteInfo("成就快照已取得；UID 尚未确认，继续等待最多 30 秒...");
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

    private static InvalidOperationException MissingUid(IGameCaptureAdapter adapter)
    {
        return new InvalidOperationException(
            $"已经取得完整成就快照，但当前游戏 UID 在 30 秒内仍未确认；{adapter.FormatDiagnostics()}"
        );
    }
}

internal sealed record CapturedAchievementSnapshot(AchievementSnapshot Snapshot, uint Uid);
