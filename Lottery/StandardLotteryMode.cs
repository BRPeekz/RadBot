using RadBot.Models;

namespace RadBot.Lottery;

/// <summary>
/// Standard lottery mode: roll 1-100, one roll per user, highest wins.
/// </summary>
public class StandardLotteryMode : ILotteryMode
{
    private static readonly Random Rng = new();

    public string ModeName => "Standard";

    public bool DeclaresWinner => true;

    public int GenerateRoll() => Rng.Next(1, 101);

    public bool CanUserRoll(ulong userId, List<Roll> existingRolls)
        => !existingRolls.Any(r => r.UserId == userId);

    public string BuildSummaryText(List<Roll> rolls)
    {
        var ordered = rolls.OrderByDescending(r => r.Value).ToList();
        return string.Join("\n", ordered.Select(r => $"<@{r.UserId}> — **{r.Value}**"));
    }

    public LotteryResult? DetermineResult(List<Roll> rolls)
    {
        if (rolls.Count == 0) return null;

        var maxValue = rolls.Max(r => r.Value);
        var winners = rolls.Where(r => r.Value == maxValue).ToList();

        return new LotteryResult { Winners = winners };
    }
}
