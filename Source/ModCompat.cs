using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // Other mods that draw the same thing we do. We only LOOK (is it loaded) and tell the player; we never touch another mod's code, settings
    // or save data - each of them has its own switches, and the player decides.
    public static class ModCompat
    {
        private static int gbx = -1;

        // GroundBlastFx (Warnix, MIT) draws launch-pad steam, dust, water spray and scorch marks. Its own setting for the steam is "Pad steam"
        // in its window (enablePadSteam in GroundBlastFx/PluginData/Settings.cfg).
        public static bool GroundBlastFxPresent
        {
            get
            {
                if (gbx < 0)
                {
                    gbx = 0;
                    foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                        if (la != null && la.name != null && la.name.StartsWith("GroundBlastFx", System.StringComparison.OrdinalIgnoreCase)) { gbx = 1; break; }
                    if (gbx == 1)
                        Debug.Log("[PlumeTrails] GroundBlastFx is installed: both mods draw launch-pad steam. In GroundBlastFx turn off \"Pad steam\" "
                            + "(enablePadSteam) or switch off \"GPU pad cloud\" in our Effects tab, so that only one of them draws it. Nothing of GroundBlastFx is changed by this mod.");
                }
                return gbx == 1;
            }
        }
    }
}
