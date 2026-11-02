using System.Text.Json;

namespace PerkTree.Core;

/// <summary>One player's slice of the sidecar.</summary>
public sealed class PerkPlayerSave
{
    public ulong NetId { get; init; }

    /// <summary>The character id the tree was built for. A mismatch means this slice belongs to someone else and is ignored.</summary>
    public string CharacterId { get; init; } = "";

    public PerkSaveData State { get; init; } = new();

    /// <summary>Hash of the committed set when saved, kept so co-op can later compare peers' loaded states (see PerkState.CommittedHash).</summary>
    public ulong CommittedHash { get; init; }

    /// <summary>Hash of the whole synced state (committed nodes + counters) when saved. Informational until co-op sync exists.</summary>
    public ulong SyncHash { get; init; }
}

/// <summary>
/// Everything the mod persists for one run, written beside the game's own run save (the game's save schema is fixed and
/// source-generated, so there is nowhere inside it for mod data). Contains ALL players, because every co-op client needs
/// every player's committed perks.
/// </summary>
public sealed class PerkRunSave
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>
    /// The run's start time from the game's own save (SerializableRun.StartTime). A sidecar is only applied to the run it
    /// was written for, so a leftover file from an abandoned run can never leak into a new one.
    /// </summary>
    public long RunStartTime { get; init; }

    public List<PerkPlayerSave> Players { get; init; } = new();

    public static PerkRunSave Capture(long runStartTime, IEnumerable<(ulong NetId, string CharacterId, PerkState State)> players) => new()
    {
        RunStartTime = runStartTime,
        Players = players
            .Select(p => new PerkPlayerSave
            {
                NetId = p.NetId,
                CharacterId = p.CharacterId,
                State = p.State.ToSave(),
                CommittedHash = p.State.CommittedHash(),
                SyncHash = p.State.SyncHash(),
            })
            .OrderBy(p => p.NetId) // stable file contents regardless of enumeration order
            .ToList(),
    };

    /// <summary>The saved slice for this player, or null if the file has none for them (or it was for a different character).</summary>
    public PerkPlayerSave? For(ulong netId, string characterId) =>
        Players.FirstOrDefault(p => p.NetId == netId &&
                                    string.Equals(p.CharacterId, characterId, StringComparison.OrdinalIgnoreCase));

    public string ToJson() => PerkJson.Serialize(this);

    /// <summary>
    /// Parses a sidecar. Never throws: a missing, corrupt or newer-version file just means "no saved perks", because a
    /// bad sidecar must never be able to stop a run from loading.
    /// </summary>
    public static bool TryParse(string? json, out PerkRunSave? save)
    {
        save = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var parsed = PerkJson.Deserialize<PerkRunSave>(json);
            if (parsed is null || parsed.Version != CurrentVersion) return false;
            save = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }
}
