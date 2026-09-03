// =============================================================================
// SampleCatalogUI.cs
//
// Landing-page demo fallback (FR12): lists GET /samples and stores a sample
// id in ImageSessionManager so Generate can call POST /process-sample/{id}.
// Built at runtime under the landing canvas.
// =============================================================================

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

namespace DepthWizard.UI
{
    [Serializable]
    public class SampleEntry
    {
        public string id;
        public string name;
        public string filename;
        public bool is_georef;
    }

    [Serializable]
    public class SamplesResponse
    {
        public SampleEntry[] samples;
    }

    /// <summary>
    /// Fetches preloaded samples and creates one button per entry.
    /// </summary>
    public class SampleCatalogUI : MonoBehaviour
    {
        private const string DEFAULT_BACKEND = "http://localhost:8000";

        private static readonly Color COL_BTN = new Color(0.06f, 0.10f, 0.16f, 0.92f);
        private static readonly Color COL_ACCENT = new Color(0f, 0.898f, 1f, 1f);

        private string _backendUrl = DEFAULT_BACKEND;
        private Transform _row;
        private Action<string> _onStatus;

        public void Bind(Transform parent, string backendUrl, Action<string> onStatus)
        {
            _backendUrl = string.IsNullOrEmpty(backendUrl) ? DEFAULT_BACKEND : backendUrl;
            _onStatus = onStatus;
            BuildRow(parent);
            StartCoroutine(FetchSamples());
        }

        private void BuildRow(Transform parent)
        {
            var rowGo = new GameObject("SampleRow");
            rowGo.transform.SetParent(parent, false);
            var rowRect = rowGo.AddComponent<RectTransform>();
            rowRect.anchorMin = new Vector2(0.5f, 0f);
            rowRect.anchorMax = new Vector2(0.5f, 0f);
            rowRect.pivot = new Vector2(0.5f, 0f);
            rowRect.anchoredPosition = new Vector2(0f, 96f);
            rowRect.sizeDelta = new Vector2(900f, 48f);

            var layout = rowGo.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 12f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            _row = rowGo.transform;
        }

        private IEnumerator FetchSamples()
        {
            string url = _backendUrl.TrimEnd('/') + "/samples";
            using (var req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _onStatus?.Invoke("Sample list unavailable (is the backend running?)");
                    yield break;
                }

                SamplesResponse parsed;
                try
                {
                    parsed = JsonUtility.FromJson<SamplesResponse>(req.downloadHandler.text);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SampleCatalogUI] Bad /samples JSON: {ex.Message}");
                    yield break;
                }

                if (parsed == null || parsed.samples == null || parsed.samples.Length == 0)
                {
                    _onStatus?.Invoke("No sample files on disk. Drop GeoTIFFs in backend/data/samples/.");
                    yield break;
                }

                foreach (var sample in parsed.samples)
                    AddButton(sample);
            }
        }

        private void AddButton(SampleEntry sample)
        {
            var go = new GameObject("Sample_" + sample.id);
            go.transform.SetParent(_row, false);

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 220f;
            le.minHeight = 40f;

            var img = go.AddComponent<Image>();
            img.color = COL_BTN;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var captured = sample;
            btn.onClick.AddListener(() => OnSampleClicked(captured));

            var textGo = new GameObject("Label");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = sample.name;
            tmp.fontSize = 14;
            tmp.color = COL_ACCENT;
            tmp.alignment = TextAlignmentOptions.Center;
        }

        private void OnSampleClicked(SampleEntry sample)
        {
            var session = ImageSessionManager.Instance;
            if (session == null) return;
            session.SetSample(sample.id, sample.name);
            _onStatus?.Invoke("Sample selected: " + sample.name);
        }
    }
}
