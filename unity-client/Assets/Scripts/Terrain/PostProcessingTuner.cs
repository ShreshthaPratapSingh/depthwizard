// =============================================================================
// PostProcessingTuner.cs
//
// Runtime adjustment of post-processing overrides in SampleScene to fix
// visual artifacts (bright white streaks caused by bloom threshold being
// too low relative to terrain material brightness + exposure boost).
//
// Runs once after terrain generation. Does NOT modify any terrain or camera
// logic — purely adjusts Volume profile overrides.
//
// Namespace: DepthWizard.Terrain
// =============================================================================

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DepthWizard.Terrain
{
    /// <summary>
    /// One-shot post-processing tuner that fixes bloom-related white streaks
    /// on terrain ridgelines by raising the bloom threshold and reducing
    /// intensity/scatter at runtime.
    /// </summary>
    public class PostProcessingTuner : MonoBehaviour
    {
        private bool _applied;

        private void Start()
        {
            // Delay slightly to ensure Volume profile is loaded
            StartCoroutine(ApplyDelayed());
        }

        private System.Collections.IEnumerator ApplyDelayed()
        {
            // Wait a couple frames for the scene to fully initialize
            yield return null;
            yield return null;

            Apply();
        }

        private void Apply()
        {
            if (_applied) return;
            _applied = true;

            // Find the global volume in the scene
            var volumes = FindObjectsByType<Volume>(FindObjectsSortMode.None);
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
                Debug.Log("[PostProcessingTuner] No Global Volume found — skipping bloom adjustment.");
                return;
            }

            VolumeProfile profile = globalVolume.profile;

            // --- Bloom adjustments ---
            if (profile.TryGet<Bloom>(out var bloom))
            {
                float oldThreshold = bloom.threshold.value;
                float oldIntensity = bloom.intensity.value;
                float oldScatter = bloom.scatter.value;

                // Raise threshold so only truly HDR-bright pixels bloom
                // (fixes white streaks on sunlit ridgelines)
                bloom.threshold.overrideState = true;
                bloom.threshold.value = 1.5f;

                // Reduce intensity for subtler bloom
                bloom.intensity.overrideState = true;
                bloom.intensity.value = 0.15f;

                // Tighten scatter to reduce glow spread
                bloom.scatter.overrideState = true;
                bloom.scatter.value = 0.35f;

                Debug.Log(
                    $"[PostProcessingTuner] Bloom adjusted: " +
                    $"threshold {oldThreshold:F2}→1.50, " +
                    $"intensity {oldIntensity:F2}→0.15, " +
                    $"scatter {oldScatter:F2}→0.35");
            }
            else
            {
                Debug.Log("[PostProcessingTuner] No Bloom override found in Volume profile.");
            }
        }
    }
}
