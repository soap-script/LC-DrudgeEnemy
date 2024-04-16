using System.Reflection;
using UnityEngine;
using BepInEx;
using LethalLib.Modules;
using BepInEx.Logging;
using System.IO;
using LC_Drudge.Configuration;

namespace LC_Drudge {
    [BepInPlugin(ModGUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency(LethalLib.Plugin.ModGUID)] 
    public class Plugin : BaseUnityPlugin {
        // It is a good idea for our GUID to be more unique than only the plugin name. Notice that it is used in the BepInPlugin attribute.
        // The GUID is also used for the config file name by default.
        public const string ModGUID = "soapsscript." + PluginInfo.PLUGIN_NAME;
        internal static new ManualLogSource Logger;
        internal static PluginConfig DrudgeConfig { get; private set; } = null;
        public static AssetBundle ModAssets;

        private void Awake() {
            Logger = base.Logger;

            DrudgeConfig = new PluginConfig(this);

            // This should be ran before Network Prefabs are registered. 
            InitializeNetworkBehaviours();

            // We load the asset bundle that should be next to our DLL file, with the specified name.
            var bundleName = "drudge-assets";
            ModAssets = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Info.Location), bundleName));
            if (ModAssets == null) {
                Logger.LogError($"Failed to load custom assets.");
                return;
            }

            // We load our assets from our asset bundle.
            var DrudgeEnemy = ModAssets.LoadAsset<EnemyType>("DrudgeEnemy");

            TerminalNode drudgeNode = ScriptableObject.CreateInstance<TerminalNode>();
            drudgeNode.displayText = "DRUDGE\r\n\r\nSigurd's danger level: 50%\r\n\r\nScientific name: Laborius invictus\r\n\r\n" +
                "Theorized to be a distant relative of vir colligerus, the \"coil-head\", Drudges are believed to have once been manufactured " +
                "en masse for the sole purpose of performing constant menial tasks; primarily those involving giving and receiving miscellaneous " +
                "objects to and from their superiors.\r\n\r\nA Drudge's anatomy consists of two angular metallic legs supporting a steel body, which " +
                "is in turn attached to a singular, crane-like appendage used for wielding various degrees of cargo.\r\n\r\nIt should be noted " +
                "that, due to the gradual decay of internal mechanisms which dictate their logic, they have been known to insufficiently distinguish a " +
                "superior from the objects they were created to transport.\r\n\r\nIf approached by a Drudge, do not allow yourself to be mistaken for " +
                "anything other than its master. As long as either one of you has at least one item of interest on their person, their presence should " +
                "prove more beneficial than harmful.";
            drudgeNode.clearPreviousText = true;
            drudgeNode.maxCharactersToType = 2000;
            drudgeNode.creatureName = "Drudge";
            drudgeNode.creatureFileID = 1089;

            TerminalKeyword drudgeKeyword = TerminalUtils.CreateTerminalKeyword("drudge", specialKeywordResult: drudgeNode);
            
            // Network Prefabs need to be registered. See https://docs-multiplayer.unity3d.com/netcode/current/basics/object-spawning/
            // LethalLib registers prefabs on GameNetworkManager.Start.
            NetworkPrefabs.RegisterNetworkPrefab(DrudgeEnemy.enemyPrefab);
			Enemies.RegisterEnemy(DrudgeEnemy, DrudgeConfig.spawnWeight.Value, Levels.LevelTypes.All, Enemies.SpawnType.Default, drudgeNode, drudgeKeyword);
            
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }

        private static void InitializeNetworkBehaviours() {
            // See https://github.com/EvaisaDev/UnityNetcodePatcher?tab=readme-ov-file#preparing-mods-for-patching
            var types = Assembly.GetExecutingAssembly().GetTypes();
            foreach (var type in types)
            {
                var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                foreach (var method in methods)
                {
                    var attributes = method.GetCustomAttributes(typeof(RuntimeInitializeOnLoadMethodAttribute), false);
                    if (attributes.Length > 0)
                    {
                        method.Invoke(null, null);
                    }
                }
            }
        } 
    }
}