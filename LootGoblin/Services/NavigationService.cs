using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace LootGoblin.Services;

public enum NavigationState
{
    Idle,
    Teleporting,
    WaitingForTeleport,
    Mounting,
    Flying,
    Arrived,
    Error,
}

public class NavigationService : IDisposable
{
    private readonly Plugin _plugin;
    private readonly IPluginLog _log;
    private readonly ICondition _condition;
    private readonly IClientState _clientState;
    private readonly IDataManager _dataManager;

    public NavigationState State { get; private set; } = NavigationState.Idle;
    public string StateDetail { get; private set; } = "";
    public Vector3 TargetPosition { get; private set; }
    public uint TargetTerritoryId { get; private set; }

    private DateTime stateStartTime;
    private float timeoutSeconds = 30f;

    public NavigationService(Plugin plugin, ICondition condition, IClientState clientState, IDataManager dataManager, IPluginLog log)
    {
        _plugin = plugin;
        _condition = condition;
        _clientState = clientState;
        _dataManager = dataManager;
        _log = log;
    }

    public void Dispose() { }

    public void TeleportToAetheryte(uint aetheryteId)
    {
        if (!_clientState.IsLoggedIn)
        {
            SetState(NavigationState.Error, "Not logged in.");
            return;
        }

        if (_condition[ConditionFlag.InCombat])
        {
            SetState(NavigationState.Error, "Cannot teleport while in combat.");
            return;
        }

        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
        {
            SetState(NavigationState.Error, "Already between areas.");
            return;
        }

        var aetheryteSheet = _dataManager.GetExcelSheet<Aetheryte>();
        var aetheryte = aetheryteSheet?.GetRow(aetheryteId);
        var name = aetheryte?.PlaceName.ValueNullable?.Name.ToString() ?? $"Aetheryte {aetheryteId}";

        _plugin.AddDebugLog($"Teleporting to {name} (ID: {aetheryteId})...");
        CommandHelper.SendCommand($"/tp {name}");

        SetState(NavigationState.Teleporting, $"Teleporting to {name}...");
    }

    public void FlyToPosition(Vector3 position)
    {
        // Re-check availability in case vnavmesh loaded after LootGoblin startup
        _plugin.VNavIPC.CheckAvailability();
        
        if (!_plugin.VNavIPC.IsAvailable)
        {
            SetState(NavigationState.Error, "vnavmesh not available.");
            return;
        }

        TargetPosition = position;
        _plugin.VNavIPC.FlyTo(position);
        SetState(NavigationState.Flying, $"Flying to {CommandHelper.FormatVector(position)}...");
    }

    public void MoveToPosition(Vector3 position)
    {
        // Re-check availability in case vnavmesh loaded after LootGoblin startup
        _plugin.VNavIPC.CheckAvailability();
        
        if (!_plugin.VNavIPC.IsAvailable)
        {
            SetState(NavigationState.Error, "vnavmesh not available.");
            return;
        }

        TargetPosition = position;
        _plugin.VNavIPC.MoveTo(position);
        SetState(NavigationState.Flying, $"Moving to {CommandHelper.FormatVector(position)}...");
    }

    public void StopNavigation()
    {
        _plugin.VNavIPC.Stop();
        // Clear any active flag to prevent pathing to old flags
        CommandHelper.SendCommand("/vnav clearflag");
        SetState(NavigationState.Idle, "Navigation stopped.");
    }

    public void MountUp()
    {
        if (_condition[ConditionFlag.Mounted])
        {
            _plugin.AddDebugLog("Already mounted.");
            return;
        }

        var selectedMount = _plugin.Configuration.SelectedMount ?? "Company Chocobo";
        var mountCommand = string.IsNullOrEmpty(selectedMount) || selectedMount == "Mount Roulette"
            ? "/mount \"Company Chocobo\""
            : $"/mount \"{selectedMount}\"";
        
        _plugin.AddDebugLog($"Using mount command: {mountCommand}");
        CommandHelper.SendCommand(mountCommand);
        SetState(NavigationState.Mounting, $"Mounting {selectedMount}...");
    }

    public void FlyToFlag()
    {
        // Re-check availability in case vnavmesh loaded after LootGoblin startup
        _plugin.VNavIPC.CheckAvailability();
        
        if (!_plugin.VNavIPC.IsAvailable)
        {
            SetState(NavigationState.Error, "vnavmesh not available.");
            return;
        }

        CommandHelper.SendCommand("/vnav flyflag");
        SetState(NavigationState.Flying, "Flying to map flag...");
        _plugin.AddDebugLog("Flying to flag via vnavmesh.");
    }

    public unsafe uint FindNearestAetheryte(uint territoryId, Vector3 targetPosition = default)
    {
        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null) return 0;

            telepo->UpdateAetheryteList();
            var count = telepo->TeleportList.Count;
            if (count == 0) return 0;

            var aetheryteSheet = _dataManager.GetExcelSheet<Aetheryte>();
            if (aetheryteSheet == null) return 0;

