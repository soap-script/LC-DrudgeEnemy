using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;

namespace LC_Drudge.Configuration {
    public class PluginConfig
    {
        // For more info on custom configs, see https://lethal.wiki/dev/intermediate/custom-configs

        public ConfigEntry<int> spawnWeight;

        public ConfigEntry<bool> canKillEmptyHanded;

        public ConfigEntry<bool> canCarryTwoHanded;
        public PluginConfig(BaseUnityPlugin plugin)
        {
            spawnWeight = plugin.Config.Bind(
                "Drudge Spawning",
                "Spawn weight",
                20,
                "The spawn chance weight for Drudge, relative to other existing enemies.\n" +
                "Goes up from 0, lower is more rare, 100 and up is very common."
            );

            canKillEmptyHanded = plugin.Config.Bind(
                "Drudge Behaviors",
                "Can Kill Empty Handed Players",
                false,
                "Normally, the Drudge will only attempt to kill players if they have an empty inventory.\n" +
                "This changes that behavior so that the Drudge only looks for an empty hand, and will kill if it or the player isn't holding anything."
            );

            canCarryTwoHanded = plugin.Config.Bind(
                "Drudge Behaviors",
                "Can Carry Two Handed Items",
                true,
                "Allows the Drudge to be able to hold Two Handed Items in it's claw."
            );
            
            ClearUnusedEntries(plugin);
        }

        private void ClearUnusedEntries(BaseUnityPlugin plugin) {
            // Normally, old unused config entries don't get removed, so we do it with this piece of code. Credit to Kittenji.
            PropertyInfo orphanedEntriesProp = plugin.Config.GetType().GetProperty("OrphanedEntries", BindingFlags.NonPublic | BindingFlags.Instance);
            var orphanedEntries = (Dictionary<ConfigDefinition, string>)orphanedEntriesProp.GetValue(plugin.Config, null);
            orphanedEntries.Clear(); // Clear orphaned entries (Unbinded/Abandoned entries)
            plugin.Config.Save(); // Save the config file to save these changes
        }
    }
}