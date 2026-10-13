using Zeitlind.Runtime.Games;

namespace Zeitlind.Runtime;

internal sealed record GameSelection(IGameModule Module, string ExecutablePath, string Version);
