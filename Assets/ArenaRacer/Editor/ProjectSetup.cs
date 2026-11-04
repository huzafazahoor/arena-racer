using ArenaRacer;
using Fusion;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

/// <summary>
/// Generates the game's scene, prefabs, materials and project settings from code, so the whole project can
/// be rebuilt from a clean checkout. Menu: Arena Racer > Rebuild Scene and Prefabs.
/// Batch: Unity.exe -batchmode -projectPath . -executeMethod ProjectSetup.RunBatch -quit
/// </summary>
public static class ProjectSetup
{
    const string Root = "Assets/ArenaRacer";
    const string ScenePath = Root + "/Scenes/Arena.unity";

    [MenuItem("Arena Racer/Rebuild Scene and Prefabs")]
    public static void Run()
    {
        EnsureFolder(Root + "/Materials");
        EnsureFolder(Root + "/Prefabs");
        EnsureFolder(Root + "/Scenes");

        ConfigurePlayer();
        ConfigureQuality();
        Materials materials = CreateMaterials();
        NetworkObject car = CreateCarPrefab();
        NetworkObject director = CreateDirectorPrefab(car);
        CreateScene(director, materials);
        AssetDatabase.SaveAssets();
        Debug.Log("ProjectSetup: scene, prefabs and settings generated.");
    }

    public static void RunBatch()
    {
        Run();
        EditorApplication.Exit(0);
    }

    struct Materials
    {
        public Material Lit, Ghost, Line, Smoke, Glow, Flame, SkidMark, Skybox;
    }

