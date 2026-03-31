#nullable enable

using System;
using System.IO;
using Vintagestory.API.Common;

namespace SubtleParticlesPlus
{
    /// <summary>
    /// Ensures Particles Plus runtime config is present in ModConfig by copying the resolved
    /// particlesplus:config/particlesplus.json asset into ModConfig on the client.
    ///
    /// Put your tuned file at assets/particlesplus/config/particlesplus.json in this mod.
    /// </summary>
    public class SubtleParticlesPlusModSystem : ModSystem
    {
        private const string TargetConfigFileName = "particlesplus.json";

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            try
            {
                ApplyParticlesConfig(api);
            }
            catch (Exception ex)
            {
                api.Logger.Error("[SubtleParticlesPlus] Failed applying particlesplus config: {0}", ex);
            }
        }

        private static void ApplyParticlesConfig(ICoreClientAPI api)
        {
            var assetLoc = new AssetLocation("particlesplus:config/particlesplus.json");
            IAsset? sourceAsset = api.Assets.TryGet(assetLoc);

            if (sourceAsset == null)
            {
                api.Logger.Warning("[SubtleParticlesPlus] Could not find asset {0}. Skipping config apply.", assetLoc);
                return;
            }

            string? jsonText = sourceAsset.ToText();
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                api.Logger.Warning("[SubtleParticlesPlus] Asset {0} was empty. Skipping config apply.", assetLoc);
                return;
            }

            string modConfigDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VintagestoryData",
                "ModConfig"
            );

            Directory.CreateDirectory(modConfigDir);
            string modConfigFile = Path.Combine(modConfigDir, TargetConfigFileName);

            // Backup any existing user config once per run before overwrite.
            if (File.Exists(modConfigFile))
            {
                string backupPath = modConfigFile + ".bak";
                File.Copy(modConfigFile, backupPath, overwrite: true);
            }

            File.WriteAllText(modConfigFile, jsonText);
            api.Logger.Notification("[SubtleParticlesPlus] Applied config to {0}", modConfigFile);
        }
    }
}
