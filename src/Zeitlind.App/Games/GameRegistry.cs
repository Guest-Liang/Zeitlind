namespace Zeitlind.App.Games;

internal static class GameRegistry
{
    public static IReadOnlyList<IGameModule> All { get; } =
    [ZzzCnGameModule.Instance, HsrCnGameModule.Instance, GiCnGameModule.Instance];

    public static IGameModule? ByExecutableName(string fileName)
    {
        return All.SingleOrDefault(module =>
            fileName.Equals(module.Descriptor.ExecutableName, StringComparison.OrdinalIgnoreCase)
        );
    }
}
