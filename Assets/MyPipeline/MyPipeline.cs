using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.GlobalIllumination;
using LightType = UnityEngine.LightType;
using Conditional = System.Diagnostics.ConditionalAttribute;
using UnityEngine.VFX;

public class MyPipeline : RenderPipeline
{
    const string RENDER_CAMERA_NAME = "Render Camera";
    const string RENDER_SHADOW_NAME = "Render Shadows";

    const int maxVisibleLights = 16;
    static int visibleLightColorsId = Shader.PropertyToID("_VisibleLightColors");
    static int visibleLightDirectionsOrPositionsId = Shader.PropertyToID("_VisibleLightDirectionsOrPositions");
    static int visibleLightAttenuationsId = Shader.PropertyToID("_VisibleLightAttenuations");
    static int visibleLightSpotDirectionsId = Shader.PropertyToID("_VisibleLightSpotDirections");
    static int lightDataId = Shader.PropertyToID("unity_LightData");

    static int visibleLightOcclusionMasksId = Shader.PropertyToID("_VisibleLightOcclusionMasks");
    Vector4[] visibleLightOcclusionMasks = new Vector4[maxVisibleLights];
    static Vector4[] occlusionMasks =
    {
        new Vector4(-1f, 0f, 0f, 0f),
        new Vector4(1f, 0f, 0f, 0f),
        new Vector4(0f, 1f, 0f, 0f),
        new Vector4(0f, 0f, 1f, 0f),
        new Vector4(0f, 0f, 0f, 1f)
    };

    static int shadowMapId = Shader.PropertyToID("_ShadowMap");
    //static int worldToShadowMatrixId = Shader.PropertyToID("_WorldToShadowMatrix");
    static int worldToShadowMatricesId = Shader.PropertyToID("_WorldToShadowMatrices");
    static int shadowBiasId = Shader.PropertyToID("_ShadowBias");
    //static int shadowStrengthId = Shader.PropertyToID("_ShadowStrength");
    static int shadowMapSizeId = Shader.PropertyToID("_ShadowMapSize");
    static int shadowDataId = Shader.PropertyToID("_ShadowData");
    static int globalShadowDataId = Shader.PropertyToID("_GlobalShadowData");
    static int cascadedShadowMapId = Shader.PropertyToID("_CascadedShadowMap");
    static int worldToShadowCascadeMatricesId = Shader.PropertyToID("_WorldToShadowCascadeMatrices");
    static int cascadedShadowMapSizeId = Shader.PropertyToID("_CascadedShadowMapSize");
    static int cascadedShadowStrengthId = Shader.PropertyToID("_CascadedShadowStrength");
    static int cascadeCullingSpheresId = Shader.PropertyToID("_CascadeCullingSpheres");
    static int subtractiveShadowColorId = Shader.PropertyToID("_SubtractiveShadowColor");

    static int ditherTextureId = Shader.PropertyToID("_DitherTexture");
    static int ditherTextureSTId = Shader.PropertyToID("_DitherTexture_ST");

    static int cameraColorTextureId = Shader.PropertyToID("_CameraColorTexture");
    static int cameraDepthTextureId = Shader.PropertyToID("_CameraDepthTexture");


#if UNITY_EDITOR
    static Lightmapping.RequestLightsDelegate lightmappingLightsDelegate = (Light[] inputLights, NativeArray<LightDataGI> outputLights) => {
        LightDataGI lightData = new LightDataGI();
        for (int i = 0; i < inputLights.Length; i++)
        {
            Light light = inputLights[i];
            switch(light.type)
            {
                case LightType.Directional:
                    var directionalLight = new DirectionalLight();
                    LightmapperUtils.Extract(light, ref directionalLight);
                    lightData.Init(ref directionalLight);
                    break;
                case LightType.Point:
                    var pointLight = new PointLight();
                    LightmapperUtils.Extract(light, ref pointLight);
                    lightData.Init(ref pointLight);
                    break;
                case LightType.Spot:
                    var spotLight = new SpotLight();
                    LightmapperUtils.Extract(light, ref spotLight);
                    lightData.Init(ref spotLight);
                    break;
                case LightType.Area:
                    var rectangleLight = new RectangleLight();
                    LightmapperUtils.Extract(light, ref rectangleLight);
                    lightData.Init(ref rectangleLight);
                    break;
                default:
                    lightData.InitNoBake(light.GetInstanceID());
                    break;
            }
            lightData.falloff = FalloffType.InverseSquared;
            outputLights[i] = lightData;
        }
    };
#endif   

    const string shadowsSoftKeyword = "_SHADOWS_SOFT";
    const string shadowsHardKeyword = "_SHADOWS_HARD";

    const string cascadedShadowsHardKeyword = "_CASCADED_SHADOWS_HARD";
    const string cascadedShadowsSoftKeyword = "_CASCADED_SHADOWS_SOFT";

