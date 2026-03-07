using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace LootGoblin.Services;

/// <summary>
/// Stores successful dig/portal XYZ locations keyed by territory + flag position.
/// When a future flag is within 10 yalms XZ of a stored entry, the stored real XYZ is used for flying.
/// File is shareable - users can contribute their own data.
/// </summary>
public class MapLocationDatabase
{
    private readonly Plugin _plugin;
    private readonly IPluginLog _log;
    private readonly string _filePath;
    private List<MapLocationEntry> _entries = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MapLocationDatabase(Plugin plugin, IPluginLog log)
    {
        _plugin = plugin;
        _log = log;
        var configDir = Plugin.PluginInterface.GetPluginConfigDirectory();
        _filePath = Path.Combine(configDir, "MapLocations.json");
        Load();
    }

    public IReadOnlyList<MapLocationEntry> Entries => _entries.AsReadOnly();

    /// <summary>
    /// Look up a stored real XYZ for a given territory + flag position.
    /// Returns the stored entry if flag XZ is within 10 yalms, null otherwise.
    /// </summary>
    public MapLocationEntry? FindEntry(uint territoryId, float flagX, float flagZ)
    {
        foreach (var entry in _entries)
        {
            if (entry.TerritoryId != territoryId) continue;
            var dx = entry.FlagX - flagX;
            var dz = entry.FlagZ - flagZ;
            var xzDist = Math.Sqrt(dx * dx + dz * dz);
            if (xzDist <= 10.0)
            {
                _plugin.AddDebugLog($"[MapLocDB] Found stored location: {entry.ZoneName} flag=({entry.FlagX:F1},{entry.FlagZ:F1}) real=({entry.RealX:F1},{entry.RealY:F1},{entry.RealZ:F1}) dist={xzDist:F1}y");
                return entry;
            }
        }
        return null;
    }

    /// <summary>
    /// Record a successful dig/portal location. Only adds if no existing entry within 10 yalms XZ.
    /// </summary>
    public void RecordLocation(uint territoryId, string zoneName, string mapName, float flagX, float flagY, float flagZ, float realX, float realY, float realZ)
    {
        // Check if we already have an entry close enough
        var existing = FindEntry(territoryId, flagX, flagZ);
        if (existing != null)
        {
            _plugin.AddDebugLog($"[MapLocDB] Already have entry for this location, skipping");
            return;
        }

        var entry = new MapLocationEntry
        {
            TerritoryId = territoryId,
            ZoneName = zoneName,
            MapName = mapName,
            FlagX = (float)Math.Round(flagX, 1),
            FlagY = (float)Math.Round(flagY, 1),
            FlagZ = (float)Math.Round(flagZ, 1),
            RealX = (float)Math.Round(realX, 1),
            RealY = (float)Math.Round(realY, 1),
            RealZ = (float)Math.Round(realZ, 1),
            RecordedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        _entries.Add(entry);
        Save();
        _plugin.AddDebugLog($"[MapLocDB] Recorded new location: {zoneName} T{territoryId} flag=({flagX:F1},{flagZ:F1}) real=({realX:F1},{realY:F1},{realZ:F1}) [total entries: {_entries.Count}]");
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _entries = JsonSerializer.Deserialize<List<MapLocationEntry>>(json, JsonOptions) ?? new();
                _plugin.AddDebugLog($"[MapLocDB] Loaded {_entries.Count} entries from {_filePath}");
            }
            else
            {
                _entries = new();
                _plugin.AddDebugLog($"[MapLocDB] No database file found, starting fresh");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load MapLocationDatabase: {ex.Message}");
            _entries = new();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_entries, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save MapLocationDatabase: {ex.Message}");
        }
    }
}

public class MapLocationEntry
{
    public uint TerritoryId { get; set; }
    public string ZoneName { get; set; } = "";
    public string MapName { get; set; } = "";
    public float FlagX { get; set; }
    public float FlagY { get; set; }
    public float FlagZ { get; set; }
    public float RealX { get; set; }
    public float RealY { get; set; }
    public float RealZ { get; set; }
    public string? RecordedAt { get; set; }
}
