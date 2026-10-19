using System.IO;
using UnityEditor;
using UnityEngine;

namespace VRWorkshop.Editor
{
    public static class WorkshopScannedMaterials
    {
        const string Source="Assets/VRWorkshop/Textures/PolyHaven/concrete_floor_02/";
        const string Destination="Assets/VRWorkshop/Resources/Industrial/";
        public static void Prepare()
        {
            if(!File.Exists(Source+"concrete_floor_02_diff_2k.jpg"))return;
            AssetDatabase.Refresh();
            Texture2D Map(string suffix,bool color=false,bool normal=false,bool readable=false)
            {
                string path=Source+"concrete_floor_02_"+suffix+"_2k.jpg";
                var importer=(TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType=normal?TextureImporterType.NormalMap:TextureImporterType.Default;
                importer.sRGBTexture=color;importer.isReadable=readable;importer.mipmapEnabled=true;
                importer.maxTextureSize=2048;importer.anisoLevel=8;importer.wrapMode=TextureWrapMode.Repeat;
                importer.textureCompression=TextureImporterCompression.CompressedHQ;importer.SaveAndReimport();
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            var albedo=Map("diff",true);var normal=Map("nor_gl",normal:true);
            var rough=Map("rough",readable:true);var ao=Map("ao");
            var pixels=rough.GetPixels32();
            for(int i=0;i<pixels.Length;i++)pixels[i]=new Color32(0,0,0,(byte)(255-pixels[i].r));
            var mask=new Texture2D(rough.width,rough.height,TextureFormat.RGBA32,true,true){name="Industrial-ScannedConcrete-MetalSmooth",wrapMode=TextureWrapMode.Repeat,anisoLevel=8};
            mask.SetPixels32(pixels);mask.Apply(true,false);mask.Compress(true);
            var existing=AssetDatabase.LoadAssetAtPath<Texture2D>(Destination+mask.name+".asset");
            if(existing==null)AssetDatabase.CreateAsset(mask,Destination+mask.name+".asset");
            else {EditorUtility.CopySerialized(mask,existing);Object.DestroyImmediate(mask);mask=existing;EditorUtility.SetDirty(mask);}
            string matPath=Destination+"Industrial-ScannedConcrete.mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if(material==null){material=new Material(Shader.Find("Standard")){name="Industrial-ScannedConcrete"};AssetDatabase.CreateAsset(material,matPath);}
            material.color=Color.white;material.enableInstancing=true;material.mainTexture=albedo;
            material.SetTexture("_BumpMap",normal);material.SetFloat("_BumpScale",.6f);material.EnableKeyword("_NORMALMAP");
            material.SetTexture("_MetallicGlossMap",mask);material.SetFloat("_GlossMapScale",.65f);material.EnableKeyword("_METALLICGLOSSMAP");
            material.SetTexture("_OcclusionMap",ao);material.SetFloat("_OcclusionStrength",.65f);
            material.mainTextureScale=new Vector2(11,10.5f);
            EditorUtility.SetDirty(material);AssetDatabase.SaveAssets();
            Map("rough");
        }
    }
}
