using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using LootGoblin.Models;

namespace LootGoblin.Services;

public class StateManager : IDisposable
{
    private readonly Plugin _plugin;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    public BotState State { get; private set; } = BotState.Idle;
    public string StateDetail { get; private set; } = "";
    public bool IsPaused { get; private set; }
    public int RetryCount { get; private set; }
    public uint SelectedMapItemId { get; private set; }
    public MapLocation? CurrentLocation { get; private set; }

    private DateTime stateStartTime = DateTime.Now;
    private DateTime lastTickTime = DateTime.MinValue;
    private bool stateActionIssued;
    private const double TickIntervalSeconds = 0.5;

    private static readonly Dictionary<BotState, double> StateTimeouts = new()
    {
        { BotState.OpeningMap,        15  },
        { BotState.DetectingLocation, 30  },
        { BotState.Teleporting,       90  },
        { BotState.Mounting,          30  },
        { BotState.WaitingForParty,   120 },
        { BotState.Flying,            300 },
        { BotState.OpeningChest,      15  },
    };

    public StateManager(Plugin plugin, IFramework framework, IPluginLog log)
    {
        _plugin = plugin;
        _framework = framework;
        _log = log;
        _framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_plugin.Configuration.Enabled) return;
        if (IsPaused) return;
        if (State == BotState.Idle || State == BotState.Error) return;

        var now = DateTime.Now;
        if ((now - lastTickTime).TotalSeconds < TickIntervalSeconds) return;
        lastTickTime = now;

        if (!Plugin.ClientState.IsLoggedIn)
        {
            TransitionTo(BotState.Error, "Lost connection - not logged in.");
            return;
        }