    const string shadowmaskKeyword = "_SHADOWMASK";
    const string distanceShadowmaskKeyword = "_DISTANCE_SHADOWMASK";
    const string subtractiveLightingKeyword = "_SUBTRACTIVE_LIGHTING";

    Vector4[] visibleLightColors = new Vector4[maxVisibleLights];
    Vector4[] visibleLightDirectionsOrPositions = new Vector4[maxVisibleLights];
    Vector4[] visibleLightAttenuations = new Vector4[maxVisibleLights];
    Vector4[] visibleLightSpotDirections = new Vector4[maxVisibleLights];

    Vector4[] shadowData = new Vector4[maxVisibleLights];
    Matrix4x4[] worldToShadowMatrices = new Matrix4x4[maxVisibleLights];
    Matrix4x4[] worldToShadowCascadeMatrices = new Matrix4x4[5];
    Vector4[] cascadeCullingSpheres = new Vector4[4];

    CullingResults cullResults;
    CommandBuffer cameraBuffer = new CommandBuffer
    {
        name = RENDER_CAMERA_NAME
    };

    CommandBuffer shadowBuffer = new CommandBuffer
    {
        name = "Render Shadows"
    };

    CommandBuffer postProcessingBuffer = new CommandBuffer
    {
        name = "Post-Processing"
    };

    Material errorMaterial;

    bool dynamicBatching;

    bool instancing;

    Texture2D ditherTexture;
    float ditherAnimationFrameDuration;
    Vector4[] ditherSTs;
    float lastDitherTime;
    int ditherSTIndex = -1;

    RenderTexture shadowMap, cascadedShadowMap;
    int shadowMapSize;
    int shadowTileCount;
    float shadowDistance;
    Vector4 globalShadowData;

    int shadowCascades;
    Vector3 shadowCascadeSplit;

    bool mainLightExists;

    MyPostProcessingStack defaultStack;

    float renderScale;
    int msaaSamples;

    bool allowHDR;

    public MyPipeline(bool dynamicBatching, bool instancing, MyPostProcessingStack defaultStack, Texture2D ditherTexture, float ditherAnimationSpeed, 
        int shadowMapSize, float shadowDistance, float shadowFadeRange, int shadowCascades, Vector3 shadowCascadeSplit,
        float renderScale, int massSamples, bool allowHDR)
    {
        
        GraphicsSettings.lightsUseLinearIntensity = true;
        if (SystemInfo.usesReversedZBuffer)
        {
            worldToShadowCascadeMatrices[4].m33 = 1f;
        }
        this.dynamicBatching = dynamicBatching;
        this.instancing = instancing;

        this.defaultStack = defaultStack;

        this.ditherTexture = ditherTexture;
        if (ditherAnimationSpeed > 0f && Application.isPlaying)
        {
            ConfigureDitherAnimation(ditherAnimationSpeed);
        }
        this.shadowMapSize = shadowMapSize;
        this.shadowDistance = shadowDistance;
        globalShadowData.y = 1f / shadowFadeRange;
        this.shadowCascades = shadowCascades;
        this.shadowCascadeSplit = shadowCascadeSplit;
#if UNITY_EDITOR
        Lightmapping.SetDelegate(lightmappingLightsDelegate);
#endif
        this.renderScale = renderScale;
        QualitySettings.antiAliasing = msaaSamples;
        this.msaaSamples = Mathf.Max(QualitySettings.antiAliasing, 1);
        Debug.Log("this.msaaSamples: " + this.msaaSamples);
        this.allowHDR = allowHDR;
    }

    void ConfigureDitherAnimation(float ditherAnimationSpeed)
    {
        ditherAnimationFrameDuration = 1f / ditherAnimationSpeed;
        ditherSTs = new Vector4[16];
        UnityEngine.Random.State state = UnityEngine.Random.state;
        for (int i = 0; i < ditherSTs.Length; i++)
        {
            ditherSTs[i] = new Vector4(
                (i & 1) == 0 ? (1f / 64f) : (-1f / 64f),
                (i & 2) == 0 ? (1f / 64f) : (-1f / 64f),
                UnityEngine.Random.value, UnityEngine.Random.value
            );
        }
        UnityEngine.Random.state = state;
    }

#if UNITY_EDITOR
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Lightmapping.ResetDelegate();
    }
