using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LootGoblin.IPC;

public class GlobeTrotterIPC : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;
    private readonly Plugin _plugin;

    public bool IsAvailable { get; private set; }

    public GlobeTrotterIPC(Plugin plugin, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _plugin = plugin;
        _pluginInterface = pluginInterface;
        _log = log;

        CheckAvailability();
    }

    public void Dispose() { }

    public void CheckAvailability()
    {
        try
        {
            var installedPlugins = _pluginInterface.InstalledPlugins;
            IsAvailable = false;
            
            foreach (var p in installedPlugins)
            {
                if (string.Equals(p.InternalName, "GlobeTrotter", StringComparison.OrdinalIgnoreCase) && p.IsLoaded)
                {
                    IsAvailable = true;
                    _plugin.AddDebugLog($"GlobeTrotter: Available (matched '{p.InternalName}')");
                    break;
                }
            }

            if (!IsAvailable)
            {
                if (_plugin.Configuration.DebugMode)
                    _plugin.AddDebugLog("GlobeTrotter: Not found (looking for 'GlobeTrotter')");
                else
                    _plugin.AddDebugLog("GlobeTrotter: Not found");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Error checking GlobeTrotter: {ex.Message}");
            IsAvailable = false;
        }
    }
}
