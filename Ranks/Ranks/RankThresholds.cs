namespace Ranks;

internal sealed class RankThresholds
{
    private readonly (int Required, string Name, int Level)[] _thresholds;

    public RankThresholds(Dictionary<string, int> ranks)
    {
        var level = 0;
        _thresholds = ranks.OrderBy(rank => rank.Value).GroupBy(rank => rank.Value)
            .Select(group =>
            {
                level += group.Count();
                return (group.Key, group.First().Key, level);
            }).ToArray();
    }

    public (string Name, int Level, long NextXp) Find(long experience)
    {
        var start = 0;
        var end = _thresholds.Length;
        while (start < end)
        {
            var middle = start + (end - start) / 2;
            if (_thresholds[middle].Required <= experience)
                start = middle + 1;
            else
                end = middle;
        }

        var nextXp = start == _thresholds.Length ? 0 : _thresholds[start].Required - experience;
        return start == 0
            ? (string.Empty, 0, nextXp)
            : (_thresholds[start - 1].Name, _thresholds[start - 1].Level, nextXp);
    }
}