#endif

    protected override void Render(ScriptableRenderContext context, Camera[] cameras)
    {
        ConfigureDitherPattern(context);
        
        foreach (var camera in cameras)
        {
            Render(context, camera);
        }
    }

    void ConfigureDitherPattern(ScriptableRenderContext context)
    {
       if (ditherSTIndex < 0)
        {
            ditherSTIndex = 0;
            lastDitherTime = Time.unscaledTime;
            cameraBuffer.SetGlobalTexture(ditherTextureId, ditherTexture);
            cameraBuffer.SetGlobalVector(ditherTextureSTId, new Vector4(1f / 64f, 1f / 64f, 0f, 0f));
            context.ExecuteCommandBuffer(cameraBuffer);
            cameraBuffer.Clear();
        }
        else if (ditherAnimationFrameDuration > 0f)
        {
            float currentTime = Time.unscaledTime;
            if (currentTime - lastDitherTime >= ditherAnimationFrameDuration)
            {
                lastDitherTime = currentTime;
                ditherSTIndex = ditherSTIndex < 15 ? ditherSTIndex + 1 : 0;
                cameraBuffer.SetGlobalVector(ditherTextureSTId, ditherSTs[ditherSTIndex]);
            }
            context.ExecuteCommandBuffer(cameraBuffer);
            cameraBuffer.Clear();
        }
    }


    RenderTexture SetShadowRenderTarget()
    {
        RenderTexture texture = RenderTexture.GetTemporary(shadowMapSize, shadowMapSize, 16, RenderTextureFormat.Shadowmap);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;

        CoreUtils.SetRenderTarget(shadowBuffer, texture, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, ClearFlag.Depth);

        return texture;
    }

    Vector2 ConfigureShadowTile(int tileIndex, int split, float tileSize)
    {
        Vector2 tileOffset;
        tileOffset.x = tileIndex % split;
        tileOffset.y = tileIndex / split;
        Rect tileViewport = new Rect(tileOffset.x * tileSize, tileOffset.y * tileSize, tileSize, tileSize);
        shadowBuffer.SetViewport(tileViewport);
        shadowBuffer.EnableScissorRect(new Rect(tileViewport.x + 4f, tileViewport.y + 4f, tileSize - 8f, tileSize - 8f));
        return tileOffset;
    }

    void CalculateWorldToShadowMatrix(ref Matrix4x4 viewMatrix, ref Matrix4x4 projectionMatrix, out Matrix4x4 worldToShadowMatrix)
    {
        if (SystemInfo.usesReversedZBuffer)
        {
            projectionMatrix.m20 = -projectionMatrix.m20;
            projectionMatrix.m21 = -projectionMatrix.m21;
            projectionMatrix.m22 = -projectionMatrix.m22;
            projectionMatrix.m23 = -projectionMatrix.m23;
        }
        //var scaleOffset = Matrix4x4.TRS(Vector3.one * 0.5f, Quaternion.identity, Vector3.one * 0.5f);
        var scaleOffset = Matrix4x4.identity;
        scaleOffset.m00 = scaleOffset.m11 = scaleOffset.m22 = 0.5f;
        scaleOffset.m03 = scaleOffset.m13 = scaleOffset.m23 = 0.5f;
        //Matrix4x4 worldToShadowMatrix = scaleOffset * (projectionMatrix * viewMatrix);
        //shadowBuffer.SetGlobalMatrix(worldToShadowMatrixId, worldToShadowMatrix);
        worldToShadowMatrix = scaleOffset * (projectionMatrix * viewMatrix);
    }

    void RenderCascadedShadows(ScriptableRenderContext context)
    {
        float tileSize = shadowMapSize / 2;
        cascadedShadowMap = SetShadowRenderTarget();
        shadowBuffer.BeginSample(RENDER_SHADOW_NAME);
        //shadowBuffer.SetGlobalVector(globalShadowDataId, new Vector4(0f, shadowDistance * shadowDistance));
        context.ExecuteCommandBuffer(shadowBuffer);
        shadowBuffer.Clear();

        Light shadowLight = cullResults.visibleLights[0].light;
        shadowBuffer.SetGlobalFloat(shadowBiasId, shadowLight.shadowBias);
        var shadowSettings = new ShadowDrawingSettings(cullResults, 0);
        var tileMatrix = Matrix4x4.identity;
        tileMatrix.m00 = tileMatrix.m11 = 0.5f;

        for (int i = 0; i < shadowCascades; i++)
        {
            Matrix4x4 viewMatrix, projectionMatrix;
            ShadowSplitData splitData;
            cullResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(0, i, shadowCascades, shadowCascadeSplit, (int)tileSize,
                shadowLight.shadowNearPlane, out viewMatrix, out projectionMatrix, out splitData);

            Vector2 tileOffset = ConfigureShadowTile(i, 2, tileSize);
            shadowBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
            context.ExecuteCommandBuffer(shadowBuffer);
            shadowBuffer.Clear();

            ShadowSplitData curSplitData = shadowSettings.splitData;
            cascadeCullingSpheres[i] = curSplitData.cullingSphere = splitData.cullingSphere;
            cascadeCullingSpheres[i].w *= splitData.cullingSphere.w;
            shadowSettings.splitData = curSplitData;
            context.DrawShadows(ref shadowSettings);

            CalculateWorldToShadowMatrix(ref viewMatrix, ref projectionMatrix, out worldToShadowCascadeMatrices[i]);
            tileMatrix.m03 = tileOffset.x * 0.5f;
            tileMatrix.m13 = tileOffset.y * 0.5f;
            worldToShadowCascadeMatrices[i] = tileMatrix * worldToShadowCascadeMatrices[i];
        }

        shadowBuffer.DisableScissorRect();
        shadowBuffer.SetGlobalTexture(cascadedShadowMapId, cascadedShadowMap);
        shadowBuffer.SetGlobalVectorArray(cascadeCullingSpheresId, cascadeCullingSpheres);
        shadowBuffer.SetGlobalMatrixArray(worldToShadowCascadeMatricesId, worldToShadowCascadeMatrices);
        float invShadowMapSize = 1f / shadowMapSize;
        shadowBuffer.SetGlobalVector(cascadedShadowMapSizeId, new Vector4(invShadowMapSize, invShadowMapSize, shadowMapSize, shadowMapSize));
        shadowBuffer.SetGlobalFloat(cascadedShadowStrengthId, shadowLight.shadowStrength);
        bool hard = shadowLight.shadows == LightShadows.Hard;
        CoreUtils.SetKeyword(shadowBuffer, cascadedShadowsHardKeyword, hard);
        CoreUtils.SetKeyword(shadowBuffer, cascadedShadowsSoftKeyword, !hard);
        shadowBuffer.EndSample(RENDER_SHADOW_NAME);
        context.ExecuteCommandBuffer(shadowBuffer);
        shadowBuffer.Clear();
    }

    void RenderShadows(ScriptableRenderContext context)
    {
        //if (!cullResults.GetShadowCasterBounds(0, out Bounds b))
        //{
        //    return;
        //}

        int split;
        if (shadowTileCount <= 1)
        {
            split = 1;
        }
        else if (shadowTileCount <= 4)
        {
            split = 2;
        }
        else if (shadowTileCount <= 9)
        {
            split = 3;
        }
        else
        {
            split = 4;
        }
        float tileSize = shadowMapSize / split;
        float tileScale = 1f / split;
        globalShadowData.x = tileScale;
        //Rect tileViewport = new Rect(0f, 0f, tileSize, tileSize);

        //shadowMap = RenderTexture.GetTemporary(shadowMapSize, shadowMapSize, 16, RenderTextureFormat.Shadowmap);
        //shadowMap.filterMode = FilterMode.Bilinear;
        //shadowMap.wrapMode = TextureWrapMode.Clamp;

        //CoreUtils.SetRenderTarget(shadowBuffer, shadowMap, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, ClearFlag.Depth);
        shadowMap = SetShadowRenderTarget();
        shadowBuffer.BeginSample(RENDER_SHADOW_NAME);
        //shadowBuffer.SetGlobalVector(globalShadowDataId, new Vector4(tileScale, shadowDistance * shadowDistance));
        context.ExecuteCommandBuffer(shadowBuffer);
        shadowBuffer.Clear();

        int tileIndex = 0;
        bool hardShadows = false;
        bool softShadows = false;
        for (int i = mainLightExists ? 1 : 0; i < cullResults.visibleLights.Length; i++)
        {
            if (i == maxVisibleLights)
            {
                break;
            }

            if (shadowData[i].x <= 0f)
            {
                continue;
            }

            Matrix4x4 viewMatrix, projectionMatrix;
            ShadowSplitData splitData;
            //if(!cullResults.ComputeSpotShadowMatricesAndCullingPrimitives(i, out viewMatrix, out projectionMatrix, out splitData))
            //{
            //    shadowData[i].x = 0f;
            //    continue;
            //}
            bool validShadows;
            if (shadowData[i].z > 0f)
            {
                validShadows = cullResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(i, 0, 1, Vector3.right, (int)tileSize,
                    cullResults.visibleLights[i].light.shadowNearPlane, out viewMatrix, out projectionMatrix, out splitData);
            }
            else
            {
                validShadows = cullResults.ComputeSpotShadowMatricesAndCullingPrimitives(i, out viewMatrix, out projectionMatrix, out splitData);
            }

            if (!validShadows)
            {
                shadowData[i].x = 0f;
                continue;
            }

            Vector2 tileOffset = ConfigureShadowTile(tileIndex, split, tileSize);
            //float tileOffsetX = tileIndex % split;
            //float tileOffsetY = tileIndex / split;
            //tileViewport.x = tileOffsetX * tileSize;
            //tileViewport.y = tileOffsetY * tileSize;
            shadowData[i].z = tileOffset.x * tileScale;
            shadowData[i].w = tileOffset.y * tileScale;
            ////if (split > 1)
            ////{
            //    shadowBuffer.SetViewport(tileViewport);
            //    //shadowBuffer.EnableScissorRect(new Rect(tileViewport.x + 4f, tileViewport.y + 4f, tileSize - 8f, tileSize - 8f));
            ////}

            shadowBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
            //if (SystemInfo.usesReversedZBuffer)
            //{
            //    projectionMatrix.m20 = -projectionMatrix.m20;
            //    projectionMatrix.m21 = -projectionMatrix.m21;
            //    projectionMatrix.m22 = -projectionMatrix.m22;
            //    projectionMatrix.m23 = -projectionMatrix.m23;
            //}
            ////var scaleOffset = Matrix4x4.TRS(Vector3.one * 0.5f, Quaternion.identity, Vector3.one * 0.5f);
            //var scaleOffset = Matrix4x4.identity;
            //scaleOffset.m00 = scaleOffset.m11 = scaleOffset.m22 = 0.5f;
            //scaleOffset.m03 = scaleOffset.m13 = scaleOffset.m23 = 0.5f;
            ////Matrix4x4 worldToShadowMatrix = scaleOffset * (projectionMatrix * viewMatrix);
            ////shadowBuffer.SetGlobalMatrix(worldToShadowMatrixId, worldToShadowMatrix);
            //worldToShadowMatrices[i] = scaleOffset * (projectionMatrix * viewMatrix);
            CalculateWorldToShadowMatrix(ref viewMatrix, ref projectionMatrix, out worldToShadowMatrices[i]);

            //if (split > 1)
            //{
            //    var tileMatrix = Matrix4x4.identity;
            //    tileMatrix.m00 = tileMatrix.m11 = tileScale;
            //    tileMatrix.m03 = tileOffsetX * tileScale;
            //    tileMatrix.m13 = tileOffsetY * tileScale;
            //    worldToShadowMatrices[i] = tileMatrix * worldToShadowMatrices[i];
            //}
            shadowBuffer.SetGlobalMatrixArray(worldToShadowMatricesId, worldToShadowMatrices);

            var light = cullResults.visibleLights[i].light;
            shadowBuffer.SetGlobalFloat(shadowBiasId, light.shadowBias);
            //shadowBuffer.SetGlobalFloat(shadowStrengthId, light.shadowStrength);
            shadowBuffer.SetGlobalVectorArray(shadowDataId, shadowData);
            float invShadowMapSize = 1f / shadowMapSize;
            shadowBuffer.SetGlobalVector(shadowMapSizeId, new Vector4(invShadowMapSize, invShadowMapSize, shadowMapSize, shadowMapSize));

            //if (light.shadows == LightShadows.Soft)
            //{
            //    shadowBuffer.EnableShaderKeyword(shadowsSoftKeyword);
            //}
            //else
            //{
            //    shadowBuffer.DisableShaderKeyword(shadowsSoftKeyword);
            //}
            //CoreUtils.SetKeyword(shadowBuffer, shadowsSoftKeyword, light.shadows == LightShadows.Soft);



            var shadowSettings = new ShadowDrawingSettings(cullResults, i);
            var shadowSettingsSplitData = shadowSettings.splitData;
            shadowSettingsSplitData.cullingSphere = splitData.cullingSphere;
            shadowSettings.splitData = shadowSettingsSplitData;
            context.DrawShadows(ref shadowSettings);

            tileIndex += 1;

            if (shadowData[i].y <= 0)
            {
                hardShadows = true;
            }
            else
            {
                softShadows = true;
            }
        }

        //if (split > 1)
        //{
        shadowBuffer.DisableScissorRect();
        //}

        shadowBuffer.SetGlobalTexture(shadowMapId, shadowMap);
        CoreUtils.SetKeyword(shadowBuffer, shadowsHardKeyword, hardShadows);
        CoreUtils.SetKeyword(shadowBuffer, shadowsSoftKeyword, softShadows);

        shadowBuffer.EndSample(RENDER_SHADOW_NAME);
        context.ExecuteCommandBuffer(shadowBuffer);
        shadowBuffer.Clear();

    }


    void Render(ScriptableRenderContext context, Camera camera)
    {
        ScriptableCullingParameters cullingParameters;
        if (!camera.TryGetCullingParameters(out cullingParameters))
        {
            return;
        }

        cullingParameters.shadowDistance = Mathf.Min(shadowDistance, camera.farClipPlane);

        cullResults = context.Cull(ref cullingParameters);

        if (cullResults.visibleLights.Length > 0)
        {
            ConfigureLights();
            if (mainLightExists)
            {
                RenderCascadedShadows(context);
            }
            else
            {
                cameraBuffer.DisableShaderKeyword(cascadedShadowsHardKeyword);
                cameraBuffer.DisableShaderKeyword(cascadedShadowsSoftKeyword);
            }
            if (shadowTileCount > 0)
            {
                RenderShadows(context);
            }
            else
            {
                cameraBuffer.DisableShaderKeyword(shadowsHardKeyword);
                cameraBuffer.DisableShaderKeyword(shadowsSoftKeyword);
            }
        }
        else
        {
            cameraBuffer.SetGlobalVector(lightDataId, Vector4.zero);
            cameraBuffer.DisableShaderKeyword(cascadedShadowsHardKeyword);
            cameraBuffer.DisableShaderKeyword(cascadedShadowsSoftKeyword);
            cameraBuffer.DisableShaderKeyword(shadowsHardKeyword);
            cameraBuffer.DisableShaderKeyword(shadowsSoftKeyword);
        }


#if UNITY_EDITOR
        if (camera.cameraType == CameraType.SceneView)
        {
            ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
        }

#endif

        context.SetupCameraProperties(camera);

        var myPipelineCamera = camera.GetComponent<MyPipelineCamera>();
        MyPostProcessingStack activeStack = myPipelineCamera ? myPipelineCamera.PostProcessingStack : defaultStack;

        bool scaledRendering = (renderScale < 1f || renderScale > 1f) && camera.cameraType == CameraType.Game;

        int renderWidth = camera.pixelWidth;
        int renderHeight = camera.pixelHeight;
        if (scaledRendering)
        {
            renderWidth = (int)(renderWidth * renderScale);
            renderHeight = (int)(renderHeight * renderScale);
        }

        int renderSamples = camera.allowMSAA ? msaaSamples : 1;
        bool renderToTexture = scaledRendering || renderSamples > 1 || activeStack;

        bool needsDepth = activeStack && activeStack.NeedsDepth;
        bool needsDirectDepth = needsDepth && renderSamples == 1;
        bool needsDepthOnlyPass = needsDepth && renderSamples > 1;

        RenderTextureFormat format = allowHDR && camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;

        if (renderToTexture)
        {
            cameraBuffer.GetTemporaryRT(cameraColorTextureId, renderWidth, renderHeight, needsDirectDepth ? 0 : 24, FilterMode.Bilinear, format,
                RenderTextureReadWrite.Default, renderSamples);
            if(needsDepth)
            {
                cameraBuffer.GetTemporaryRT(cameraDepthTextureId, renderWidth, renderHeight, 24, FilterMode.Point, RenderTextureFormat.Depth,
                RenderTextureReadWrite.Linear, renderSamples);
                
            }
            if (needsDirectDepth)
            {
                cameraBuffer.SetRenderTarget(cameraColorTextureId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
               cameraDepthTextureId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            }
            else
            {
                cameraBuffer.SetRenderTarget(cameraColorTextureId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            }
            
        }

        CameraClearFlags clearFlags = camera.clearFlags;
        //camereBuffer.BeginSample(RENDER_CAMERA_NAME);
        cameraBuffer.ClearRenderTarget((clearFlags & CameraClearFlags.Depth) != 0, (clearFlags & CameraClearFlags.Color) != 0, camera.backgroundColor);

        cameraBuffer.BeginSample(RENDER_CAMERA_NAME);
        //camereBuffer.EndSample(RENDER_CAMERA_NAME);
        cameraBuffer.SetGlobalVectorArray(visibleLightColorsId, visibleLightColors);
        cameraBuffer.SetGlobalVectorArray(visibleLightDirectionsOrPositionsId, visibleLightDirectionsOrPositions);
        cameraBuffer.SetGlobalVectorArray(visibleLightAttenuationsId, visibleLightAttenuations);
        cameraBuffer.SetGlobalVectorArray(visibleLightSpotDirectionsId, visibleLightSpotDirections);
        cameraBuffer.SetGlobalVectorArray(visibleLightOcclusionMasksId, visibleLightOcclusionMasks);
        globalShadowData.z = 1f - cullingParameters.shadowDistance * globalShadowData.y;
        cameraBuffer.SetGlobalVector(globalShadowDataId, globalShadowData);
        context.ExecuteCommandBuffer(cameraBuffer);
        cameraBuffer.Clear();

        SortingSettings sortingSettings = new SortingSettings(camera);
        sortingSettings.criteria = SortingCriteria.CommonOpaque;
        var drawSettings = new DrawingSettings(new ShaderTagId("SRPDefaultUnlit"), sortingSettings);
        drawSettings.enableDynamicBatching = dynamicBatching;
        drawSettings.enableInstancing = instancing;
        if (cullResults.visibleLights.Length > 0)
        {
            drawSettings.perObjectData = PerObjectData.LightData | PerObjectData.LightIndices;
        }
        drawSettings.perObjectData |= PerObjectData.ReflectionProbes | PerObjectData.Lightmaps | PerObjectData.LightProbe
            | PerObjectData.LightProbeProxyVolume | PerObjectData.ShadowMask | PerObjectData.OcclusionProbe | PerObjectData.OcclusionProbeProxyVolume;
        var filterSettings = new FilteringSettings(RenderQueueRange.opaque);


        context.DrawRenderers(cullResults, ref drawSettings, ref filterSettings);

        context.DrawSkybox(camera);

        if (activeStack)
        {
            if (needsDepthOnlyPass)
            {
                SortingSettings depthOnlySortingSettings = new SortingSettings(camera);
                depthOnlySortingSettings.criteria = SortingCriteria.CommonOpaque;
                var depthOnlyDrawSettings = new DrawingSettings(new ShaderTagId("DepthOnly"), depthOnlySortingSettings);
                cameraBuffer.SetRenderTarget(cameraDepthTextureId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
                cameraBuffer.ClearRenderTarget(true, false, Color.clear);
                context.ExecuteCommandBuffer(cameraBuffer);
                cameraBuffer.Clear();
                context.DrawRenderers(cullResults, ref depthOnlyDrawSettings, ref filterSettings);
            }

            activeStack.RenderAfterOqaque(postProcessingBuffer, cameraColorTextureId, cameraDepthTextureId, renderWidth, renderHeight, renderSamples, format);
            context.ExecuteCommandBuffer(postProcessingBuffer);
            postProcessingBuffer.Clear();
            if (needsDirectDepth)
            {
                cameraBuffer.SetRenderTarget(cameraColorTextureId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                cameraDepthTextureId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            }
            else
            {
                cameraBuffer.SetRenderTarget(cameraColorTextureId, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            }
            
            context.ExecuteCommandBuffer(cameraBuffer);
            cameraBuffer.Clear();
        }

        filterSettings.renderQueueRange = RenderQueueRange.transparent;
        sortingSettings.criteria = SortingCriteria.CommonTransparent;
        drawSettings.sortingSettings = sortingSettings;
        context.DrawRenderers(cullResults, ref drawSettings, ref filterSettings);

        DrawDefaultPipeline(context, camera);

        if (renderToTexture)
        {
            if (activeStack)
            {
                activeStack.RenderAfterTransparent(postProcessingBuffer, cameraColorTextureId, cameraDepthTextureId, renderWidth, renderHeight, renderSamples, format);
                context.ExecuteCommandBuffer(postProcessingBuffer);
                postProcessingBuffer.Clear();
                
            }
            else
            {
                cameraBuffer.Blit(cameraColorTextureId, BuiltinRenderTextureType.CameraTarget);
            }
            cameraBuffer.ReleaseTemporaryRT(cameraColorTextureId);
            if (needsDepth)
            {
                cameraBuffer.ReleaseTemporaryRT(cameraDepthTextureId);
            }
            
        }
        

        cameraBuffer.EndSample(RENDER_CAMERA_NAME);
        context.ExecuteCommandBuffer(cameraBuffer);
        cameraBuffer.Clear();

        context.Submit();

        if (shadowMap)
        {
            RenderTexture.ReleaseTemporary(shadowMap);
            shadowMap = null;
        }
        if (cascadedShadowMap)
        {
            RenderTexture.ReleaseTemporary(cascadedShadowMap);
            cascadedShadowMap = null;
        }
    }

    Vector4 ConfigureShadows(int lightIndex, Light shadowLight)
    {
        Vector4 shadow = Vector4.zero;
        Bounds shadowBounds;
        if (shadowLight.shadows != LightShadows.None && cullResults.GetShadowCasterBounds(lightIndex, out shadowBounds))
        {
            shadowTileCount += 1;
            shadow.x = shadowLight.shadowStrength;
            shadow.y = shadowLight.shadows == LightShadows.Soft ? 1f : 0f;
        }
        return shadow;
    }

    void ConfigureLights()
    {
        mainLightExists = false;
        bool shadowmaskExists = false;
        bool subtractiveLighting = false;
        shadowTileCount = 0;
        for (int i = 0; i < cullResults.visibleLights.Length; i++)
        {
            if (i == maxVisibleLights)
            {
                break;
            }
            VisibleLight light = cullResults.visibleLights[i];
            visibleLightColors[i] = light.finalColor;
            Vector4 attenuation = Vector4.zero;
            //防止点光源衰减影响其他光类型
            attenuation.w = 1f;
            Vector4 shadow = Vector4.zero;

            LightBakingOutput baking = light.light.bakingOutput;
            visibleLightOcclusionMasks[i] = occlusionMasks[baking.occlusionMaskChannel + 1];
            if (baking.lightmapBakeType == LightmapBakeType.Mixed)
            {
                shadowmaskExists |= baking.mixedLightingMode == MixedLightingMode.Shadowmask;
                //subtractiveLighting |= baking.mixedLightingMode == MixedLightingMode.Subtractive;
                if (baking.mixedLightingMode == MixedLightingMode.Subtractive)
                {
                    subtractiveLighting = true;
                    cameraBuffer.SetGlobalColor(subtractiveShadowColorId, UnityEngine.RenderSettings.subtractiveShadowColor.linear);
                }
            }

            if (light.lightType == LightType.Directional)
            {
                Vector4 zDir = light.localToWorldMatrix.GetColumn(2);
                zDir.x = -zDir.x;
                zDir.y = -zDir.y;
                zDir.z = -zDir.z;
                visibleLightDirectionsOrPositions[i] = zDir;
                shadow = ConfigureShadows(i, light.light);
                //z == 1 方向光阴影; z == 0 聚光灯阴影;
                shadow.z = 1f;
                if (i == 0 && shadow.x > 0 && shadowCascades > 0)
                {
                    mainLightExists = true;
                    shadowTileCount -= 1;
                }
            }
            else
            {
                visibleLightDirectionsOrPositions[i] = light.localToWorldMatrix.GetColumn(3);
                attenuation.x = 1f / Mathf.Max(light.range * light.range, 0.00001f);

                if (light.lightType == LightType.Spot)
                {
                    Vector4 zDir = light.localToWorldMatrix.GetColumn(2);
                    zDir.x = -zDir.x;
                    zDir.y = -zDir.y;
                    zDir.z = -zDir.z;
                    visibleLightSpotDirections[i] = zDir;

                    float outerRad = Mathf.Deg2Rad * 0.5f * light.spotAngle;
                    float outerCos = Mathf.Cos(outerRad);
                    float outerTan = Mathf.Tan(outerRad);
                    float innerCos = Mathf.Cos(Mathf.Atan((46f / 64f) * outerTan));
                    float angleRange = Mathf.Max(innerCos - outerCos, 0.001f);
                    attenuation.z = 1f / angleRange;
                    attenuation.w = -outerCos * attenuation.z;

                    //Light shadowLight = light.light;
                    //Bounds shadowBounds;
                    //if(shadowLight.shadows != LightShadows.None && cullResults.GetShadowCasterBounds(i, out shadowBounds))
                    //{
                    //    shadowTileCount += 1;
                    //    shadow.x = shadowLight.shadowStrength;
                    //    shadow.y = shadowLight.shadows == LightShadows.Soft ? 1f : 0f;
                    //}
                    shadow = ConfigureShadows(i, light.light);
                }
                else
                {
                    visibleLightSpotDirections[i] = Vector4.one;
                }
            }
            visibleLightAttenuations[i] = attenuation;
            shadowData[i] = shadow;
        }

        bool useDistanceShadowmask = QualitySettings.shadowmaskMode == ShadowmaskMode.DistanceShadowmask;
        CoreUtils.SetKeyword(cameraBuffer, shadowmaskKeyword, shadowmaskExists && !useDistanceShadowmask);
        CoreUtils.SetKeyword(cameraBuffer, distanceShadowmaskKeyword, shadowmaskExists && useDistanceShadowmask);
        CoreUtils.SetKeyword(cameraBuffer, subtractiveLightingKeyword, subtractiveLighting);

        if (mainLightExists || cullResults.visibleLights.Length > maxVisibleLights)
        {
            var lightIndices = cullResults.GetLightIndexMap(Allocator.Temp);
            if (mainLightExists)
            {
                lightIndices[0] = -1;
            }
            for (int i = maxVisibleLights; i < cullResults.visibleLights.Length; i++)
            {
                lightIndices[i] = -1;
            }
            cullResults.SetLightIndexMap(lightIndices);
        }
        
    }

    [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
    void DrawDefaultPipeline(ScriptableRenderContext context, Camera camera)
    {
        if (errorMaterial == null)
        {
            Shader errorShader = Shader.Find("Hidden/InternalErrorShader");
            errorMaterial = new Material(errorShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        SortingSettings sortingSettings = new SortingSettings(camera);
        var drawSettings = new DrawingSettings(new ShaderTagId("ForwardBase"), sortingSettings);
        drawSettings.SetShaderPassName(1, new ShaderTagId("PrepassBase"));
        drawSettings.SetShaderPassName(2, new ShaderTagId("Always"));
        drawSettings.SetShaderPassName(3, new ShaderTagId("Vertex"));
        drawSettings.SetShaderPassName(4, new ShaderTagId("VertexLMRGBM"));
        drawSettings.SetShaderPassName(5, new ShaderTagId("VertexLM"));
        drawSettings.overrideMaterial = errorMaterial;
        
        var filterSettings = new FilteringSettings(RenderQueueRange.all);

        context.DrawRenderers(cullResults, ref drawSettings, ref filterSettings);
    }
}
