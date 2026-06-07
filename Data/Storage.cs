using RadBot.Models;
using System.Text.Json;

namespace RadBot.Data;

public class Storage
{
    private const string InfrastructureFileName = "infra.json";
    private const string RoundFileName = "round.json";

    public static void SaveInfrastructure(Infrastructure infra)
    {
        var json = JsonSerializer.Serialize(infra);
        File.WriteAllText(InfrastructureFileName, json);
    }

    public static void SaveRound(RoundState round)
    {
        var json = JsonSerializer.Serialize(round);
        File.WriteAllText(RoundFileName, json);
    }

    public static Infrastructure LoadInfrastructure()
    {
        if (!File.Exists(InfrastructureFileName)) return new Infrastructure();
        var json = File.ReadAllText(InfrastructureFileName);
        return JsonSerializer.Deserialize<Infrastructure>(json) ?? new Infrastructure();
    }

    public static RoundState LoadRound()
    {
        if (!File.Exists(RoundFileName)) return new RoundState();
        var json = File.ReadAllText(RoundFileName);
        return JsonSerializer.Deserialize<RoundState>(json) ?? new RoundState();
    }

    /// <summary>
    /// Deletes the round.json file and returns a fresh RoundState.
    /// Prevents deserialization conflicts when the schema changes between modes.
    /// </summary>
    public static RoundState ResetRound()
    {
        if (File.Exists(RoundFileName))
            File.Delete(RoundFileName);

        return new RoundState();
    }
}
