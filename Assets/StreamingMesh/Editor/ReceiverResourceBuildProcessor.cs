using System.Collections.Generic;
using StreamingMesh.Core.Serialization;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace StreamingMesh.Editor
{
  // Strip texture dependencies from transient build-scene copies, never asset files.
  public sealed class ReceiverResourceBuildProcessor : IProcessSceneWithReport
  {
    public int callbackOrder => 0;
    public void OnProcessScene(Scene scene, BuildReport report)
    {
      if (report == null) return;
      PrepareScene(scene);
    }
    public static void PrepareScene(Scene scene)
    {
      var identities = new ResourceIdentityRegistry();
      var copies = new Dictionary<Material, Material>();
      foreach (var root in scene.GetRootGameObjects())
        foreach (var receiver in root.GetComponentsInChildren<Receiver>(true))
        {
          var settings = new SerializedObject(receiver);
          var bindings = settings.FindProperty("m_MaterialTemplates");
          bool strip = settings.FindProperty("m_StreamTexturesOnlyTemplates").boolValue;
          for (int index = 0; index < bindings.arraySize; index++)
          {
            var binding = bindings.GetArrayElementAtIndex(index);
            var reference = binding.FindPropertyRelative("template");
            var original = reference.objectReferenceValue as Material;
            if (original == null) continue;
            var id = binding.FindPropertyRelative("materialId");
            if (string.IsNullOrEmpty(id.stringValue)) id.stringValue = identities.GetId(original);
            if (!strip) continue;
            if (!copies.TryGetValue(original, out var copy))
            {
              copy = new Material(original) { name = original.name + " (stream template)" };
              var shader = copy.shader;
              for (int property = 0; property < shader.GetPropertyCount(); property++)
                if (shader.GetPropertyType(property) == ShaderPropertyType.Texture)
                  copy.SetTexture(shader.GetPropertyName(property), null);
              // Saved properties can contain textures from an earlier shader too.
              var serializedCopy = new SerializedObject(copy);
              var textures = serializedCopy.FindProperty("m_SavedProperties.m_TexEnvs");
              for (int texture = 0; texture < textures.arraySize; texture++)
                textures.GetArrayElementAtIndex(texture).FindPropertyRelative("second.m_Texture").objectReferenceValue = null;
              serializedCopy.ApplyModifiedPropertiesWithoutUndo();
              copies.Add(original, copy);
            }
            reference.objectReferenceValue = copy;
          }
          settings.ApplyModifiedPropertiesWithoutUndo();
        }
    }
  }
}
