# LootGoblin Learning Document

Living document tracking what works, what doesn't, and lessons learned.

---

## Phase 1: Overworld (Map → Dig → Chest → Portal) — ✅ WORKING

### Map Selection & Deciphering
- **Status:** Working
- Uses inventory scan order (not UI sort order) to match `/gaction decipher` menu index
- 1-based indexing for `FireCallback` on `SelectIconString`
- Controller mode (`numpad2` → `numpad0` × 2) for single-map-type fast path

### Flying to Location
- **Status:** Working
- `VNavIPC.FlyTo()` with `CultureInfo.InvariantCulture` formatting
- Stuck detection every 10s: re-pathfind if moved <5y (proven TickFlying pattern)
- Party mount wait before flying

### Digging
- **Status:** Working
- `/gaction dig` after dismount
- Retry dig if combat interrupts before chest spawns

### Overworld Chest Interaction
- **Status:** Working (with stuck detection fix)
- `ChestDetectionService.FindNearestCoffer()` + `lockon+automove`
- `TargetSystem.InteractWithObject()` every 2s
- `ClickYesIfVisible()` for dialogs
- Combat detection: stop automove, clear target, wait for combat end
- **Fix applied:** Stuck detection — if lockon+automove doesn't close 2y in 5s, switches to vnavmesh navigation

### Portal Detection & Interaction
- **Status:** Working
- Portal check runs FIRST every tick (even during combat)
- `TargetSystem.InteractWithObject()` every 1s + `ClickYesIfVisible()`
- `/gaction jump` for underwater portals with Y-axis range issues

---

## Phase 2: Dungeon Entry — ✅ WORKING

### Territory Change Detection
- **Status:** Working
- Territory change handler detects overworld → dungeon transition
- `BoundByDuty` flag confirms dungeon entry
- Objectives reset on new dungeon entry (floor, attempted objects, etc.)

### Object Loading Wait
- **Status:** Working (with fix)
- All dungeon objects start as `Targetable:False` on entry
- Sweep waits up to 30s for objects to become targetable
- **Fix applied:** If progression objects (Arcane Sphere) become targetable first, skip sweep wait

---

## Phase 3: Dungeon Floor Sweep (Coffers/Sacks) — ✅ WORKING

### Object Detection
- **Status:** Working
- `ObjectKind.Treasure` = coffers, sacks (PandorasBox pattern)
- `ObjectKind.EventObj` = arcane spheres, doors, portals
- Always scan BOTH types
- Sweep log throttled to once per 10s

### ProcessLootTarget 3-Phase Approach
- **Status:** Working
- **>6y:** vnavmesh `MoveToPosition()` + stuck detection every 10s
- **3-6y:** Stop vnavmesh, `lockon+automove` + interact every 2s
- **<3y:** Stop ALL movement, interact every 2s
- Multi-method interaction cycling: `TargetSystem.InteractWithObject` (even attempts) + `/interact` command (odd attempts)
- 60s timeout per object (was 15s — too short for dungeon corridors)

---

## Phase 4: Arcane Sphere Interaction — ✅ WORKING

### Detection
- **Status:** Working
- Named `"Arcane Sphere"` as `ObjectKind.EventObj`
- Detected by `GetProgressionObjects()` after sweep is complete

### Interaction
- **Status:** Working
- Same multi-method cycling as ProcessLootTarget
- `ClickYesIfVisible()` for the roulette confirmation dialog
- Stuck detection helped reach the sphere (was getting stuck at 6.3y)

---

## Phase 5: Post-Roulette (After Arcane Sphere) — ✅ WORKING (confirmed in territory 794)

All 5 fixes confirmed working. See `LootGoblinLearningRouletteDungeons.md` for full log analysis.

### Fixes Applied (all confirmed)
1. `CountNearbyUntargetableProgressionObjects()` filters out `attemptedCoffers`/`processedSpheres`
2. `ProcessingSpheres` detects sphere was used → transitions to `DungeonProgressing`
3. `GetProgressionObjects()` expanded to include "door", "gate", "high", "low"
4. `FindDungeonObjects()` includes unnamed targetable EventObj within 30y as potential doors
5. `TickDungeonLooting` checks `BetweenAreas` for floor transitions
6. All timeout/wait failures transition to `DungeonProgressing` instead of `HeadingToExit`

