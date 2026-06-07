using RadBot.Models;

namespace RadBot.Lottery;

/// <summary>
/// Defines the game logic for a lottery mode (e.g., Standard, BossRaid, Bingo).
/// The service handles lifecycle; the mode handles rules.
/// </summary>
public interface ILotteryMode
{
    string ModeName { get; }

    /// <summary>
    /// Whether this mode announces a winner at the end of the round.
    /// </summary>
    bool DeclaresWinner { get; }

    /// <summary>
    /// Generate a roll value for this mode.
    /// </summary>
    int GenerateRoll();

    /// <summary>
    /// Whether a user is allowed to roll again in this round.
    /// </summary>
    bool CanUserRoll(ulong userId, List<Roll> existingRolls);

    /// <summary>
    /// Build the summary text for the current round state.
    /// </summary>
    string BuildSummaryText(List<Roll> rolls);

    /// <summary>
    /// Determine the result at end of round.
    /// Returns null if no rolls were made.
    /// </summary>
    LotteryResult? DetermineResult(List<Roll> rolls);
}
