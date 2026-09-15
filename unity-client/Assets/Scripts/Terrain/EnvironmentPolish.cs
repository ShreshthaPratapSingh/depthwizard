// =============================================================================
// EnvironmentPolish.cs
//
// Runtime visual polish applied once after terrain generation in SampleScene.
// Adds missing post-processing overrides (Color Adjustments) to the existing
// Global Volume and adjusts the Directional Light color temperature for a
// warmer golden-hour feel.
//
// Does NOT modify any existing scripts — purely additive.
// Does NOT touch RuntimeTerrainBuilder, BackendClient, or DroneController.
//
// Namespace: DepthWizard.Terrain
// =============================================================================

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DepthWizard.Terrain
{
    /// <summary>
    /// One-shot environment polish applied after terrain generation.
    /// Adds Color Adjustments to the Global Volume and warms the sun.
    /// </summary>
    public static class EnvironmentPolish
    {
        private static bool _applied;

        /// <summary>
        /// Apply visual polish. Safe to call multiple times — only runs once.
        /// </summary>
        public static void Apply()
        {
            if (_applied) return;
            _applied = true;

            ApplyColorAdjustments();
            WarmDirectionalLight();

            Debug.Log("[EnvironmentPolish] Visual polish applied.");
        }

        /// <summary>
        /// Reset the applied flag (e.g. when re-entering the scene).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _applied = false;
        }

        // -----------------------------------------------------------------
        // Color Adjustments — subtle saturation and contrast boost
        // -----------------------------------------------------------------

        private static void ApplyColorAdjustments()
        {
            // Find the existing Global Volume in the scene
            var volumes = Object.FindObjectsByType<Volume>(FindObjectsSortMode.None);
            Volume globalVolume = null;

            foreach (var v in volumes)
            {
                if (v.isGlobal && v.profile != null)
                {
                    globalVolume = v;
                    break;
                }
            }

            if (globalVolume == null)
            {
                Debug.LogWarning("[EnvironmentPolish] No Global Volume found — skipping Color Adjustments.");
                return;
            }

            VolumeProfile profile = globalVolume.profile;

            // Check if Color Adjustments already exist
            if (profile.Has<ColorAdjustments>())
            {
                Debug.Log("[EnvironmentPolish] Color Adjustments already present — skipping.");
                return;
            }

            // Add Color Adjustments override
            var colorAdj = profile.Add<ColorAdjustments>(overrides: true);

            // Subtle saturation boost — enhances terrain texture colors
            colorAdj.saturation.overrideState = true;
            colorAdj.saturation.value = 10f; // +10 (range -100 to +100)

            // Slight contrast boost — improves terrain depth definition
            colorAdj.contrast.overrideState = true;
            colorAdj.contrast.value = 8f; // +8 (range -100 to +100)

            // Very subtle exposure lift — brightens slightly
            colorAdj.postExposure.overrideState = true;
            colorAdj.postExposure.value = 0.15f;

            Debug.Log("[EnvironmentPolish] Color Adjustments added: saturation=+10, contrast=+8, exposure=+0.15");
        }

        // -----------------------------------------------------------------
        // Directional Light — warmer color temperature for golden-hour feel
        // -----------------------------------------------------------------

        private static void WarmDirectionalLight()
        {
            // Find the Directional Light
            var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            Light sun = null;

            foreach (var l in lights)
            {
                if (l.type == LightType.Directional)
                {
                    sun = l;
                    break;
                }
            }

            if (sun == null)
            {
                Debug.LogWarning("[EnvironmentPolish] No Directional Light found — skipping warm tint.");
                return;
            }

            // Current: 5000K (neutral daylight)
            // Target:  4500K (slightly warmer, golden-hour feel)
            float currentTemp = sun.colorTemperature;
            if (currentTemp > 4600f) // only adjust if still at default-ish
            {
                sun.useColorTemperature = true;
                sun.colorTemperature = 4500f;
                Debug.Log($"[EnvironmentPolish] Directional Light temperature: {currentTemp}K → 4500K (warmer).");
            }
            else
            {
                Debug.Log($"[EnvironmentPolish] Directional Light already warm ({currentTemp}K) — no change.");
            }
        }
    }
}
