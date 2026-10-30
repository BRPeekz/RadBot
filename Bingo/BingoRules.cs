using System.Collections.Frozen;
using System.Collections.Immutable;

namespace RadBot.Bingo;

/// <summary>Pure rules shared by commands, transactions and reports.</summary>
public static class BingoRules
{
    /// <summary>The single immutable source of truth for all eight winning lines.</summary>
    public static FrozenDictionary<BingoCombination, ImmutableArray<int>> Lines { get; } =
        new Dictionary<BingoCombination, ImmutableArray<int>>
        {
            [BingoCombination.RowTop] = [1, 2, 3], [BingoCombination.RowMiddle] = [4, 5, 6],
            [BingoCombination.RowBottom] = [7, 8, 9], [BingoCombination.ColumnLeft] = [1, 4, 7],
            [BingoCombination.ColumnMiddle] = [2, 5, 8], [BingoCombination.ColumnRight] = [3, 6, 9],
            [BingoCombination.DiagonalDown] = [1, 5, 9], [BingoCombination.DiagonalUp] = [3, 5, 7]
        }.ToFrozenDictionary();

    /// <summary>Stable human-readable label; not used as the internal line identity.</summary>
    public static string Label(BingoCombination combination) => string.Concat(Lines[combination]);

    /// <summary>Complete lines whose global prizes have not been claimed.</summary>
    public static IReadOnlyList<BingoPrize> Claimable(BingoEventState state, BingoPlayerState player) =>
        player.HasWon || state.Status != BingoEventStatus.Active ? [] : state.Prizes
            .Where(p => p.Claim is null && Lines[p.Combination].All(player.UniqueNumbers.Contains)).ToArray();

    /// <summary>Pending state is derived, so losing a line to another player immediately unblocks rolls.</summary>
    public static bool PendingClaim(BingoEventState state, BingoPlayerState player) => Claimable(state, player).Count > 0;

    /// <summary>Determines eligibility independent of the current window's consumed attempt.</summary>
    public static bool IsEligible(BingoEventState state, BingoPlayerState player) =>
        state.Status == BingoEventStatus.Active && !player.HasWon && !PendingClaim(state, player);
}

/// <summary>Expected validation failure safe to display privately.</summary>
public sealed class BingoException(string message) : Exception(message);

/// <summary>Injectable uniform Bingo number source.</summary>
public interface IBingoRandom
{
    /// <summary>Returns a value uniformly distributed from one through nine.</summary>
    int Next();
}

/// <summary>Thread-safe cryptographic uniform number source.</summary>
public sealed class BingoRandom : IBingoRandom
{
    /// <inheritdoc />
    public int Next() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 10);
}
