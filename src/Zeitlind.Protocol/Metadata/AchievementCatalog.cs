using System.Globalization;
using System.Text.Json;
using Zeitlind.Core.Games;

namespace Zeitlind.Protocol.Metadata;

public sealed record AchievementCatalog
{
    private const string ZzzResourceName = "Zeitlind.Metadata.Zzz.AchievementInfo.json";
    private const string HsrResourceName = "Zeitlind.Metadata.Hsr.AchievementInfo.json";

    public required GameKind Game { get; init; }

    public required IReadOnlySet<uint> Ids { get; init; }

    public required string LatestVersion { get; init; }

    public int Count => Ids.Count;

    public static AchievementCatalog LoadBundled(GameKind game)
    {
        var resourceName = game == GameKind.ZZZ ? ZzzResourceName : HsrResourceName;
        var assembly = typeof(AchievementCatalog).Assembly;
        using var stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"缺少内嵌成就元数据资源：{resourceName}");
        using var document = JsonDocument.Parse(stream);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{game} AchievementInfo.json 的根节点不是对象");
        }

        var ids = new HashSet<uint>();
        Version? latestVersion = null;
        var latestVersionText = "unknown";

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!uint.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                throw new InvalidDataException($"{game} 元数据含非数字成就 ID：{property.Name}");
            }

            if (game == GameKind.HSR)
            {
                ValidateHsrEntry(property, id, ids);
            }
            else if (!ids.Add(id))
            {
                throw new InvalidDataException($"ZZZ 元数据含重复成就 ID：{id}");
            }

            if (
                property.Value.ValueKind != JsonValueKind.Object
                || !property.Value.TryGetProperty("Version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(versionElement.GetString())
            )
            {
                throw new InvalidDataException($"{game} 元数据条目 {id} 缺少有效的 Version");
            }

            var versionText = versionElement.GetString()!.Trim();
            if (!Version.TryParse(versionText, out var version))
            {
                throw new InvalidDataException($"{game} 元数据条目 {id} 含无效版本号 {versionText}");
            }

            if (latestVersion is null || version > latestVersion)
            {
                latestVersion = version;
                latestVersionText = versionText;
            }
        }

        var minimumCount = game == GameKind.HSR ? 1_000 : 100;
        if (ids.Count < minimumCount)
        {
            throw new InvalidDataException($"{game} 内嵌元数据仅有 {ids.Count} 个 ID，疑似不完整");
        }

        return new AchievementCatalog
        {
            Game = game,
            Ids = ids,
            LatestVersion = latestVersionText,
        };
    }

    private static void ValidateHsrEntry(JsonProperty property, uint id, ISet<uint> ids)
    {
        if (id is < 4_000_000 or > 4_999_999 || !ids.Add(id))
        {
            throw new InvalidDataException("HSR 元数据含无效或重复的成就 ID");
        }

        if (
            property.Value.ValueKind != JsonValueKind.Object
            || !property.Value.TryGetProperty("AchievementID", out var idElement)
            || !idElement.TryGetUInt32(out var embeddedId)
            || embeddedId != id
        )
        {
            throw new InvalidDataException($"HSR 元数据条目 {property.Name} 缺少匹配的 AchievementID");
        }
    }
}
