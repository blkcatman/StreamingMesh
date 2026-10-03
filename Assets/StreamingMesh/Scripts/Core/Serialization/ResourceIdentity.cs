using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace StreamingMesh.Core.Serialization
{
  // Resource identity is independent of display names and resource contents.
  // A registry belongs to one initial-data snapshot, never to a playback frame.
  public sealed class ResourceIdentityRegistry
  {
    readonly Dictionary<UnityEngine.Object, string> ids = new Dictionary<UnityEngine.Object, string>();
    readonly string session = Guid.NewGuid().ToString("N");
    public string GetId(UnityEngine.Object resource)
    {
      if (resource == null) return "";
      if (ids.TryGetValue(resource, out var id)) return id;
      string kind = resource is Material ? "material" : resource is Texture ? "texture" : throw new ArgumentException("Unsupported resource type.");
#if UNITY_EDITOR
      string path = UnityEditor.AssetDatabase.GetAssetPath(resource).Replace('\\', '/');
      if (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal))
      {
        long localId = 0;
        if (UnityEditor.AssetDatabase.IsSubAsset(resource) &&
            !UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(resource, out string _, out localId))
          throw new InvalidOperationException("Cannot identify subasset: " + path);
        id = ResourceIdentity.ForPath(kind, path, localId);
      }
      else
#endif
        id = ResourceIdentity.Hash("runtime\0" + kind + "\0" + session + "\0" + ids.Count);
      ids.Add(resource, id);
      return id;
    }
  }

  public static class ResourceIdentity
  {
    public static string ForPath(string kind, string path, long localId = 0)
    {
      if (kind != "material" && kind != "texture") throw new ArgumentException("Unsupported resource kind.", nameof(kind));
      if (string.IsNullOrEmpty(path)) throw new ArgumentException("Asset path is required.", nameof(path));
      path = path.Replace('\\', '/');
      if (!(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)) ||
          path.Contains("//") || path.Contains("/../") || path.Contains("/./") || path.EndsWith("/..") || path.EndsWith("/.") || path.EndsWith("/"))
        throw new ArgumentException("Expected a canonical project-relative asset path.", nameof(path));
      return Hash("asset\0" + kind + "\0" + path + "\0" + localId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    public static string Hash(string text)
    {
      using (var sha = SHA256.Create())
      {
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var result = new StringBuilder(64);
        foreach (byte value in bytes) result.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return result.ToString();
      }
    }
    public static bool IsValid(string id)
    {
      if (id == null || id.Length != 64) return false;
      foreach (char value in id) if (!(value >= '0' && value <= '9') && !(value >= 'a' && value <= 'f')) return false;
      return true;
    }
  }
}
