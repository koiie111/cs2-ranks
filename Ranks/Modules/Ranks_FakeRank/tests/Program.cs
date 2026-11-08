using Ranks_FakeRank;

var ranks = new Dictionary<int, int> { [1] = 10, [2] = 20, [18] = 180 };
var max = RankProjection.MaxRank(ranks);
Check(max == 18, "maximum key");
Check(RankProjection.Project(0, -1, ranks, max) == -1, "unloaded XP");
Check(RankProjection.Project(0, 99999, ranks, max) == 99999, "XP ceiling");
Check(RankProjection.Project(0, 100000, ranks, max) == 99999, "XP clamp");
Check(RankProjection.Project(1, 0, ranks, max) == 10, "zero rank");
Check(RankProjection.Project(1, -1, ranks, max) == 10, "unloaded rank");
Check(RankProjection.Project(1, 2, ranks, max) == 20, "rank mapping");
Check(RankProjection.Project(1, 100, ranks, max) == 180, "upper rank clamp");
Console.WriteLine("8 FakeRank projection checks passed");

static void Check(bool success, string name)
{
    if (!success) throw new Exception(name);
}
