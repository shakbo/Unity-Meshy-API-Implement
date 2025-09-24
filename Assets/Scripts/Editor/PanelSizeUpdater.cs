using UnityEngine;
using UnityEditor;

namespace InstaSpace
{
  [CustomEditor(typeof(PanelUIBuilder))]
  public class PanelUIBuilderEditor : Editor
  {
    public override void OnInspectorGUI()
    {
      DrawDefaultInspector();

      PanelUIBuilder panelBuilder = (PanelUIBuilder)target;

      GUILayout.Space(10);

      if (GUILayout.Button("Apply Small Size Settings"))
      {
        Undo.RecordObject(panelBuilder, "Apply Small Size Settings");

        // Apply the smaller settings
        panelBuilder.localOffset = new Vector3(0.02f, 0f, 0.04f);
        panelBuilder.panelScale = new Vector3(0.025f, 0.012f, 0.001f);

        EditorUtility.SetDirty(panelBuilder);

        Debug.Log("[PanelUIBuilder] Applied small size settings - you may need to restart the scene to see changes");
      }

      if (GUILayout.Button("Apply Extra Small Size Settings"))
      {
        Undo.RecordObject(panelBuilder, "Apply Extra Small Size Settings");

        // Apply even smaller settings
        panelBuilder.localOffset = new Vector3(0.015f, 0f, 0.03f);
        panelBuilder.panelScale = new Vector3(0.02f, 0.01f, 0.001f);

        EditorUtility.SetDirty(panelBuilder);

        Debug.Log("[PanelUIBuilder] Applied extra small size settings - you may need to restart the scene to see changes");
      }
    }
  }
}