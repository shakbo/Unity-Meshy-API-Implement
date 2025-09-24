using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(InstaSpace.PanelUIBuilder))]
public class PanelUIBuilderEditor : Editor
{
  public override void OnInspectorGUI()
  {
    serializedObject.Update();

    // Draw default inspector UI for all serialized properties
    DrawPropertiesExcluding(serializedObject, "m_Script");

    EditorGUILayout.Space();

    if (GUILayout.Button("Rebuild Panel Now"))
    {
      foreach (var t in targets)
      {
        var builder = t as InstaSpace.PanelUIBuilder;
        if (builder != null)
        {
          // Call rebuild and mark object/scene dirty so changes persist
          builder.RebuildPanel();
          EditorUtility.SetDirty(builder);
          if (!Application.isPlaying)
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(builder.gameObject.scene);
        }
      }
    }

    serializedObject.ApplyModifiedProperties();
  }
}
