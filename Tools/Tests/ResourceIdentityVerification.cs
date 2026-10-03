using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamingMesh.Core.Serialization;
using StreamingMesh.Lib;
using UnityEditor;
using UnityEngine;

public static class ResourceIdentityVerification
{
  static int checks;
  static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
  public static void Run()
  {
    const string root="Assets/ResourceIdentityFixtures";
    AssetDatabase.CreateFolder("Assets", "ResourceIdentityFixtures");
    AssetDatabase.CreateFolder(root, "A"); AssetDatabase.CreateFolder(root, "B");
    var objects=new List<UnityEngine.Object>();
    try
    {
      var shader=Shader.Find("Hidden/StreamingMesh/MaterialFixture");
      var a=new Texture2D(2,2) {name="same"}; var b=new Texture2D(2,2) {name="same"};
      AssetDatabase.CreateAsset(a,root+"/A/same.asset"); AssetDatabase.CreateAsset(b,root+"/B/same.asset");
      var ma=new Material(shader) {name="same"}; var mb=new Material(shader) {name="same"};
      ma.SetTexture("_MainTex",a); mb.SetTexture("_MainTex",a);
      AssetDatabase.CreateAsset(ma,root+"/A/same.mat"); AssetDatabase.CreateAsset(mb,root+"/B/same.mat");
      var sub=new Material(shader) {name="same"}; AssetDatabase.AddObjectToAsset(sub,ma); AssetDatabase.SaveAssets();
      var registry=new ResourceIdentityRegistry();
      string aid=registry.GetId(a), mid=registry.GetId(ma);
      Check(aid!=registry.GetId(b),"Same-name textures in different directories collided");
      Check(mid!=registry.GetId(mb),"Same-name materials in different directories collided");
      Check(mid!=registry.GetId(sub),"Material subasset collided with main asset");
      Check(new ResourceIdentityRegistry().GetId(sub)==registry.GetId(sub),"Subasset identity changed between snapshots");
      Check(new ResourceIdentityRegistry().GetId(a)==aid,"Persistent identity changed between snapshots");
      Check(ResourceIdentity.ForPath("texture",root+"\\A\\same.asset")==aid,"Path separator normalization failed");
      Check(ResourceIdentity.ForPath("material",root+"/A/same.asset")!=aid,"Resource kinds collided");
      var ia=MaterialConverter.Serialize(ma,registry); var ib=MaterialConverter.Serialize(mb,registry);
      Check(ia.properties.Single(p=>p.name=="_MainTex").textureId==ib.properties.Single(p=>p.name=="_MainTex").textureId,"Shared texture was not shared by ID");
      var runtimeA=new Material(shader) {name="same"}; var runtimeB=new Material(shader) {name="same"};
      objects.Add(runtimeA); objects.Add(runtimeB);
      Check(registry.GetId(runtimeA)!=registry.GetId(runtimeB),"Generated resources collided");
      Check(registry.GetId(runtimeA)==registry.GetId(runtimeA),"Generated identity changed within snapshot");
      foreach(string path in new[]{"../same", "Assets/A/../same", "Assets//same", "Assets/A/./same"})
      { bool rejected=false; try{ResourceIdentity.ForPath("texture",path);}catch(ArgumentException){rejected=true;}Check(rejected,"Invalid path accepted"); }
      Check(ResourceIdentity.IsValid(aid) && !ResourceIdentity.IsValid(aid.ToUpperInvariant()) && !ResourceIdentity.IsValid("same"),"ID validation failed");
      foreach(int size in new[]{1,8192,1048576})
      {
        var bytes=Enumerable.Range(0,size).Select(i=>(byte)(i*31)).ToArray();
        var zipped=ExternalTools.Compress(bytes);
        Check(bytes.SequenceEqual(ExternalTools.DecompressExact(zipped,size)),"Exact gzip roundtrip failed");
        bool rejected=false;try{ExternalTools.DecompressExact(zipped,size-1);}catch(InvalidDataException){rejected=true;}Check(rejected,"Gzip allocation limit ignored");
      }
      // Unity permits empty material slots and extra slots for additional passes.
      var meshInfo=new MeshInfo {name="fixture",vertexCount=3,subMeshCount=1,
        materialIds=new List<string>{mid,"",registry.GetId(mb)},indices=new List<int>{0,1,2},
        indicesCounts=new List<int>{3},uv=new[]{Vector2.zero,Vector2.right,Vector2.up}};
      var meshData=InfoConverter.Serialize(meshInfo);
      List<string> slots;
      var mesh=MeshConverter.DeserializeFromBinary(meshData,0,meshData.Length,4,out slots);
      Check(slots.SequenceEqual(meshInfo.materialIds),"Additional/empty material slots were changed");
      UnityEngine.Object.DestroyImmediate(mesh);
      bool missing=false;
      try {MeshConverter.DeserializeFromBinary(meshData,0,meshData.Length,4,out slots,new Dictionary<string,Material>());}
      catch(InvalidDataException) {missing=true;}
      Check(missing,"Missing mesh material was accepted");
      var broken=ExternalTools.Compress(new byte[4096]); broken[broken.Length-4]=1;
      bool badLength=false;try {ExternalTools.DecompressExact(broken,8192);}catch(Exception error) when (error is IOException || error is InvalidDataException){badLength=true;}
      Check(badLength,"Incorrect GZip trailer was accepted");
      Debug.Log("PASS resource identities: "+checks+" checks");
    }
    finally
    {
      foreach(var obj in objects) UnityEngine.Object.DestroyImmediate(obj);
      AssetDatabase.DeleteAsset(root);
    }
  }
}