        CheckStateTimeout();
        Tick();
    }

    private void CheckStateTimeout()
    {
        if (!StateTimeouts.TryGetValue(State, out var timeout)) return;
        if ((DateTime.Now - stateStartTime).TotalSeconds > timeout)
            HandleError($"Timeout in state {State} after {timeout}s.");
    }

    private void Tick()
    {
        switch (State)
        {
            case BotState.SelectingMap:     TickSelectingMap();     break;
            case BotState.OpeningMap:       TickOpeningMap();       break;
            case BotState.DetectingLocation: TickDetectingLocation(); break;
            case BotState.Teleporting:      TickTeleporting();      break;
            case BotState.Mounting:         TickMounting();         break;
            case BotState.WaitingForParty:  TickWaitingForParty();  break;
            case BotState.Flying:           TickFlying();           break;
            case BotState.OpeningChest:     TickOpeningChest();     break;
            case BotState.InCombat:         TickInCombat();         break;
            case BotState.InDungeon:        TickInDungeon();        break;
            case BotState.Completed:        TickCompleted();        break;
        }
    }

    public void Start()
    {
        if (State != BotState.Idle)
        {
            _plugin.AddDebugLog("Cannot start: bot is not idle.");
            return;
        }

        if (!Plugin.ClientState.IsLoggedIn)
        {
            _plugin.AddDebugLog("Cannot start: not logged in.");
            return;
        }

        RetryCount = 0;
        CurrentLocation = null;
        SelectedMapItemId = 0;
        TransitionTo(BotState.SelectingMap, "Starting map run...");
    }

    public void Stop()
    {
        _plugin.NavigationService.StopNavigation();
        IsPaused = false;
        TransitionTo(BotState.Idle, "Stopped by user.");
    }

    public void Pause()
    {
        if (State == BotState.Idle || State == BotState.Error) return;
        IsPaused = true;
        _plugin.NavigationService.StopNavigation();
        _plugin.AddDebugLog("Bot paused.");
    }

    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;
        stateActionIssued = false;
        _plugin.AddDebugLog("Bot resumed.");
    }

    // ─── State Ticks ─────────────────────────────────────────────────────────

    private void TickSelectingMap()
    {
        var maps = _plugin.InventoryService.ScanForMaps();
        if (maps.Count == 0)
        {
            HandleError("No maps found in inventory.");
            return;
        }

        // Pick the highest item ID present (roughly highest tier)
        uint bestId = 0;
        foreach (var kvp in maps)
        {
            if (kvp.Key > bestId) bestId = kvp.Key;
        }

        SelectedMapItemId = bestId;
        _plugin.AddDebugLog($"Selected map item ID {bestId}.");
        TransitionTo(BotState.OpeningMap, $"Opening map ID {bestId}...");
    }

    private void TickOpeningMap()
    {
        if (!stateActionIssued)
        {
            // TODO Phase 6: Use item via game interaction
            // For now: send /item use command placeholder
            _plugin.AddDebugLog($"[Stub] Opening map {SelectedMapItemId} - Phase 6 will implement actual map use.");
            stateActionIssued = true;
        }

        // Transition after a short delay (stub until Phase 6)
        if ((DateTime.Now - stateStartTime).TotalSeconds > 3)
            TransitionTo(BotState.DetectingLocation, "Waiting for map location...");
    }

    private void TickDetectingLocation()
    {
        if (!_plugin.GlobeTrotterIPC.IsAvailable)
        {
            HandleError("GlobeTrotter not available. Install GlobeTrotter to detect map locations.");
            return;
        }

        // TODO Phase 6: Poll GlobeTrotter IPC for map coordinates
        // Stub: will be replaced when GlobeTrotter IPC methods are wired up
        if ((DateTime.Now - stateStartTime).TotalSeconds > 5)
        {
            HandleError("Location detection is a Phase 6 stub - GlobeTrotter IPC not yet wired.");
        }
    }

    private void TickTeleporting()
    {
        var nav = _plugin.NavigationService;

        if (!stateActionIssued)
        {
            if (CurrentLocation == null || CurrentLocation.NearestAetheryteId == 0)
            {
                HandleError("No aetheryte ID for teleport.");
                return;
            }
            nav.TeleportToAetheryte(CurrentLocation.NearestAetheryteId);
            stateActionIssued = true;
            return;
        }

        // Check if teleport finished (no longer between areas and in correct territory)
        if (!nav.IsTeleporting())
        {
            var currentTerritory = Plugin.ClientState.TerritoryType;
            if (CurrentLocation != null && currentTerritory == CurrentLocation.TerritoryId)
            {
                TransitionTo(BotState.Mounting, "Arrived! Mounting up...");
            }
            else if ((DateTime.Now - stateStartTime).TotalSeconds > 10)
            {
                HandleError($"Wrong territory after teleport: {currentTerritory} (expected {CurrentLocation?.TerritoryId}).");
            }
        }
    }

    private void TickMounting()
    {
        var nav = _plugin.NavigationService;

        if (nav.IsMounted())
        {
            var partySize = Plugin.PartyList.Length;
            if (partySize > 0 && _plugin.Configuration.WaitForParty)
                TransitionTo(BotState.WaitingForParty, "Waiting for party to mount...");
            else
                TransitionTo(BotState.Flying, "Mounted! Flying to location...");
            return;
        }

        if (!stateActionIssued)
        {
            nav.MountUp();
            stateActionIssued = true;
        }
    }

    private void TickWaitingForParty()
    {
        _plugin.PartyService.UpdatePartyStatus();

        if (_plugin.PartyService.AllMembersMounted)
        {
            TransitionTo(BotState.Flying, "All party members mounted! Flying...");
            return;
        }

        var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        var timeout = _plugin.Configuration.PartyWaitTimeout;
        var remaining = timeout - (int)elapsed;

        if ((int)elapsed % 10 == 0 && (int)elapsed > 0)
        {
            var mounted = _plugin.PartyService.PartyMembers.FindAll(m => m.IsMounted).Count;
            var total = _plugin.PartyService.PartyMembers.Count;
            StateDetail = $"Waiting for party ({mounted}/{total} mounted) - {remaining}s left...";
        }
    }

    private void TickFlying()
    {
        if (CurrentLocation == null)
        {
            HandleError("No location data for navigation.");
            return;
        }

        var nav = _plugin.NavigationService;

        if (!stateActionIssued)
        {
            var target = new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z);
            nav.FlyToPosition(target);
            stateActionIssued = true;
            return;
        }

        if (nav.State == NavigationState.Error)
        {
            HandleError($"Navigation error: {nav.StateDetail}");
            return;
        }

        if (nav.State == NavigationState.Arrived || nav.State == NavigationState.Idle)
        {
            TransitionTo(BotState.OpeningChest, "Arrived! Looking for treasure coffer...");
        }
    }

    private void TickOpeningChest()
    {
        // TODO Phase 6: Detect and interact with treasure coffer
        if ((DateTime.Now - stateStartTime).TotalSeconds > 5)
        {
            HandleError("Chest interaction is a Phase 6 stub - not yet implemented.");
        }
    }

    private void TickInCombat()
    {
        // TODO Phase 7: Combat handling
        if (!_plugin.NavigationService.IsInCombat())
        {
            TransitionTo(BotState.OpeningChest, "Combat ended. Returning to chest...");
        }
    }

    private void TickInDungeon()
    {
        // TODO Phase 8: Dungeon handling
        _plugin.AddDebugLog("[Stub] In dungeon - Phase 8 will handle this.");
        TransitionTo(BotState.Completed, "Dungeon handling not yet implemented.");
    }

    private void TickCompleted()
    {
        _plugin.AddDebugLog("Map run complete.");
        KrangleService.ClearCache();

        if (_plugin.Configuration.AutoStartNextMap)
        {
            var maps = _plugin.InventoryService.ScanForMaps();
            if (maps.Count > 0)
            {
                RetryCount = 0;
                CurrentLocation = null;
                TransitionTo(BotState.SelectingMap, "Auto-starting next map...");
                return;
            }

            _plugin.AddDebugLog("No more maps in inventory.");
        }

        TransitionTo(BotState.Idle, "Run complete.");
    }

    // ─── Error Handling ───────────────────────────────────────────────────────

    private void HandleError(string message)
    {
        _plugin.AddDebugLog($"[Error] {message}");

        var maxRetries = _plugin.Configuration.MaxRetries;
        if (maxRetries > 0 && RetryCount < maxRetries)
        {
            RetryCount++;
            _plugin.AddDebugLog($"Retrying ({RetryCount}/{maxRetries})...");
            _plugin.NavigationService.StopNavigation();
            TransitionTo(BotState.SelectingMap, $"Retry {RetryCount}/{maxRetries}: {message}");
        }
        else
        {
            _plugin.NavigationService.StopNavigation();
            TransitionTo(BotState.Error, message);
        }
    }

    // ─── Transition ───────────────────────────────────────────────────────────

    private void TransitionTo(BotState newState, string detail)
    {
        var prev = State;
        State = newState;
        StateDetail = detail;
        stateStartTime = DateTime.Now;
        stateActionIssued = false;

        if (_plugin.Configuration.EnableStateLogging)
            _plugin.AddDebugLog($"[State] {prev} → {newState} | {detail}");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    public void SetLocation(MapLocation location)
    {
        CurrentLocation = location;
        _plugin.AddDebugLog($"Location set: {location.ZoneName} ({location.X:F1}, {location.Y:F1}, {location.Z:F1})");
    }
}
