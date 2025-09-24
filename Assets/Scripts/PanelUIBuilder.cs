// PanelUIBuilder.cs
// Builds a small world-space UI panel attached to the left controller.
// Attach to: LeftControllerAnchor (a Transform representing the left-hand controller pose)
// Bind in Inspector: 5 prefab references in `modelPrefabs` OR set via ModelSelector.
// Responsibilities: create Canvas and simple selectable item visuals; expose UpdateSelection(int).

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace InstaSpace
{
  public class PanelUIBuilder : MonoBehaviour
  {
    [Header("Panel Settings")]
    [Tooltip("Prefabs corresponding to panel options (expect 5).")]
    public GameObject[] modelPrefabs = new GameObject[5];

    [Tooltip("Local offset from the controller where the panel will appear.")]
    public Vector3 localOffset = new Vector3(0.02f, 0f, 0.04f);

    [Tooltip("Scale of the panel in world units.")]
    public Vector3 panelScale = new Vector3(0.025f, 0.012f, 0.001f);

    [Header("Inspector Tuning")]
    [Tooltip("Controls how many world units make up a pixel; higher makes the UI appear smaller in world space.")]
    public float pixelsPerUnit = 500f;

    [Tooltip("Pixel size of the root panel. Adjust to make the panel physically larger/smaller in world space.")]
    public Vector2 rootSize = new Vector2(200f, 80f);

    [Header("Visuals")]
    public Color backgroundColor = new Color(0f, 0f, 0f, 0.6f);
    public Color selectedColor = new Color(0.2f, 0.6f, 1f, 0.9f);
    public Color unselectedColor = new Color(0.15f, 0.15f, 0.15f, 0.6f);
    public Color outlineColor = Color.white;

    // Internal
    private Canvas _canvas;
    private RectTransform _rootRect;
    private List<Image> _optionBackgrounds = new List<Image>();
    private bool _panelBuilt = false;

    void Awake()
    {
      // If a previously-built canvas exists as a child (e.g. from prior domain reload or editor operations),
      // try to reuse it instead of creating a duplicate. This prevents multiple panels appearing.
      Transform existing = transform.Find("ModelSelectorCanvas");
      if (existing != null)
      {
        _canvas = existing.GetComponent<Canvas>();
        var root = existing.transform.Find("Root");
        if (root != null)
          _rootRect = root.GetComponent<RectTransform>();
        _panelBuilt = (_canvas != null && _rootRect != null);
        if (!_panelBuilt)
          BuildPanel();
      }
      else
      {
        BuildPanel();
      }
    }
    // Do not force a rebuild on Start: rebuilding unconditionally after Awake caused duplicate
    // canvas creation during domain reloads and when editing in the Inspector. Use RebuildPanel()
    // only when explicitly requested (e.g. from SetupTools.UpdatePanelSizes).

    /// <summary>
    /// Force rebuild the panel with current settings
    /// </summary>
    public void RebuildPanel()
    {
      if (_canvas != null)
      {
        DestroyImmediate(_canvas.gameObject);
      }
      _optionBackgrounds.Clear();
      _panelBuilt = false;
      BuildPanel();
    }

    void BuildPanel()
    {
      // Remove any existing ModelSelectorCanvas child to avoid duplicates when rebuilding
      var existing = transform.Find("ModelSelectorCanvas");
      if (existing != null)
      {
        DestroyImmediate(existing.gameObject);
      }

      // Create a world-space Canvas as a child of this transform
      GameObject canvasGO = new GameObject("ModelSelectorCanvas");
      canvasGO.transform.SetParent(transform, false);
      canvasGO.transform.localPosition = localOffset;
      canvasGO.transform.localRotation = Quaternion.Euler(0, 0, 0); // face outward from controller
      canvasGO.transform.localScale = panelScale;

      _canvas = canvasGO.AddComponent<Canvas>();
      _canvas.renderMode = RenderMode.WorldSpace;
      _canvas.worldCamera = Camera.main; // best-effort; XR will replace camera at runtime

      CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
      // Use inspector-exposed pixelsPerUnit for convenient tuning.
      scaler.dynamicPixelsPerUnit = Mathf.Max(1f, pixelsPerUnit);

      canvasGO.AddComponent<GraphicRaycaster>();

      // Root panel - reduced size for smaller slots
      GameObject root = new GameObject("Root");
      root.transform.SetParent(canvasGO.transform, false);
      _rootRect = root.AddComponent<RectTransform>();
      // Use inspector-exposed rootSize so user can tune the pixel dimensions of the panel.
      _rootRect.sizeDelta = new Vector2(Mathf.Max(10f, rootSize.x), Mathf.Max(10f, rootSize.y));

      // Background
      GameObject bg = new GameObject("Background");
      bg.transform.SetParent(_rootRect, false);
      var bgRect = bg.AddComponent<RectTransform>();
      bgRect.anchorMin = Vector2.zero;
      bgRect.anchorMax = Vector2.one;
      bgRect.sizeDelta = Vector2.zero;

      Image bgImage = bg.AddComponent<Image>();
      bgImage.color = backgroundColor;

      // Create option slots horizontally - smaller and more compact
      int count = Mathf.Max(1, modelPrefabs.Length);
      float padding = 1f; // Reduced from 8f
      float slotW = (_rootRect.sizeDelta.x - padding * (count + 1)) / count;
      float slotH = _rootRect.sizeDelta.y - 2 * padding;

      for (int i = 0; i < count; i++)
      {
        GameObject slot = new GameObject($"Option_{i}");
        slot.transform.SetParent(_rootRect, false);
        var rect = slot.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(slotW, slotH);
        rect.anchorMin = new Vector2(0, 0);
        rect.anchorMax = new Vector2(0, 0);
        rect.pivot = new Vector2(0, 0);
        rect.anchoredPosition = new Vector2(padding + i * (slotW + padding), padding);

        // Background image per slot
        Image img = slot.AddComponent<Image>();
        img.color = unselectedColor;

        // Outline to indicate selection - smaller outline for compact design
        var outline = slot.AddComponent<Outline>();
        outline.effectColor = new Color(0, 0, 0, 0);
        outline.effectDistance = new Vector2(1, 1); // Reduced from (2,2)

        // Add a label with the prefab name (fallback when no thumbnail available)
        GameObject labelGO = new GameObject("Label");
        labelGO.transform.SetParent(slot.transform, false);
        var labelRect = labelGO.AddComponent<RectTransform>();
        labelRect.anchorMin = new Vector2(0, 0);
        labelRect.anchorMax = new Vector2(1, 1);
        labelRect.sizeDelta = Vector2.zero;

        var label = labelGO.AddComponent<Text>();
        label.alignment = TextAnchor.MiddleCenter;
        // Use the legacy runtime built-in font on newer Unity versions where Arial.ttf may no longer be provided.
        // Fallback to LegacyRuntime.ttf which is safe across Unity editor versions.
        try
        {
          label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }
        catch
        {
          // As a last resort, attempt to use any available default font (may return null in some editor states).
          label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
        label.text = modelPrefabs[i] ? modelPrefabs[i].name : $"Slot {i + 1}";
        label.color = Color.white;
        label.fontSize = 6; // Smaller font size for compact design
        label.resizeTextForBestFit = true;
        label.resizeTextMinSize = 6;
        label.resizeTextMaxSize = 10;

        _optionBackgrounds.Add(img);
      }

      _panelBuilt = true;
    }

    /// <summary>
    /// Update visual selection. Index is 0-based.
    /// </summary>
    public void UpdateSelection(int index)
    {
      for (int i = 0; i < _optionBackgrounds.Count; i++)
      {
        if (i == index)
        {
          _optionBackgrounds[i].color = selectedColor;
        }
        else
        {
          _optionBackgrounds[i].color = unselectedColor;
        }
      }
    }
  }
}
