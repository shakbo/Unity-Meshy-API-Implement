// ModelSelector.cs
// Attach to: LeftControllerAnchor or a manager GameObject.
// Bind in Inspector: PanelUIBuilder (for visuals), PlacementManager (to update preview), and modelPrefabs.
// Responsibilities: read left-hand joystick + X/Y buttons, maintain selected index, update panel visuals and notify PlacementManager on confirm.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using XRInputDevice = UnityEngine.XR.InputDevice;
// Optional: Interaction SDK / XR Interaction Toolkit support is not required here.
// If you have the Interaction Toolkit and want typed integration, re-add
// `using UnityEngine.XR.Interaction.Toolkit;` and implement event-based input in an #if block.

namespace InstaSpace
{
  public class ModelSelector : MonoBehaviour
  {
    [Header("References")]
    public PanelUIBuilder panelUI;
    public PlacementManager placementManager;

    [Tooltip("Model prefabs available for selection (expect 5)")]
    public GameObject[] modelPrefabs = new GameObject[5];

    [Header("Input")]
    [Tooltip("Cooldown between thumbstick movements to prevent jitter")]
    public float joystickCooldown = 0.3f;

    private int _currentIndex = 0;
    private float _lastMoveTime = -1f;
    private Vector2 _lastThumbstickInput = Vector2.zero;

    void Start()
    {
      if (panelUI) panelUI.UpdateSelection(_currentIndex);
      if (placementManager && modelPrefabs.Length > 0)
      {
        placementManager.UpdatePreview(modelPrefabs[_currentIndex]);
      }
    }

    void Update()
    {
      HandleInput();
    }

    void HandleInput()
    {
      // Always use OVRInput for Meta Quest controllers
      // Note: editorDebug mode removed to avoid Input System conflicts

      // Get left controller thumbstick input using OVRInput directly
      Vector2 thumbstick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);

      // Process thumbstick input
      ProcessThumbstickInput(thumbstick);

      // Handle confirmation buttons (X button on left controller)
      if (OVRInput.GetDown(OVRInput.Button.One)) // X button on left controller
      {
        Debug.Log("[ModelSelector] OVRInput X button pressed");
        ConfirmSelection();
      }

      // Alternative: Y button on left controller  
      if (OVRInput.GetDown(OVRInput.Button.Two)) // Y button on left controller
      {
        Debug.Log("[ModelSelector] OVRInput Y button pressed");
        ConfirmSelection();
      }
    }

    void ProcessThumbstickInput(Vector2 thumbstick)
    {
      const float deadzone = 0.3f;

      // Debug thumbstick input (only log when there's significant input)
      if (thumbstick.magnitude > 0.1f)
      {
        Debug.Log($"[ModelSelector] Thumbstick input: {thumbstick}, magnitude: {thumbstick.magnitude:F2}");
      }

      // Only process input if it's outside deadzone and enough time has passed since last move
      if (Time.time - _lastMoveTime < joystickCooldown)
      {
        if (thumbstick.magnitude > deadzone)
        {
          Debug.Log($"[ModelSelector] Thumbstick input ignored due to cooldown. Remaining: {joystickCooldown - (Time.time - _lastMoveTime):F2}s");
        }
        return;
      }

      // Check for new input (different from last frame to avoid repeated moves)
      bool hasNewInput = false;

      // Horizontal movement (left/right)
      if (Mathf.Abs(thumbstick.x) > deadzone && Mathf.Abs(_lastThumbstickInput.x) <= deadzone)
      {
        if (thumbstick.x < -deadzone)
        {
          Debug.Log("[ModelSelector] Moving Left");
          MoveLeft();
          hasNewInput = true;
        }
        else if (thumbstick.x > deadzone)
        {
          Debug.Log("[ModelSelector] Moving Right");
          MoveRight();
          hasNewInput = true;
        }
      }

      // Vertical movement (up/down)
      if (Mathf.Abs(thumbstick.y) > deadzone && Mathf.Abs(_lastThumbstickInput.y) <= deadzone)
      {
        if (thumbstick.y > deadzone)
        {
          Debug.Log("[ModelSelector] Moving Up");
          MoveUp();
          hasNewInput = true;
        }
        else if (thumbstick.y < -deadzone)
        {
          Debug.Log("[ModelSelector] Moving Down");
          MoveDown();
          hasNewInput = true;
        }
      }

      if (hasNewInput)
      {
        _lastMoveTime = Time.time;
        Debug.Log($"[ModelSelector] New selection index: {_currentIndex}");
      }

      _lastThumbstickInput = thumbstick;
    }

    void MoveUp()
    {
      if (Time.time - _lastMoveTime < joystickCooldown) return;
      _lastMoveTime = Time.time;
      // For a 5-option horizontal UI, up/down can wrap or be mapped to previous/next
      _currentIndex = Mathf.Clamp(_currentIndex - 1, 0, modelPrefabs.Length - 1);
      ApplySelection();
    }

    void MoveDown()
    {
      if (Time.time - _lastMoveTime < joystickCooldown) return;
      _lastMoveTime = Time.time;
      _currentIndex = Mathf.Clamp(_currentIndex + 1, 0, modelPrefabs.Length - 1);
      ApplySelection();
    }

    void MoveLeft()
    {
      if (Time.time - _lastMoveTime < joystickCooldown) return;
      _lastMoveTime = Time.time;
      _currentIndex = (_currentIndex - 1 + modelPrefabs.Length) % modelPrefabs.Length;
      ApplySelection();
    }

    void MoveRight()
    {
      if (Time.time - _lastMoveTime < joystickCooldown) return;
      _lastMoveTime = Time.time;
      _currentIndex = (_currentIndex + 1) % modelPrefabs.Length;
      ApplySelection();
    }

    void ApplySelection()
    {
      Debug.Log($"[ModelSelector] ApplySelection called - Index: {_currentIndex}/{modelPrefabs.Length}");

      if (panelUI)
      {
        panelUI.UpdateSelection(_currentIndex);
        Debug.Log($"[ModelSelector] Updated PanelUI with index: {_currentIndex}");
      }
      else
      {
        Debug.LogWarning("[ModelSelector] PanelUI is null!");
      }

      if (placementManager && modelPrefabs.Length > _currentIndex && _currentIndex >= 0)
      {
        var selectedPrefab = modelPrefabs[_currentIndex];
        if (selectedPrefab != null)
        {
          placementManager.UpdatePreview(selectedPrefab);
          Debug.Log($"[ModelSelector] Updated PlacementManager with prefab: {selectedPrefab.name}");
        }
        else
        {
          Debug.LogWarning($"[ModelSelector] Selected prefab at index {_currentIndex} is null!");
        }
      }
      else
      {
        Debug.LogWarning($"[ModelSelector] Cannot update preview - PlacementManager: {placementManager != null}, ModelPrefabs count: {modelPrefabs.Length}, Index: {_currentIndex}");
      }
    }

    void ConfirmSelection()
    {
      // Confirm selection: tell PlacementManager to lock preview selection (if needed)
      if (placementManager && modelPrefabs.Length > _currentIndex)
      {
        placementManager.SetSelectedPrefab(modelPrefabs[_currentIndex]);
      }
    }
  }
}
