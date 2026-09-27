// No importer changes, preserve sRGB/linear colors and alpha from a non-readable texture.
float maxError=0;
foreach(bool linear in new[]{false,true}) {
 var texture=new Texture2D(2,2,TextureFormat.RGBA32,false,linear);
 var expected=new[]{new Color32(31,79,157,43),new Color32(218,132,66,255),new Color32(0,255,128,192),new Color32(255,0,255,0)};
 texture.SetPixels32(expected);texture.Apply(false,true);
 var bytes=StreamingMesh.STMHttpSerializer.GetTextureToPNGByteArray(texture,true);
 var decoded=new Texture2D(2,2,TextureFormat.RGBA32,false,linear);decoded.LoadImage(bytes);
 var actual=decoded.GetPixels32();
 for(int i=0;i<4;i++) {
  int[] d={Math.Abs(actual[i].r-expected[i].r),Math.Abs(actual[i].g-expected[i].g),Math.Abs(actual[i].b-expected[i].b),Math.Abs(actual[i].a-expected[i].a)};
  maxError=Mathf.Max(maxError,d.Max());
 }
 UnityEngine.Object.DestroyImmediate(texture);UnityEngine.Object.DestroyImmediate(decoded);
}
if(maxError>2) throw new Exception("Color/alpha changed during GPU export: "+maxError);
var path=UnityEditor.AssetDatabase.GetAllAssetPaths().First(p=>p.Contains("UnityChanKAGURA") && p.EndsWith(".png") && UnityEditor.AssetImporter.GetAtPath(p) is UnityEditor.TextureImporter);
var before=System.IO.File.ReadAllBytes(path+".meta");
var imported=UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path);
var png=StreamingMesh.STMHttpSerializer.GetTextureToPNGByteArray(imported,true);
if(!before.SequenceEqual(System.IO.File.ReadAllBytes(path+".meta"))) throw new Exception("Texture importer changed");
return new {passed=true,maxChannelError=maxError,asset=path,pngBytes=png.Length};
