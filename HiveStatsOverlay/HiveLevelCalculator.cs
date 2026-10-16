using System.Text.Json;

namespace HiveStatsOverlay;

internal sealed class HiveLevelCalculator
{
    private readonly object gate = new();
    private int maxLevel;
    private List<(int Xp, int Level)> thresholds;

    public HiveLevelCalculator(int maxLevel = 100)
    {
        this.maxLevel = maxLevel;
        thresholds = CreateOfficialFallback(maxLevel);
    }

    public int GetLevel(int xp)
    {
        lock (gate)
        {
            if (thresholds.Count == 0)
                return 1;

            int level = 1;
            foreach ((int requiredXp, int thresholdLevel) in thresholds)
            {
                if (xp < requiredXp)
                    break;
                level = Math.Max(level, thresholdLevel + 1);
            }
            return Math.Clamp(level, 1, maxLevel);
        }
    }

    public bool TryUpdateFromMetadata(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("experienceToLevel", out JsonElement mapping) ||
                mapping.ValueKind != JsonValueKind.Object)
                return false;

            List<(int Xp, int Level)> parsed = [];
            foreach (JsonProperty item in mapping.EnumerateObject())
            {
                if (int.TryParse(item.Name, out int requiredXp) && item.Value.TryGetInt32(out int level))
                    parsed.Add((requiredXp, level));
            }

            parsed.Sort((a, b) => a.Xp.CompareTo(b.Xp));
            if (parsed.Count == 0)
                return false;

            int parsedMaxLevel = document.RootElement.TryGetProperty("maxLevel", out JsonElement maximum) && maximum.TryGetInt32(out int value)
                ? value
                : parsed.Max(item => item.Level) + 1;
            lock (gate)
            {
                thresholds = parsed;
                maxLevel = Math.Max(parsedMaxLevel, 1);
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<(int Xp, int Level)> CreateOfficialFallback(int maxLevel)
    {
        List<(int Xp, int Level)> result = [];
        int xp = 0;
        for (int zeroBasedLevel = 0; zeroBasedLevel < maxLevel; zeroBasedLevel++)
        {
            result.Add((xp, zeroBasedLevel));
            int nextLevel = zeroBasedLevel + 1;
            xp += Math.Min(nextLevel * 150, 7650);
        }
        return result;
    }
}