    static void ConfigurePlayer()
    {
        PlayerSettings.companyName = "Huzafa Zahoor";
        PlayerSettings.productName = "Arena Racer";
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.runInBackground = true;
        PlayerSettings.stripEngineCode = true;
        PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.WebGL, "FUSION_WEAVER;FUSION2");
        PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone, "FUSION_WEAVER;FUSION2");
        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Minimal);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { GraphicsDeviceType.OpenGLES3 });  // WebGL 2 (needed for linear colour)
    }

    static void ConfigureQuality()
    {
        // One sensible level everywhere: soft shadows over 90 m, 4x MSAA, one per-pixel light.
        var asset = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
        SerializedProperty levels = asset.FindProperty("m_QualitySettings");
        for (int i = 0; i < levels.arraySize; i++)
        {
            SerializedProperty level = levels.GetArrayElementAtIndex(i);
            level.FindPropertyRelative("shadows").intValue = (int)ShadowQuality.All;
            level.FindPropertyRelative("shadowResolution").intValue = (int)ShadowResolution.Medium;
            level.FindPropertyRelative("shadowDistance").floatValue = 90f;
            level.FindPropertyRelative("shadowCascades").intValue = 1;
            level.FindPropertyRelative("antiAliasing").intValue = 4;
            level.FindPropertyRelative("pixelLightCount").intValue = 1;
            level.FindPropertyRelative("softParticles").boolValue = false;
            level.FindPropertyRelative("realtimeReflectionProbes").boolValue = false;
            level.FindPropertyRelative("vSyncCount").intValue = 1;
        }
        asset.ApplyModifiedPropertiesWithoutUndo();
    }

    static Materials CreateMaterials()
    {
        var m = new Materials
        {
            Lit = MaterialAsset("Lit", "ArenaRacer/Lit"),
            Ghost = MaterialAsset("Ghost", "ArenaRacer/Ghost"),
            Line = MaterialAsset("Line", "ArenaRacer/Line"),
            Smoke = MaterialAsset("Smoke", "ArenaRacer/Particle"),
            Glow = MaterialAsset("Glow", "ArenaRacer/Particle"),
            Flame = MaterialAsset("Flame", "ArenaRacer/Ghost"),
            SkidMark = MaterialAsset("Skid Mark", "ArenaRacer/Line"),
            Skybox = MaterialAsset("Sky", "Skybox/Procedural"),
        };
        m.Smoke.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        m.Glow.SetFloat("_DstBlend", (float)BlendMode.One);
        m.Flame.SetColor("_Color", new Color(1f, 0.55f, 0.15f, 0.95f));
        m.Skybox.SetFloat("_SunSize", 0.035f);
        m.Skybox.SetFloat("_AtmosphereThickness", 0.75f);
        m.Skybox.SetColor("_SkyTint", new Color(0.45f, 0.52f, 0.66f));
        m.Skybox.SetColor("_GroundColor", new Color(0.32f, 0.36f, 0.34f));
        m.Skybox.SetFloat("_Exposure", 1.15f);
        foreach (Material material in new[] { m.Smoke, m.Glow, m.Flame, m.Skybox }) EditorUtility.SetDirty(material);
        return m;
    }

    static Material MaterialAsset(string name, string shaderName)
    {
        string path = $"{Root}/Materials/{name}.mat";
        Shader shader = Shader.Find(shaderName);
        if (shader == null) throw new System.Exception($"Shader not found: {shaderName}");
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        material.shader = shader;
        return material;
    }

    static NetworkObject CreateCarPrefab()
    {
        var go = new GameObject("Car");
        var networkObject = go.AddComponent<NetworkObject>();
        go.AddComponent<NetCar>();
        go.AddComponent<CarView>();
        // Cars survive their owner leaving (a bot takes over), and any client may take State Authority of one.
        networkObject.Flags = (networkObject.Flags.SetCurrentVersion() | NetworkObjectFlags.AllowStateAuthorityOverride)
                              & ~NetworkObjectFlags.DestroyWhenStateAuthorityLeaves
                              & ~NetworkObjectFlags.MasterClientObject;
        return SavePrefab(go, $"{Root}/Prefabs/Car.prefab");
    }

    static NetworkObject CreateDirectorPrefab(NetworkObject carPrefab)
    {
        var go = new GameObject("Race Director");
        var networkObject = go.AddComponent<NetworkObject>();
        var director = go.AddComponent<RaceDirector>();
        // State Authority always follows the shared-mode master client.
        networkObject.Flags = (networkObject.Flags.SetCurrentVersion() | NetworkObjectFlags.MasterClientObject)
                              & ~NetworkObjectFlags.DestroyWhenStateAuthorityLeaves;
        var serialized = new SerializedObject(director);
        serialized.FindProperty("carPrefab").objectReferenceValue = carPrefab;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        return SavePrefab(go, $"{Root}/Prefabs/Race Director.prefab");
    }

    static NetworkObject SavePrefab(GameObject go, string path)
    {
        PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        // Fusion re-bakes network prefabs on import, which invalidates the object SaveAsPrefabAsset returned.
        return AssetDatabase.LoadAssetAtPath<NetworkObject>(path);
    }

    static void CreateScene(NetworkObject director, Materials m)
    {
        string directorPath = AssetDatabase.GetAssetPath(director);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
        var camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.fieldOfView = 62f;
        camera.nearClipPlane = 0.3f;
        camera.farClipPlane = 900f;
        cameraObject.AddComponent<AudioListener>();
        cameraObject.AddComponent<ChaseCamera>();
        cameraObject.transform.position = new Vector3(0f, 80f, -170f);

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.transform.rotation = Quaternion.Euler(42f, -38f, 0f);
        sun.color = new Color(1f, 0.95f, 0.86f);
        sun.intensity = 1.25f;
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.7f;
        sun.shadowBias = 0.06f;
        sun.shadowNormalBias = 0.3f;

        var arena = new GameObject("Arena").AddComponent<TrackBuilder>();
        SetField(arena, "litMaterial", m.Lit);

        var game = new GameObject("Game");
        var assets = game.AddComponent<GameAssets>();
        assets.Lit = m.Lit;
        assets.Ghost = m.Ghost;
        assets.Line = m.Line;
        assets.Smoke = m.Smoke;
        assets.Glow = m.Glow;
        assets.Flame = m.Flame;
        assets.SkidMark = m.SkidMark;
        assets.Rounded = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        assets.Engine = Clip("engine");
        assets.Skid = Clip("skid");
        assets.Boost = Clip("boost");
        assets.Bump = Clip("bump");
        assets.Beep = Clip("beep");
        assets.Go = Clip("go");
        assets.Lap = Clip("lap");
        assets.Finish = Clip("finish");
        assets.Click = Clip("click");
        game.AddComponent<Sfx>();
        var session = game.AddComponent<GameSession>();
        // Load by path: NewScene() above unloads assets nothing references yet, which kills earlier handles.
        SetField(session, "directorPrefab", AssetDatabase.LoadAssetAtPath<NetworkObject>(directorPath));
        game.AddComponent<RaceHud>();
        game.AddComponent<DevView>();
        new GameObject("Skid Marks").AddComponent<SkidMarks>().transform.SetParent(game.transform);

        var events = new GameObject("EventSystem");
        events.AddComponent<EventSystem>();
        events.AddComponent<StandaloneInputModule>();

        RenderSettings.skybox = m.Skybox;
        RenderSettings.sun = sun;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.58f, 0.64f, 0.74f);
        RenderSettings.ambientEquatorColor = new Color(0.45f, 0.48f, 0.5f);
        RenderSettings.ambientGroundColor = new Color(0.25f, 0.27f, 0.25f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogStartDistance = 180f;
        RenderSettings.fogEndDistance = 620f;
        RenderSettings.fogColor = new Color(0.62f, 0.68f, 0.76f);
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;

        if (new SerializedObject(session).FindProperty("directorPrefab").objectReferenceValue == null)
            throw new System.Exception("ProjectSetup: GameSession.directorPrefab is not wired");
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
    }

    static AudioClip Clip(string name)
    {
        string path = $"{Root}/Audio/{name}.wav";
        var importer = (AudioImporter)AssetImporter.GetAtPath(path);
        AudioImporterSampleSettings settings = importer.defaultSampleSettings;
        settings.loadType = AudioClipLoadType.DecompressOnLoad;
        settings.compressionFormat = AudioCompressionFormat.Vorbis;
        settings.quality = 0.6f;
        importer.defaultSampleSettings = settings;
        importer.forceToMono = true;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
    }

    static void SetField(Object target, string field, Object value)
    {
        var serialized = new SerializedObject(target);
        serialized.FindProperty(field).objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        EnsureFolder(path.Substring(0, slash));
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }
}
