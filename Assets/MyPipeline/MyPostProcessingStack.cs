using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[CreateAssetMenu(menuName = "Rendering/My Post-Processing Stack")]
public class MyPostProcessingStack : ScriptableObject
{
    enum Pass
    {
        Copy,
        Blur,
        DepthStripes,
        ToneMapping
    };

    static Mesh fullScreenTriangle;

    static Material material;

    static int mainTexId = Shader.PropertyToID("_MainTex");
    static int tempTexId = Shader.PropertyToID("_MyPostProcessingStackTempTex");
    static int depthTexId = Shader.PropertyToID("_DepthTex");
    static int resolvedTexId = Shader.PropertyToID("_MyPostProcessingStackResolvedTex");

    [SerializeField, Range(0, 10)]
    int blurStrength;

    [SerializeField]
    bool depthStripes;

    [SerializeField]
    bool toneMapping;
    [SerializeField, Range(1f, 100f)]
    float toneMappingRange = 100f;


    public bool NeedsDepth
    {
        get
        {
            return depthStripes;
        }
    }

    static void InitializeStatic()
    {
        if (fullScreenTriangle)
        {
            return;
        }

        fullScreenTriangle = new Mesh
        {
            name = "My Post-Processing Stack Full-Screen Triangle",
            vertices = new Vector3[]
            {
                new Vector3(-1f, -1f, 0f),
                new Vector3(-1f, 3f, 0f),
                new Vector3(3f, -1f, 0f)
            },
            triangles = new int[] {0, 1, 2},
        };
        fullScreenTriangle.UploadMeshData(true);

        material = new Material(Shader.Find("Hidden/My Pipeline/PostEffectStack"))
        {
            name = "My Post-Processing Stack material",
            hideFlags = HideFlags.HideAndDontSave
        };
    }

    public void RenderAfterTransparent(CommandBuffer cb, int cameraColorId, int cameraDepthId, int width, int height, int samples, RenderTextureFormat format)
    {

        if (blurStrength > 0)
        {
            if (toneMapping || samples > 1)
            {
                cb.GetTemporaryRT(resolvedTexId, width, height, 0, FilterMode.Bilinear);
                if (toneMapping)
                {
                    ToneMapping(cb, cameraColorId, resolvedTexId);
                }
                else
                {
                    Blit(cb, cameraColorId, resolvedTexId);
                }
               
                Blur(cb, resolvedTexId, width, height);
                cb.ReleaseTemporaryRT(resolvedTexId);
            }
            else
            {
                Blur(cb, cameraColorId, width, height);
            }

        }
        else if (toneMapping)
        {
            ToneMapping(cb, cameraColorId, BuiltinRenderTextureType.CameraTarget);
        }
        else
        {
            Blit(cb, cameraColorId, BuiltinRenderTextureType.CameraTarget);
        }
              
    }

    public void RenderAfterOqaque(CommandBuffer cb, int cameraColorId, int cameraDepthId, int width, int height, int samples, RenderTextureFormat format)
    {
        InitializeStatic();
        if (depthStripes)
        {
            DepthStripes(cb, cameraColorId, cameraDepthId, width, height, format);
        }
        
    }

    void DepthStripes(CommandBuffer cb, int cameraColorId, int cameraDepthId, int width, int height, RenderTextureFormat format)
    {
        cb.BeginSample("Depth Stripes");
        cb.GetTemporaryRT(tempTexId, width, height, 0, FilterMode.Point, format);
        cb.SetGlobalTexture(depthTexId, cameraDepthId);
        Blit(cb, cameraColorId, tempTexId, Pass.DepthStripes);
        Blit(cb, tempTexId, cameraColorId);
        cb.ReleaseTemporaryRT(tempTexId);
        cb.EndSample("Depth Stripes");
    }

    void Blur(CommandBuffer cb, int cameraColorId, int width, int height)
    {
        cb.BeginSample("Blur");
        if (blurStrength == 1)
        {
            Blit(cb, cameraColorId, BuiltinRenderTextureType.CameraTarget, Pass.Blur);
            cb.EndSample("Blur");
            return;
        }
        cb.GetTemporaryRT(tempTexId, width, height, 0, FilterMode.Bilinear);
        int passesLeft;
        for(passesLeft = blurStrength; passesLeft > 2; passesLeft -= 2)
        {
            Blit(cb, cameraColorId, tempTexId, Pass.Blur);
            Blit(cb, tempTexId, cameraColorId, Pass.Blur);
        }
        if (passesLeft > 1)
        {
            Blit(cb, cameraColorId, tempTexId, Pass.Blur);
            Blit(cb, tempTexId, BuiltinRenderTextureType.CameraTarget, Pass.Blur);
        }
        else
        {
            Blit(cb, cameraColorId, BuiltinRenderTextureType.CameraTarget, Pass.Blur);
        }
        
        cb.ReleaseTemporaryRT(tempTexId);
        cb.EndSample("Blur");
    }
   
    void Blit(CommandBuffer cb, RenderTargetIdentifier sourceId, RenderTargetIdentifier destinationId, Pass pass = Pass.Copy)
    {
        cb.SetGlobalTexture(mainTexId, sourceId);
        cb.SetRenderTarget(destinationId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
        cb.DrawMesh(fullScreenTriangle, Matrix4x4.identity, material, 0, (int)pass);
    }

    void ToneMapping(CommandBuffer cb, RenderTargetIdentifier sourceId, RenderTargetIdentifier destinationId)
    {
        cb.BeginSample("Tone Mapping");
        cb.SetGlobalFloat("_ReinhardModifier", 1f / toneMappingRange * toneMappingRange);
        Blit(cb, sourceId, destinationId, Pass.ToneMapping);
        cb.EndSample("Tone Mapping");
    }
}
