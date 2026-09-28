using Zeitlind.Core.Games;

namespace Zeitlind.App.Games;

internal sealed record GameDescriptor(
    GameKind Kind,
    string Id,
    string DisplayName,
    string ExecutableName,
    string ProcessName,
    string RegistryPath,
    string HookResourceName,
    string HookEntryPoint
);
