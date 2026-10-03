// Run in an Editor folder with the KAGURA material assets and URP Toon package.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using StreamingMesh;
using StreamingMesh.Core.Serialization;
using StreamingMesh.Core.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class ReceiverMaterialVerification
{
  static int checks;
  static void Check(bool condition,string message) { checks++; if(!condition) throw new Exception(message); }
  static void Near(float a,float b,string message) { Check(Mathf.Abs(a-b)<1e-6f,message+": "+a+" != "+b); }

  public static void Run()
  {
    var textures=new Dictionary<string,Texture2D>();
    var oldCulture=CultureInfo.CurrentCulture;
    try
    {
      // Scalar wire formatting must be independent of machine locale.
      CultureInfo.CurrentCulture=new CultureInfo("fr-FR");
      string folder="Assets/Samples/UnityChanKAGURA/Models/FBX/Materials";
      var guids=AssetDatabase.FindAssets("t:Material",new[] {folder});
      Check(guids.Length==9,"Expected all nine KAGURA materials");
      foreach(var guid in guids)
      {
        var original=AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
        var sender=new Material(original) {name=original.name};
        // Change a parameter from the local .mat so template-only copying cannot pass.
        if(sender.HasProperty("_BaseColor_Step")) sender.SetFloat("_BaseColor_Step",0.73125f);
        if(sender.HasProperty("_Outline_Width")) sender.SetFloat("_Outline_Width",0.003125f);
        sender.renderQueue=2451; sender.enableInstancing=true; sender.doubleSidedGI=true;
        sender.SetOverrideTag("RenderType","TransparentCutout");
        sender.SetShaderPassEnabled("ShadowCaster",false);
        sender.DisableKeyword("_EMISSIVE_SIMPLE"); sender.EnableKeyword("_EMISSIVE_ANIMATION");
        if(sender.HasProperty("_MainTex")) {sender.SetTextureScale("_MainTex",new Vector2(1.25f,0.75f));sender.SetTextureOffset("_MainTex",new Vector2(0.125f,-0.25f));}
        var info=MaterialConverter.Serialize(sender);
        foreach(var property in info.properties.Where(p=>p.type==4 && !string.IsNullOrEmpty(p.value)))
        {
          if(textures.ContainsKey(property.value)) continue;
          var png=TextureConverter.SerializeToPNG(sender.GetTexture(property.name));
          var received=TextureConverter.DeserializeFromBinary(png,0,png.Length,property.textureLinear,property.textureMipChain);
          received.name=property.value; textures.Add(property.value,received);
        }
        // Exercise the actual JSON/binary receive entry point and real template precedence.
        var data=InfoConverter.Serialize(info);
        var templates=new[] {new MaterialTemplateBinding {materialName=sender.name,template=original}};
        var restored=MaterialConverter.DeserializeFromBinary(data,0,data.Length,null,Shader.Find("Unlit/Color"),textures,templates,true);
        Compare(sender,restored);
        Check(original.renderQueue!=restored.renderQueue,"Template asset was changed or Sender state ignored");
        UnityEngine.Object.DestroyImmediate(restored);
        var bare=MaterialConverter.Deserialize(info,null,Shader.Find("Unlit/Color"),textures,null,true);
        Compare(sender,bare); UnityEngine.Object.DestroyImmediate(bare);
        VerifyLegacy(original,textures);
        UnityEngine.Object.DestroyImmediate(sender);
      }
      VerifyGeneric();
      VerifyNormalExport();
      VerifyRenderedAppearance(textures);
      VerifyScene();
      ReceiverTangentVerification.Run();
      Debug.Log("PASS receiver materials: "+checks+" checks, KAGURA materials="+guids.Length+", textures="+textures.Count);
    }
    catch(Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); throw; }
    finally {CultureInfo.CurrentCulture=oldCulture;foreach(var texture in textures.Values) UnityEngine.Object.DestroyImmediate(texture);}
  }

  static void Compare(Material sender,Material receiver)
  {
    Check(sender.shader==receiver.shader,"Shader differs for "+sender.name);
    Check(sender.renderQueue==receiver.renderQueue,"RenderQueue differs");
    Check(sender.enableInstancing==receiver.enableInstancing && sender.doubleSidedGI==receiver.doubleSidedGI,"Material flags differ");
    Check(sender.globalIlluminationFlags==receiver.globalIlluminationFlags,"GI flags differ");
    Check(sender.shaderKeywords.OrderBy(k=>k).SequenceEqual(receiver.shaderKeywords.OrderBy(k=>k)),"Keywords differ for "+sender.name);
    Check(sender.GetTag("RenderType",false)==receiver.GetTag("RenderType",false),"RenderType differs");
    Check(!receiver.GetShaderPassEnabled("ShadowCaster"),"Pass state differs");
    var shader=sender.shader;
    for(int i=0;i<shader.GetPropertyCount();i++)
    {
      string name=shader.GetPropertyName(i);
      switch(shader.GetPropertyType(i))
      {
        case ShaderPropertyType.Float: case ShaderPropertyType.Range: Near(sender.GetFloat(name),receiver.GetFloat(name),sender.name+"/"+name);break;
        case ShaderPropertyType.Int: Check(sender.GetInteger(name)==receiver.GetInteger(name),"Integer differs: "+name);break;
        case ShaderPropertyType.Color: Check(sender.GetColor(name)==receiver.GetColor(name),"Color differs: "+name);break;
        case ShaderPropertyType.Vector: Check(sender.GetVector(name)==receiver.GetVector(name),"Vector differs: "+name);break;
        case ShaderPropertyType.Texture:
          var source=sender.GetTexture(name); var target=receiver.GetTexture(name);
          Check(source==null ? target==null : target!=null && source.name==target.name,"Texture reference differs: "+name);
          Check(sender.GetTextureScale(name)==receiver.GetTextureScale(name) && sender.GetTextureOffset(name)==receiver.GetTextureOffset(name),"UV transform differs: "+name);
          if(source!=null)
          {
            Check(source.width==target.width && source.height==target.height,"Texture dimensions differ");
            Check(source.filterMode==target.filterMode && source.wrapModeU==target.wrapModeU && source.wrapModeV==target.wrapModeV,"Sampler differs");
            Check(((Texture2D)source).mipmapCount==((Texture2D)target).mipmapCount,"Mip chain differs");
            Check(UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat)==
              UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(target.graphicsFormat),"Texture color space differs: "+name);
          }
          break;
      }
    }
  }

  static void VerifyLegacy(Material template,Dictionary<string,Texture2D> textures)
  {
    var info=new MaterialInfo {name=template.name,properties=new List<MaterialPropertyInfo> {
      new MaterialPropertyInfo {name="_BaseColor_Step",type=3,value="{}"}}};
    var restored=MaterialConverter.Deserialize(info,null,template.shader,textures,
      new[] {new MaterialTemplateBinding {materialName=template.name,template=template}});
    if(template.HasProperty("_BaseColor_Step")) Near(template.GetFloat("_BaseColor_Step"),restored.GetFloat("_BaseColor_Step"),"Lost legacy value overwrote template");
    Check(template.shaderKeywords.OrderBy(k=>k).SequenceEqual(restored.shaderKeywords.OrderBy(k=>k)),"Legacy keywords overwritten");
    UnityEngine.Object.DestroyImmediate(restored);
  }

  static void VerifyGeneric()
  {
    var shader=Shader.Find("Hidden/StreamingMesh/MaterialFixture"); Check(shader!=null,"Fixture missing");
    var source=new Material(shader) {name="fixture"}; source.SetFloat("_Float",0.8125f); source.SetInteger("_Integer",123456789);
    source.SetVector("_Vector",new Vector4(1,-2,3,4)); source.SetColor("_Color",new Color(0.1f,0.2f,0.3f,0.4f));
    source.SetTexture("_MainTex",null);
    var info=MaterialConverter.Serialize(source);
    var restored=MaterialConverter.Deserialize(info,new ShaderTable(),shader,new Dictionary<string,Texture2D>());
    Near(restored.GetFloat("_Float"),0.8125f,"Scalar roundtrip");
    Check(restored.GetInteger("_Integer")==123456789,"Integer precision lost");
    Check(restored.GetVector("_Vector")==source.GetVector("_Vector") && restored.GetColor("_Color")==source.GetColor("_Color"),"Vector/color roundtrip");
    Check(restored.GetTexture("_MainTex")==null,"Explicit null texture ignored");
    var mapping=new ShaderTable(); mapping.GetTable().Add("fixture",Shader.Find("Unlit/Color"));
    var fallback=MaterialConverter.Deserialize(info,mapping,shader,null,null,true);
    Check(fallback.shader.name=="Unlit/Color","Explicit shader mapping ignored");
    info.properties.Add(new MaterialPropertyInfo {name="_MainTex",type=4,value="missing"});
    bool rejected=false; try {MaterialConverter.Deserialize(info,null,shader,null);} catch(InvalidOperationException) {rejected=true;}
    Check(rejected,"Missing texture silently kept a local texture");
    foreach(var material in new[] {source,restored,fallback}) UnityEngine.Object.DestroyImmediate(material);
  }

  static void VerifyNormalExport()
  {
    const string path="Assets/normal-export-fixture.png";
    var input=new Texture2D(4,4,TextureFormat.RGBA32,false,true);
    var direction=new Vector3(0.3f,-0.4f,Mathf.Sqrt(0.75f)); var color=new Color(direction.x*0.5f+0.5f,direction.y*0.5f+0.5f,direction.z*0.5f+0.5f,1);
    input.SetPixels(Enumerable.Repeat(color,16).ToArray());input.Apply();File.WriteAllBytes(path,input.EncodeToPNG());
    UnityEngine.Object.DestroyImmediate(input); AssetDatabase.ImportAsset(path);
    var importer=(TextureImporter)AssetImporter.GetAtPath(path); importer.textureType=TextureImporterType.NormalMap;
    importer.textureCompression=TextureImporterCompression.Uncompressed; importer.SaveAndReimport();
    var normal=AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    var png=TextureConverter.SerializeToPNG(normal);
    var decoded=new Texture2D(2,2,TextureFormat.RGBA32,false,true); decoded.LoadImage(png);
    var actual=decoded.GetPixel(0,0);
    Check(Mathf.Abs(actual.r-color.r)<0.01f && Mathf.Abs(actual.g-color.g)<0.01f && Mathf.Abs(actual.b-color.b)<0.01f && actual.a==1,"Normal PNG is not canonical XYZ");
    UnityEngine.Object.DestroyImmediate(decoded); AssetDatabase.DeleteAsset(path);
  }

  static void VerifyRenderedAppearance(Dictionary<string,Texture2D> textures)
  {
    var asset=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/StreamingMeshURP.asset");
    Check(asset!=null,"URP asset missing"); GraphicsSettings.defaultRenderPipeline=asset; QualitySettings.renderPipeline=asset;
    var source=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
      "Assets/Samples/UnityChanKAGURA/Models/FBX/UnityCHanKAGURA.fbx"));
    var cameraObject=new GameObject("material-verification-camera"); var camera=cameraObject.AddComponent<Camera>();
    cameraObject.AddComponent<UniversalAdditionalCameraData>();
    camera.transform.position=new Vector3(0,1.1f,3.9f); camera.transform.LookAt(new Vector3(0,1.1f,0));
    camera.fieldOfView=45; camera.nearClipPlane=0.05f; camera.farClipPlane=50;
    camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(0.12f,0.14f,0.18f);
    var lightObject=new GameObject("material-verification-light"); var light=lightObject.AddComponent<Light>();
    light.type=LightType.Directional;light.intensity=1.2f;
    light.transform.rotation=new Quaternion(0.3303661f,-0.24321036f,0.08852133f,0.9076734f);
    RenderSettings.ambientMode=AmbientMode.Flat; RenderSettings.ambientLight=new Color(0.5f,0.5f,0.5f);
    var restoredMaterials=new List<Material>(); var bakedMeshes=new List<Mesh>();
    foreach(var skinned in source.GetComponentsInChildren<SkinnedMeshRenderer>())
    {
      // Bake the same shape once; this comparison isolates material transport
      // from the separate receiver normal/tangent reconstruction approximation.
      var mesh=new Mesh();skinned.BakeMesh(mesh);bakedMeshes.Add(mesh);
      var filter=skinned.gameObject.AddComponent<MeshFilter>();filter.sharedMesh=mesh;
      var draw=skinned.gameObject.AddComponent<MeshRenderer>();draw.sharedMaterials=skinned.sharedMaterials;skinned.enabled=false;
    }
    // The first request initializes URP and new renderer state in batch mode.
    UnityEngine.Object.DestroyImmediate(Render(camera));
    var original=Render(camera);
    foreach(var draw in source.GetComponentsInChildren<MeshRenderer>())
    {
      var materials=new List<Material>();
      foreach(var template in draw.sharedMaterials)
      {
        var info=MaterialConverter.Serialize(template);
        // Reuse exported texture payloads but apply the original material settings.
        var material=MaterialConverter.Deserialize(info,null,template.shader,textures,
          new[] {new MaterialTemplateBinding {materialName=template.name,template=template}});
        materials.Add(material);restoredMaterials.Add(material);
      }
      draw.sharedMaterials=materials.ToArray();
    }
    var received=Render(camera);
    var a=original.GetPixels32();var b=received.GetPixels32();
    double absolute=0;int changed=0,foreground=0;
    for(int i=0;i<a.Length;i++)
    {
      int error=Math.Abs(a[i].r-b[i].r)+Math.Abs(a[i].g-b[i].g)+Math.Abs(a[i].b-b[i].b);
      absolute+=error;if(error>3)changed++;
      if(Math.Abs(a[i].r-a[0].r)+Math.Abs(a[i].g-a[0].g)+Math.Abs(a[i].b-a[0].b)>20)foreground++;
    }
    double mean=absolute/(a.Length*3);
    Directory.CreateDirectory("Results");File.WriteAllBytes("Results/kagura-sender-materials.png",original.EncodeToPNG());
    File.WriteAllBytes("Results/kagura-receiver-materials.png",received.EncodeToPNG());
    Debug.Log("KAGURA material render comparison: meanByteError="+mean+", changedPixels="+changed+", foreground="+foreground);
    Check(foreground>10000,"KAGURA was not rendered");
    Check(mean<2,"KAGURA material image mismatch");
    var draws=source.GetComponentsInChildren<MeshRenderer>();
    var meshes=draws.Select(draw=>draw.GetComponent<MeshFilter>().sharedMesh).ToArray();
    var positions=meshes.SelectMany(mesh=>mesh.vertices.Select(p=>new Vector4(p.x,p.y,p.z,1))).ToArray();
    var requirements=meshes.Select(mesh=>true).ToArray();
    using(var pipeline=new GpuVertexPipeline(meshes,128,4,4,requirements,requirements))
    {
      // Test-only injection isolates presentation from wire quantization. Exercise
      // the production GPU output layout, normals, tangents and Toon draw together.
      var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
      var frames=(System.Collections.IList)typeof(GpuVertexPipeline).GetField("m_Frames",flags).GetValue(pipeline);
      var frame=(GpuVertexPipeline.Frame)frames[0];
      ((ComputeBuffer)typeof(GpuVertexPipeline.Frame).GetField("vertices",flags).GetValue(frame)).SetData(positions);
      typeof(GpuVertexPipeline.Frame).GetProperty("Bounds").SetValue(frame,new Bounds(Vector3.zero,Vector3.one*20));
      pipeline.Present(frame,frame,0);
      var gpuImage=Render(camera);File.WriteAllBytes("Results/kagura-receiver-gpu.png",gpuImage.EncodeToPNG());
      var pixels=gpuImage.GetPixels32();int visible=0;
      foreach(var pixel in pixels) if(Math.Abs(pixel.r-pixels[0].r)+Math.Abs(pixel.g-pixels[0].g)+Math.Abs(pixel.b-pixels[0].b)>20)visible++;
      Check(visible>10000,"GPU receiver Toon mesh was not rendered");
      Check(meshes.All(mesh=>mesh.HasVertexAttribute(VertexAttribute.Tangent)),"GPU Toon tangents missing");
      Debug.Log("KAGURA GPU Toon presentation: meshes="+meshes.Length+", vertices="+positions.Length+", tangentBytes="+pipeline.TangentBytes);
      UnityEngine.Object.DestroyImmediate(gpuImage);
    }
    foreach(var item in restoredMaterials)UnityEngine.Object.DestroyImmediate(item);
    foreach(var item in bakedMeshes)UnityEngine.Object.DestroyImmediate(item);
    foreach(var item in new UnityEngine.Object[] {source,cameraObject,lightObject,original,received})UnityEngine.Object.DestroyImmediate(item);
  }

  static Texture2D Render(Camera camera)
  {
    var target=new RenderTexture(512,512,24,RenderTextureFormat.ARGB32);target.Create();
    RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest {destination=target});
    var previous=RenderTexture.active;RenderTexture.active=target;
    var image=new Texture2D(512,512,TextureFormat.RGBA32,false);image.ReadPixels(new Rect(0,0,512,512),0,0);image.Apply();
    RenderTexture.active=previous;target.Release();UnityEngine.Object.DestroyImmediate(target);return image;
  }

  static void VerifyScene()
  {
    var scene=UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity");
    var receiver=scene.GetRootGameObjects().SelectMany(root=>root.GetComponentsInChildren<Receiver>(true)).Single();
    var settings=new SerializedObject(receiver);
    Check(settings.FindProperty("m_NormalMode").enumValueIndex==2 && settings.FindProperty("m_TangentMode").enumValueIndex==2,"Sample basis settings missing");
    Check(settings.FindProperty("m_UseSenderShader").boolValue,"Sender shader setting missing");
    var bindings=settings.FindProperty("m_MaterialTemplates"); Check(bindings.arraySize==9,"Sample mappings missing");
    for(int i=0;i<bindings.arraySize;i++)
    {
      var element=bindings.GetArrayElementAtIndex(i);
      var template=(Material)element.FindPropertyRelative("template").objectReferenceValue;
      Check(template!=null && template.name==element.FindPropertyRelative("materialName").stringValue,"Sample material binding unresolved");
    }
    var key=scene.GetRootGameObjects().SelectMany(root=>root.GetComponentsInChildren<Light>()).Single();
    Check(key.type==LightType.Directional && Mathf.Abs(key.intensity-1.2f)<1e-6f,"Sample lighting missing");
  }
}
