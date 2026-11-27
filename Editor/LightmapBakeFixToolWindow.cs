#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 光照烘焙一键诊断与修复工具
/// 适用场景：解决 Unity Progressive GPU 报错
/// "Assertion failed on expression: 'openCLRenderBuffers.HasBakingBuffers()'"、
/// 进度长期卡在 0%、OpenCL 显存充足却烘焙失败、以及外景/无 UV2 物体误开 Contribute GI 等问题。
/// 使用方法：将本文件放入 Unity 项目的 Assets/Editor/ 目录下，在顶部菜单打开「工具 -> 光照烘焙一键诊断与修复」。
/// </summary>
public class LightmapBakeFixToolWindow : EditorWindow
{
    private class GiMeshItem
    {
        public GameObject Go;
        public string HierarchyPath;
        public bool HasUv2;
        public bool RendererEnabled;
        public float MaxDimension;
        public float ScaleInLightmap;
        public string Reason;
    }

    private static readonly string[] BackendOptions = { "Enlighten (已弃用)", "Progressive CPU (稳定推荐)", "Progressive GPU (OpenCL)" };
    private static readonly string[] DenoiserOptions = { "无 (None)", "Optix (仅 NVIDIA)", "OpenImageDenoise (CPU/通用推荐)", "RadeonPro (易在新A卡失效)" };

    private Vector2 _scrollPos;
    private float _largeThresholdMeters = 20f;
    private float _targetResolution = 25f;
    private int _targetMaxAtlasSize = 1024;
    private bool _fixDisableRealtimeGi = true;
    private bool _fixSwitchToCpu = true;
    private bool _fixCleanInvalidGi = true;
    private bool _fixDistanceShadowmask = true;
    private bool _skipIndoorByName = true;

    private int _selectedBackend = 1;
    private int _selectedDenoiser = 2;

    private readonly List<GiMeshItem> _problemItems = new List<GiMeshItem>();
    private int _totalContributeGiCount;
    private int _noUv2Count;
    private int _disabledRendererCount;
    private int _oversizedCount;
    private string _diagnosticSummary = "点击「重新扫描诊断」查看当前场景光照烘焙状态。";
    private string _lastActionMessage = "";

    [MenuItem("工具/光照烘焙一键诊断与修复")]
    public static void ShowWindow()
    {
        var window = GetWindow<LightmapBakeFixToolWindow>("光照烘焙诊断与修复");
        window.minSize = new Vector2(560f, 640f);
        window.RunDiagnostic();
    }

    private void OnEnable()
    {
        RunDiagnostic();
    }

    private void OnGUI()
    {
        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Unity 光照烘焙一键诊断与修复工具", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "专治显卡资源充足但烘焙仍报 openCLRenderBuffers.HasBakingBuffers()、烘焙卡在 0%、或大场景烘焙极慢的问题。",
            MessageType.Info);

        DrawStatusSection();
        EditorGUILayout.Space(8);
        DrawOneClickFixSection();
        EditorGUILayout.Space(8);
        DrawManualControlSection();
        EditorGUILayout.Space(8);
        DrawProblemListSection();