---

## Phase 6: Door Interaction (`TickDungeonProgressing`) — ✅ WORKING (roulette), 🔧 FIXED (canal)

### Logic
- `FindDungeonObjects(lootOnly: false)` finds doors
- Checks for loot first (bonus spawns after combat)
- Approaches door with lockon+automove (<10y) or vnavmesh (>10y)
- Interacts every 1s with `GameHelpers.InteractWithObject()`
- 60s stuck timer per door → try another door
- Detects loading screen → advance floor → `InDungeon` state

### Door Selection
- Pick closest door (per user: "just pick whichever door object is closer")
- If stuck 60s → exclude that door, try next one
- Golden aura door = guaranteed (no detection yet, relies on closest-first)

### Ejection Detection
- `!BoundByDuty` during progression → ejected (wrong door RNG)
- Transitions to `Completed` state

### Canal of Uznair Bug Fix (loot priority)
- **Bug:** Bot beelined to Sluice Gate before looting Treasure Coffer
- **Root cause 1:** `DungeonProgressing → DungeonLooting` transition didn't reset `currentObjective` to `ClearingChests` — sweep never re-ran
- **Root cause 2:** `ProcessingSpheres` didn't check for loot before targeting progression objects
- **Fix 1:** `DungeonProgressing` resets `currentObjective = ClearingChests` when loot found
- **Fix 2:** `ProcessingSpheres` calls `FindDungeonObjects(lootOnly:true)` before targeting progression — if loot exists, goes back to `ClearingChests`
- **Key learning:** Treasure Coffers can spawn AFTER the initial sweep completes (late object table population)

---

## Phase 7: Combat Detection — ✅ WORKING

### Pattern
- `ConditionFlag.InCombat` checked at top of every dungeon tick handler
- Transitions to `DungeonCombat` state, lets BMR handle fighting
- After combat: 2s grace period for despawn, then scan for loot
- `previouslyInCombat` edge detection for single "combat started" log

---

## Key Architecture Decisions

### State Machine Flow
```
Idle → SelectingMap → OpeningMap → DetectingLocation → Mounting → WaitingForParty 
→ Flying → OpeningChest → Completed → InDungeon → DungeonLooting → DungeonCombat 
→ DungeonProgressing → (loop to InDungeon or Completed/Error)
```

### Dungeon Objective Hierarchy
```
ClearingChests (sweep coffers/sacks) → ProcessingSpheres (Arcane Sphere/doors) 
→ DungeonProgressing (door navigation) → InDungeon (next floor)
```

### Important Object Rules
- `ObjectKind.Treasure` = coffers, sacks — interact via `TargetSystem.InteractWithObject()`
- `ObjectKind.EventObj` = spheres, doors, portals — same interaction method works
- Unnamed EventObj can be doors in some territories (e.g. 794)
- Named objects like "Shortcut", scenery are filtered by sweep exclude list
- `IsTargetable` is the key signal for interactability and despawn detection

### Interaction Patterns (Proven)
- **Primary:** `GameHelpers.InteractWithObject()` → `TargetSystem.Instance()->InteractWithObject()`
- **Secondary:** `CommandHelper.SendCommand("/interact")` (game native)
- **Dialog:** `GameHelpers.ClickYesIfVisible()` every tick
- **Approach:** `lockon+automove` for short range, `vnavmesh MoveToPosition` for long range

---

## Known Territories
| Territory ID | Name | Notes |
|---|---|---|
| 612 | The Fringes | Overworld dig location (Seemingly Special maps) |
| 620 | The Peaks | Overworld dig location |
| 712 | Canals of Uznair | Room-based dungeon. Named objects (High, Low, Shortcut, Sluice Gate). Start: <0.02, 149.80, 388.27>. Treasure Coffer spawns late. |
| 794 | Unknown roulette dungeon | ALL objects unnamed except Arcane Sphere. Roulette confirmed 100% working. |

---

## Settings Removed (Dead Code)
- `AllowPillionRiders` — Not a controllable game feature
- `TargetingMethod` enum — ProcessLootTarget always uses TargetSystem + /interact cycling now
- `InteractMethod1_Current/2/3` — Replaced by multi-method cycling
- `GetTargetCommand`, `PostInteractionTracking`, `TriggerControllerModeInteract`, `SendChatCommand` — All dead code
