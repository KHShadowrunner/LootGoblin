using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace LootGoblin.Services;

/// <summary>
/// Static unsafe helpers for game state queries and item/object interaction.
/// Patterns adapted from FrenRider's GameHelpers.cs.
/// </summary>
public static class GameHelpers
{
    /// <summary>
    /// Use an item from inventory by item ID.
    /// For treasure maps: finds the item, opens context menu, and triggers "Use" action.
    /// Returns false if player is busy, item not found, or action fails.
    /// </summary>
    public static unsafe bool UseItem(uint itemId)
    {
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return false;
            if (player.IsCasting) return false;

            if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
                Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                Plugin.Condition[ConditionFlag.Occupied33] ||
                Plugin.Condition[ConditionFlag.Occupied39])
                return false;

            var im = InventoryManager.Instance();
            if (im == null)
            {
                Plugin.Log.Warning($"UseItem({itemId}): InventoryManager is null");
                return false;
            }

            // Search player inventory for the item
            var containers = new[] {
                InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4
            };

            foreach (var container in containers)
            {
                var inv = im->GetInventoryContainer(container);
                if (inv == null) continue;

                for (var i = 0; i < inv->Size; i++)
                {
                    var slot = im->GetInventorySlot(container, i);
                    if (slot == null || slot->ItemId != itemId) continue;

                    // Found the item - open context menu and trigger Use
                    var agent = AgentInventoryContext.Instance();
                    if (agent == null)
                    {
                        Plugin.Log.Warning($"UseItem({itemId}): AgentInventoryContext is null");
                        return false;
                    }

                    // Open context menu for this item slot
                    agent->OpenForItemSlot(container, i, 0, 0);
                    
                    // Trigger the "Use" action (event ID 0 = Use)
                    // This simulates right-click → Use on the item
                    var result = agent->UseItem(itemId, container, (uint)i, 0);
                    Plugin.Log.Information($"UseItem({itemId}): container={container}, slot={i}, result={result}");
                    return result >= 0;
                }
            }

            Plugin.Log.Warning($"UseItem({itemId}): Item not found in inventory");
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"UseItem({itemId}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Interact with a targeted game object via TargetSystem.
    /// Sets the Dalamud target first, then calls TargetSystem.InteractWithObject.
    /// </summary>
    public static unsafe bool InteractWithObject(IGameObject obj)
    {
        try
        {
            Plugin.TargetManager.Target = obj;

            var ts = TargetSystem.Instance();
            if (ts == null) return false;

            var gameObjPtr = (GameObject*)obj.Address;
            if (gameObjPtr == null) return false;

            ts->InteractWithObject(gameObjPtr, true);
            Plugin.Log.Information($"InteractWithObject: {obj.Name.TextValue} at {obj.Position}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"InteractWithObject failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if the player is available (logged in, not casting, not occupied, not in combat).
    /// </summary>
    public static bool IsPlayerAvailable()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return false;
        if (player.IsCasting) return false;
        if (Plugin.Condition[ConditionFlag.InCombat]) return false;
        if (Plugin.Condition[ConditionFlag.Casting]) return false;
        if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent]) return false;
        if (Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]) return false;
        return true;
    }
}