        if (!string.IsNullOrEmpty(_lastActionMessage))
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(_lastActionMessage, MessageType.None);
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawStatusSection()
    {
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("1. 当前环境与问题诊断", EditorStyles.boldLabel);
        if (GUILayout.Button("重新扫描诊断", GUILayout.Width(110)))
        {
            RunDiagnostic();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.TextArea(_diagnosticSummary, EditorStyles.wordWrappedLabel);

        if (Lightmapping.isRunning)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                $"当前正在烘焙中，进度：{Lightmapping.buildProgress * 100f:F1}% 。如果长期停在 0% 或报错，请先点击下方「停止当前烘焙」。",
                MessageType.Warning);
        }
        EditorGUILayout.EndVertical();
    }

    private void DrawOneClickFixSection()
    {
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("2. 一键稳定修复（推荐）", EditorStyles.boldLabel);

        _fixSwitchToCpu = EditorGUILayout.ToggleLeft("切换为 Progressive CPU + OpenImageDenoise 降噪（规避 OpenCL 兼容异常）", _fixSwitchToCpu);
        _fixDisableRealtimeGi = EditorGUILayout.ToggleLeft("关闭冲突的 Realtime GI（避免与 Baked GI 抢管线及重建缓冲）", _fixDisableRealtimeGi);
        _fixDistanceShadowmask = EditorGUILayout.ToggleLeft("Shadowmask 改为 DistanceShadowmask（确保静态房子能给动态地面投射实时投影）", _fixDistanceShadowmask);
        _fixCleanInvalidGi = EditorGUILayout.ToggleLeft("清理异常物体的 Contribute GI（无 UV2 外景 / 已禁用渲染器 / 超大网格）", _fixCleanInvalidGi);

        EditorGUI.indentLevel++;
        _skipIndoorByName = EditorGUILayout.ToggleLeft("保护室内建筑与家具（跳过名称含 House/Floor/Wall/Ceiling/Furniture 的无 UV2 物体）", _skipIndoorByName);
        _largeThresholdMeters = EditorGUILayout.Slider("超大物体尺寸阈值 (米)", _largeThresholdMeters, 10f, 100f);
        _targetResolution = EditorGUILayout.Slider("目标烘焙分辨率 (Texels/Unit)", _targetResolution, 8f, 60f);
        _targetMaxAtlasSize = EditorGUILayout.IntPopup("最大光照贴图图集尺寸", _targetMaxAtlasSize,
            new[] { "512", "1024", "2048", "4096" }, new[] { 512, 1024, 2048, 4096 });
        EditorGUI.indentLevel--;

        EditorGUILayout.Space(6);
        EditorGUILayout.BeginHorizontal();
        GUI.backgroundColor = new Color(0.55f, 0.90f, 0.65f);
        if (GUILayout.Button("一键应用修复并开始烘焙", GUILayout.Height(34)))
        {
            ApplyRecommendedFix(startBakeAfterFix: true);
        }
        GUI.backgroundColor = new Color(0.75f, 0.88f, 1f);
        if (GUILayout.Button("仅应用修复（暂不烘焙）", GUILayout.Height(34)))
        {
            ApplyRecommendedFix(startBakeAfterFix: false);
        }
        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
    }

    private void DrawManualControlSection()
    {
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("3. 快捷手动控制", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        _selectedBackend = EditorGUILayout.Popup("烘焙器后端", _selectedBackend, BackendOptions);
        if (GUILayout.Button("应用后端", GUILayout.Width(90)))
        {
            ApplyBackendAndDenoiser(_selectedBackend, _selectedDenoiser);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        _selectedDenoiser = EditorGUILayout.Popup("降噪器 (Direct/Indirect/AO)", _selectedDenoiser, DenoiserOptions);
        if (GUILayout.Button("同步降噪器", GUILayout.Width(90)))
        {
            ApplyBackendAndDenoiser(_selectedBackend, _selectedDenoiser);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("停止当前烘焙", GUILayout.Height(26)))
        {
            Lightmapping.Cancel();
            _lastActionMessage = "已请求停止当前光照烘焙任务。";
            RunDiagnostic();
        }
        if (GUILayout.Button("清理 GI 磁盘缓存", GUILayout.Height(26)))
        {
            Lightmapping.ClearDiskCache();
            _lastActionMessage = "已清理 GI Cache 磁盘缓存。";
            RunDiagnostic();
        }
        if (GUILayout.Button("立即异步烘焙", GUILayout.Height(26)))
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("提示", "请先退出运行模式（Play Mode）再开始烘焙。", "确定");
            }
            else
            {
                bool ok = Lightmapping.BakeAsync();
                _lastActionMessage = ok ? "已启动异步光照烘焙。" : "启动烘焙失败，请检查是否有其他未完成的任务或先停止当前烘焙。";
                RunDiagnostic();
            }
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
    }

    private void DrawProblemListSection()
    {
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField($"4. 检测到可疑 Contribute GI 物体 ({_problemItems.Count} 个)", EditorStyles.boldLabel);
        if (_problemItems.Count > 0 && GUILayout.Button("仅清理下方列表 GI 标记", GUILayout.Width(175)))
        {
            int cleaned = ClearProblemGiFlags();
            _lastActionMessage = $"已关闭 {cleaned} 个物体的 Contribute GI 标记（保留静态合批与遮挡剔除标记）。";
            RunDiagnostic();
        }
        EditorGUILayout.EndHorizontal();

        if (_problemItems.Count == 0)
        {
            EditorGUILayout.LabelField("未发现明显的异常 Contribute GI 物体。", EditorStyles.miniLabel);
        }
        else
        {
            int showMax = Math.Min(_problemItems.Count, 80);
            for (int i = 0; i < showMax; i++)
            {
                var item = _problemItems[i];
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("定位", GUILayout.Width(44)))
                {
                    Selection.activeGameObject = item.Go;
                    EditorGUIUtility.PingObject(item.Go);
                }
                EditorGUILayout.LabelField($"{item.HierarchyPath}  [{item.Reason} | 最大边 {item.MaxDimension:F1}m | UV2={(item.HasUv2 ? "有" : "无")}]", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }
            if (_problemItems.Count > showMax)
            {
                EditorGUILayout.LabelField($"... 其余 {_problemItems.Count - showMax} 项已省略显示。", EditorStyles.miniLabel);
            }
        }
        EditorGUILayout.EndVertical();
    }

    private void RunDiagnostic()
    {
        _problemItems.Clear();
        _totalContributeGiCount = 0;
        _noUv2Count = 0;
        _disabledRendererCount = 0;
        _oversizedCount = 0;

        var ls = Lightmapping.lightingSettings;
        var issues = new List<string>();
        var info = new StringBuilder();

        info.AppendLine($"显卡: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize} MB) | CPU: {SystemInfo.processorCount} 线程 | 内存: {SystemInfo.systemMemorySize} MB");

        if (ls == null)
        {
            issues.Add("当前场景未绑定 LightingSettings 资产（使用默认临时配置）。");
        }
        else
        {
            var so = new SerializedObject(ls);
            int backend = GetIntProp(so, "m_BakeBackend", (int)ls.lightmapper);
            int dDirect = GetIntProp(so, "m_PVRDenoiserTypeDirect", -1);
            int dIndirect = GetIntProp(so, "m_PVRDenoiserTypeIndirect", -1);
            int dAo = GetIntProp(so, "m_PVRDenoiserTypeAO", -1);
            bool bakedGi = GetBoolProp(so, "m_EnableBakedLightmaps", ls.bakedGI);
            bool realtimeGi = GetBoolProp(so, "m_EnableRealtimeLightmaps", ls.realtimeGI);
            float res = GetFloatProp(so, "m_BakeResolution", ls.lightmapResolution);
            int maxSize = GetIntProp(so, "m_LightmapMaxSize", ls.lightmapMaxSize);

            _selectedBackend = Mathf.Clamp(backend, 0, BackendOptions.Length - 1);
            _selectedDenoiser = Mathf.Clamp(dDirect >= 0 ? dDirect : 2, 0, DenoiserOptions.Length - 1);

            info.AppendLine($"配置文件: {ls.name} | 后端: {SafeOption(BackendOptions, backend)} | 分辨率: {res} | 最大图集: {maxSize}");
            info.AppendLine($"Baked GI: {(bakedGi ? "开" : "关")} | Realtime GI: {(realtimeGi ? "开" : "关")} | 降噪器(Direct/Indirect/AO): {dDirect}/{dIndirect}/{dAo}");

            if (backend == 2)
            {
                issues.Add("当前使用 Progressive GPU (OpenCL)，在部分 AMD/新架构驱动下容易出现 HasBakingBuffers() 断言并卡在 0%。");
            }
            if (dDirect == 3 || dIndirect == 3 || dAo == 3)
            {
                issues.Add("降噪器值为 3 (RadeonPro)，在新显卡 OpenCL 环境下极易初始化失败。");
            }
            if (dDirect == 1 && !SystemInfo.graphicsDeviceVendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add("当前非 NVIDIA 显卡却选择了 Optix (1) 降噪器。");
            }
            if (bakedGi && realtimeGi)
            {
                issues.Add("同时开启了 Baked GI 和 Realtime GI，编辑资源或刷新时易触发缓冲区重建报错。");
            }
            if (res > 35f)
            {
                issues.Add($"烘焙分辨率偏高 ({res})，在大场景下会大幅拉长烘焙耗时与显存/内存开销。");
            }

            var lpProp = so.FindProperty("m_LightmapParameters");
            if (lpProp != null && lpProp.objectReferenceValue != null)
            {
                var lpSo = new SerializedObject(lpProp.objectReferenceValue);
                int aa = GetIntProp(lpSo, "antiAliasingSamples", 0);
                int aoQ = GetIntProp(lpSo, "AOQuality", 0);
                int irrQ = GetIntProp(lpSo, "irradianceQuality", 0);
                info.AppendLine($"LightmapParameters: {lpProp.objectReferenceValue.name} (Irradiance={irrQ}, AOQuality={aoQ}, AA={aa})");
                if (aa >= 8 || aoQ >= 256 || irrQ >= 8192)
                {
                    issues.Add($"自定义 LightmapParameters ({lpProp.objectReferenceValue.name}) 采样极高（AA={aa}, AO={aoQ}），CPU 烘焙时间会较长。");
                }
            }
        }

        foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if (!mr.gameObject.activeInHierarchy) continue;
            var flags = GameObjectUtility.GetStaticEditorFlags(mr.gameObject);
            if ((flags & StaticEditorFlags.ContributeGI) == 0) continue;

            _totalContributeGiCount++;
            var mf = mr.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            bool hasUv2 = mesh != null && mesh.uv2 != null && mesh.uv2.Length > 0;
            bool renEnabled = mr.enabled;
            var b = mr.bounds.size;
            float maxDim = Mathf.Max(b.x, Mathf.Max(b.y, b.z));

            if (!hasUv2) _noUv2Count++;
            if (!renEnabled) _disabledRendererCount++;
            if (maxDim > _largeThresholdMeters) _oversizedCount++;

            string path = GetHierarchyPath(mr.transform);
            bool isProtectedIndoor = _skipIndoorByName && IsProtectedIndoor(path);

            if (!renEnabled || (!hasUv2 && !isProtectedIndoor) || (maxDim > _largeThresholdMeters && !isProtectedIndoor))
            {
                var reasons = new List<string>();
                if (!renEnabled) reasons.Add("渲染器已禁用");
                if (!hasUv2) reasons.Add("无UV2");
                if (maxDim > _largeThresholdMeters) reasons.Add("超大尺寸");

                _problemItems.Add(new GiMeshItem
                {
                    Go = mr.gameObject,
                    HierarchyPath = path,
                    HasUv2 = hasUv2,
                    RendererEnabled = renEnabled,
                    MaxDimension = maxDim,
                    ScaleInLightmap = mr.scaleInLightmap,
                    Reason = string.Join("+", reasons)
                });
            }
        }

        info.AppendLine($"激活 Contribute GI 物体总数: {_totalContributeGiCount}（无 UV2: {_noUv2Count} | 超大尺寸: {_oversizedCount} | 禁用渲染器: {_disabledRendererCount}）");
        info.AppendLine($"Shadowmask 模式: {QualitySettings.shadowmaskMode} | 当前已加载光照贴图数量: {(LightmapSettings.lightmaps != null ? LightmapSettings.lightmaps.Length : 0)}");

        if (QualitySettings.shadowmaskMode == ShadowmaskMode.Shadowmask)
        {
            issues.Add("当前 QualitySettings 为 Shadowmask 模式：已烘焙静态物体不会给未烘焙的动态地面投射实时阴影，建议改为 DistanceShadowmask。");
        }

        if (issues.Count > 0)
        {
            info.AppendLine("\n[诊断发现的风险点]");
            for (int i = 0; i < issues.Count; i++)
            {
                info.AppendLine($"• {issues[i]}");
            }
        }
        else
        {
            info.AppendLine("\n[诊断结果] 当前光照烘焙参数配置健康。");
        }

        _diagnosticSummary = info.ToString().TrimEnd();
    }

    private void ApplyRecommendedFix(bool startBakeAfterFix)
    {
        if (EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog("提示", "请先退出运行模式（Play Mode）后再执行修复。", "确定");
            return;
        }

        if (Lightmapping.isRunning)
        {
            Lightmapping.Cancel();
        }

        int cleaned = 0;
        if (_fixCleanInvalidGi)
        {
            cleaned = ClearProblemGiFlags();
        }

        var ls = EnsureLightingSettingsAsset();
        Undo.RecordObject(ls, "修复光照烘焙设置");
        var so = new SerializedObject(ls);

        if (_fixSwitchToCpu)
        {
            SetIntProp(so, "m_BakeBackend", 1); // Progressive CPU
            SetIntProp(so, "m_PVRDenoiserTypeDirect", 2);   // OpenImageDenoise
            SetIntProp(so, "m_PVRDenoiserTypeIndirect", 2); // OpenImageDenoise
            SetIntProp(so, "m_PVRDenoiserTypeAO", 2);       // OpenImageDenoise
        }

        SetBoolProp(so, "m_EnableBakedLightmaps", true);
        if (_fixDisableRealtimeGi)
        {
            SetBoolProp(so, "m_EnableRealtimeLightmaps", false);
        }

        SetFloatProp(so, "m_BakeResolution", _targetResolution);
        SetIntProp(so, "m_LightmapMaxSize", _targetMaxAtlasSize);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(ls);

        if (_fixDistanceShadowmask)
        {
            QualitySettings.shadowmaskMode = ShadowmaskMode.DistanceShadowmask;
        }

        AssetDatabase.SaveAssets();

        Lightmapping.ClearDiskCache();

        if (startBakeAfterFix)
        {
            bool started = Lightmapping.BakeAsync();
            _lastActionMessage = $"修复完成：已清理 {cleaned} 个异常 GI 物体，已切换稳定烘焙设置并清理缓存。启动异步烘焙结果: {started}";
        }
        else
        {
            _lastActionMessage = $"修复完成：已清理 {cleaned} 个异常 GI 物体，已更新烘焙设置并清理 GI 缓存。";
        }

        RunDiagnostic();
    }

    private void ApplyBackendAndDenoiser(int backendIndex, int denoiserIndex)
    {
        if (Lightmapping.isRunning)
        {
            Lightmapping.Cancel();
        }
        var ls = EnsureLightingSettingsAsset();
        Undo.RecordObject(ls, "更改光照烘焙器与降噪器");
        var so = new SerializedObject(ls);
        SetIntProp(so, "m_BakeBackend", backendIndex);
        SetIntProp(so, "m_PVRDenoiserTypeDirect", denoiserIndex);
        SetIntProp(so, "m_PVRDenoiserTypeIndirect", denoiserIndex);
        SetIntProp(so, "m_PVRDenoiserTypeAO", denoiserIndex);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(ls);
        AssetDatabase.SaveAssets();

        _lastActionMessage = $"已更新为：{SafeOption(BackendOptions, backendIndex)} / {SafeOption(DenoiserOptions, denoiserIndex)}。";
        RunDiagnostic();
    }

    private int ClearProblemGiFlags()
    {
        int count = 0;
        foreach (var item in _problemItems)
        {
            if (item.Go == null) continue;
            var flags = GameObjectUtility.GetStaticEditorFlags(item.Go);
            if ((flags & StaticEditorFlags.ContributeGI) != 0)
            {
                Undo.RecordObject(item.Go, "关闭异常物体的 Contribute GI");
                GameObjectUtility.SetStaticEditorFlags(item.Go, flags & ~StaticEditorFlags.ContributeGI);
                EditorUtility.SetDirty(item.Go);
                count++;
            }
        }
        return count;
    }

    private static LightingSettings EnsureLightingSettingsAsset()
    {
        if (Lightmapping.lightingSettings != null)
        {
            return Lightmapping.lightingSettings;
        }

        var ls = new LightingSettings { name = "FixedLightingSettings" };
        string assetPath = "Assets/FixedLightingSettings.lighting";
        assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
        AssetDatabase.CreateAsset(ls, assetPath);
        AssetDatabase.SaveAssets();
        Lightmapping.lightingSettings = ls;
        return ls;
    }

    private static bool IsProtectedIndoor(string path)
    {
        string lower = path.ToLowerInvariant();
        return lower.Contains("house/")
            || lower.Contains("3d model")
            || lower.Contains("floor")
            || lower.Contains("wall")
            || lower.Contains("ceiling")
            || lower.Contains("pillar")
            || lower.Contains("frame")
            || lower.Contains("bed")
            || lower.Contains("coffee");
    }

    private static string GetHierarchyPath(Transform t)
    {
        var names = new List<string>();
        while (t != null)
        {
            names.Add(t.name);
            t = t.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private static string SafeOption(string[] options, int idx)
    {
        return (idx >= 0 && idx < options.Length) ? options[idx] : $"未知({idx})";
    }

    private static int GetIntProp(SerializedObject so, string name, int fallback)
    {
        var p = so.FindProperty(name);
        return p != null ? p.intValue : fallback;
    }

    private static float GetFloatProp(SerializedObject so, string name, float fallback)
    {
        var p = so.FindProperty(name);
        return p != null ? p.floatValue : fallback;
    }

    private static bool GetBoolProp(SerializedObject so, string name, bool fallback)
    {
        var p = so.FindProperty(name);
        return p != null ? p.boolValue : fallback;
    }

    private static void SetIntProp(SerializedObject so, string name, int val)
    {
        var p = so.FindProperty(name);
        if (p != null) p.intValue = val;
    }

    private static void SetFloatProp(SerializedObject so, string name, float val)
    {
        var p = so.FindProperty(name);
        if (p != null) p.floatValue = val;
    }

    private static void SetBoolProp(SerializedObject so, string name, bool val)
    {
        var p = so.FindProperty(name);
        if (p != null) p.boolValue = val;
    }
}
#endif
