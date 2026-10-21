using Zeitlind.Runtime.Games;

namespace Zeitlind.Runtime.Infrastructure;

internal enum RegistryGameState
{
    Detected,
    NotDetected,
    InvalidPath,
    ReadFailed,
}

internal sealed record RegistryGameStatus(
    IGameModule Module,
    RegistryGameState State,
    string? ExecutablePath,
    string? Error
)
{
    public bool IsDetected => State == RegistryGameState.Detected;
    public string Description =>
        State switch
        {
            RegistryGameState.Detected => $"已检测 {ExecutablePath}",
            RegistryGameState.NotDetected => Error ?? "未检测到安装",
            RegistryGameState.InvalidPath => $"路径无效：{Error}",
            RegistryGameState.ReadFailed => $"读取失败：{Error}",
            _ => throw new InvalidOperationException("未知注册表检测状态"),
        };
    public string MenuText => $"{Module.Descriptor.DisplayName}（注册表：{Description}）";
}
