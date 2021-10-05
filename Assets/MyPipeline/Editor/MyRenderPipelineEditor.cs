using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

[CustomEditor(typeof(MyRenderPipeline))]
public class MyRenderPipelineEditor : Editor
{
    SerializedProperty shadowCascades;
    SerializedProperty twoCascadesSplit;
    SerializedProperty fourCascadesSplit;

    private void OnEnable()
    {
        shadowCascades = serializedObject.FindProperty("shadowCascades");
        twoCascadesSplit = serializedObject.FindProperty("twoCascadesSplit");
        fourCascadesSplit = serializedObject.FindProperty("fourCascadesSplit");
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        switch(shadowCascades.enumValueIndex)
        {
            case 0:
                return;
            case 1:
                
                break;
            case 2:
                break;
        }
        serializedObject.ApplyModifiedProperties();
    }
}