            _plugin.AddDebugLog($"[Aetheryte] Searching territory {territoryId}, target=({targetPosition.X:F1}, {targetPosition.Y:F1}, {targetPosition.Z:F1}), teleport list count={count}");

            // Collect all candidate aetherytes in the target territory
            var candidates = new System.Collections.Generic.List<(uint Id, string Name, uint Cost, Vector3 WorldPos)>();

            for (int i = 0; i < count; i++)
            {
                var entry = telepo->TeleportList[i];
                if (entry.AetheryteId == 0) continue;

                var aetheryte = aetheryteSheet.GetRow(entry.AetheryteId);

                if (aetheryte.Territory.RowId != territoryId) continue;

                var name = aetheryte.PlaceName.ValueNullable?.Name.ToString() ?? $"ID {entry.AetheryteId}";

                // Try to get world position from Level sheet
                var worldPos = Vector3.Zero;
                try
                {
                    int levelCount = 0;
                    foreach (var lvl in aetheryte.Level)
                    {
                        levelCount++;
                        var levelRow = lvl.ValueNullable;
                        if (levelRow != null)
                        {
                            var lx = levelRow.Value.X;
                            var ly = levelRow.Value.Y;
                            var lz = levelRow.Value.Z;
                            _plugin.AddDebugLog($"  [Level] {name}: Level[{levelCount-1}] RowId={lvl.RowId} XYZ=({lx:F1}, {ly:F1}, {lz:F1})");
                            if (lx != 0 || lz != 0) // Take first entry with non-zero coords
                            {
                                worldPos = new Vector3(lx, ly, lz);
                                break;
                            }
                        }
                        else
                        {
                            _plugin.AddDebugLog($"  [Level] {name}: Level[{levelCount-1}] RowId={lvl.RowId} -> null");
                        }
                    }
                    if (levelCount == 0)
                        _plugin.AddDebugLog($"  [Level] {name}: Level collection was EMPTY (0 entries)");
                    if (worldPos == Vector3.Zero && levelCount > 0)
                        _plugin.AddDebugLog($"  [Level] {name}: iterated {levelCount} entries but no valid position found");
                }
                catch (Exception ex)
                {
                    _plugin.AddDebugLog($"  [Level] {name}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                }

                candidates.Add((entry.AetheryteId, name, entry.GilCost, worldPos));
            }

            if (candidates.Count == 0)
            {
                _plugin.AddDebugLog($"[Aetheryte] No unlocked aetheryte found for territory {territoryId}.");
                return 0;
            }

            // Log all candidates
            foreach (var c in candidates)
            {
                var posStr = c.WorldPos != Vector3.Zero ? $"({c.WorldPos.X:F1}, {c.WorldPos.Y:F1}, {c.WorldPos.Z:F1})" : "NO_POS";
                _plugin.AddDebugLog($"  [Candidate] {c.Name} (ID: {c.Id}, Cost: {c.Cost}g, Pos: {posStr})");
            }

            uint bestId;
            string bestName;

            // Pick closest to target if we have positions and a target
            if (targetPosition != default && candidates.Any(c => c.WorldPos != Vector3.Zero))
            {
                var closest = candidates
                    .Where(c => c.WorldPos != Vector3.Zero)
                    .OrderBy(c => {
                        var dx = c.WorldPos.X - targetPosition.X;
                        var dz = c.WorldPos.Z - targetPosition.Z;
                        return dx * dx + dz * dz;
                    })
                    .First();
                bestId = closest.Id;
                bestName = closest.Name;
                var xzDist = Math.Sqrt(Math.Pow(closest.WorldPos.X - targetPosition.X, 2) + Math.Pow(closest.WorldPos.Z - targetPosition.Z, 2));
                _plugin.AddDebugLog($"[Aetheryte] Selected closest: {bestName} (ID: {bestId}, XZ dist: {xzDist:F0}y from flag)");
            }
            else
            {
                // Fallback: cheapest cost
                var cheapest = candidates.OrderBy(c => c.Cost).First();
                bestId = cheapest.Id;
                bestName = cheapest.Name;
                _plugin.AddDebugLog($"[Aetheryte] FALLBACK cheapest: {bestName} (ID: {bestId}, Cost: {cheapest.Cost}g) [no position data available]");
            }

            return bestId;
        }
        catch (Exception ex)
        {
            _log.Error($"Error finding nearest aetheryte: {ex.Message}");
            _plugin.AddDebugLog($"[Aetheryte] FATAL EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    public bool IsTeleporting()
    {
        return _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];
    }

    public bool IsMounted()
    {
        return _condition[ConditionFlag.Mounted];
    }

    public bool IsInCombat()
    {
        return _condition[ConditionFlag.InCombat];
    }

    public bool IsFlying()
    {
        return _condition[ConditionFlag.InFlight] || _condition[ConditionFlag.Diving];
    }

    private void SetState(NavigationState state, string detail)
    {
        State = state;
        StateDetail = detail;
        stateStartTime = DateTime.Now;
        _plugin.AddDebugLog($"Nav state: {state} - {detail}");
    }
}
