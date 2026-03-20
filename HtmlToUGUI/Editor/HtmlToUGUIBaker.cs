using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Editor.UIBaker
{
    [System.Serializable]
    public class UIDataNode
    {
        public string name;
        public string type;
        public string spriteName;
        public string spritePath;
        public string dir;
        public float value;
        public bool isChecked;
        public List<string> options;
        public float x;
        public float y;
        public float width;
        public float height;
        public string color;
        public string fontColor;
        public int fontSize;
        public string textAlign;
        public string text;
        public List<UIDataNode> children;
    }

    public class HtmlToUGUIBaker : EditorWindow
    {
        private enum InputMode
        {
            FileAsset,
            RawString
        }

        private const string PREFS_URL_KEY = "HtmlToUGUIBaker_ConverterUrl";
        private const string PREFS_CONFIG_PATH_KEY = "HtmlToUGUIBaker_ConfigPath";
        private const string PREFS_RES_INDEX_KEY = "HtmlToUGUIBaker_ResIndex";
        private const string PREFS_SPRITE_FOLDER_KEY = "HtmlToUGUIBaker_SpriteFolderPath";

        private InputMode currentMode = InputMode.FileAsset;
        private TextAsset jsonAsset;
        private string rawJsonString = "";
        private Vector2 scrollPosition;
        private Canvas targetCanvas;

        private string converterUrl = "";
        private HtmlToUGUIConfig config;
        private int selectedResolutionIndex = 0;
        private SerializedObject configSO;
        private SerializedProperty resolutionsProp;

        private DefaultAsset spriteFolderAsset;
        private string spriteFolderPath = "";
        private readonly Dictionary<string, Sprite> spriteIndex = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> spriteNameList = new List<string>();
        private readonly List<string> spriteBindingFailures = new List<string>();
        private readonly List<SpritePrecheckItem> precheckItems = new List<SpritePrecheckItem>();
        private string precheckSummary = "尚未执行预检。";
        private Vector2 precheckScroll;

        [Serializable]
        private class SpritePrecheckItem
        {
            public string nodeName;
            public string nodeType;
            public string spriteName;
            public string spritePath;
            public string matchedKey;
            public string assetPath;
            public bool success;
            public string failCategory;
            public string note;
        }

        [MenuItem("Tools/UI Architecture/HTML to UGUI Baker (Full Controls)")]
        public static void ShowWindow()
        {
            GetWindow<HtmlToUGUIBaker>("UI 原型烘焙器");
        }

        private void OnEnable()
        {
            converterUrl = EditorPrefs.GetString(PREFS_URL_KEY, "");

            string configPath = EditorPrefs.GetString(PREFS_CONFIG_PATH_KEY, "");
            if (!string.IsNullOrEmpty(configPath))
            {
                config = AssetDatabase.LoadAssetAtPath<HtmlToUGUIConfig>(configPath);
            }

            spriteFolderPath = EditorPrefs.GetString(PREFS_SPRITE_FOLDER_KEY, "");
            if (!string.IsNullOrEmpty(spriteFolderPath))
            {
                spriteFolderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(spriteFolderPath);
                RebuildSpriteIndex();
            }

            selectedResolutionIndex = EditorPrefs.GetInt(PREFS_RES_INDEX_KEY, 0);
        }

        private void OnGUI()
        {
            GUILayout.Label("基于坐标烘焙的 UI 原型生成工具 (支持多分辨率与全控件)", EditorStyles.boldLabel);
            GUILayout.Space(10);

            DrawConfigUI();
            DrawExternalToolchainUI();
            DrawSpriteFolderUI();

            if (GUILayout.Button("打开效果图识别 -> HTML", GUILayout.Height(28)))
            {
                EffectImageToHtmlMultiWindow.ShowWindow();
            }

            targetCanvas = (Canvas)EditorGUILayout.ObjectField("目标 Canvas", targetCanvas, typeof(Canvas), true);
            GUILayout.Space(10);

            currentMode = (InputMode)GUILayout.Toolbar((int)currentMode, new string[] { "读取 JSON 文件", "直接粘贴 JSON 字符" });
            GUILayout.Space(10);

            if (currentMode == InputMode.FileAsset)
            {
                DrawFileModeUI();
            }
            else
            {
                DrawStringModeUI();
            }

            GUILayout.Space(20);
            DrawSpritePrecheckUI();
            GUILayout.Space(10);
            if (GUILayout.Button("烘焙前预检", GUILayout.Height(36)))
            {
                RunSpritePrecheck();
            }

            GUI.backgroundColor = new Color(0.2f, 0.8f, 0.2f);
            if (GUILayout.Button("执行烘焙生成", GUILayout.Height(40)))
            {
                ExecuteBake();
            }

            GUI.backgroundColor = Color.white;
        }

        private void DrawConfigUI()
        {
            GUILayout.Label("多分辨率与 DSL 配置", EditorStyles.label);
            GUILayout.BeginVertical("box");

            EditorGUI.BeginChangeCheck();
            config = (HtmlToUGUIConfig)EditorGUILayout.ObjectField("配置文件 (SO)", config, typeof(HtmlToUGUIConfig), false);
            if (EditorGUI.EndChangeCheck())
            {
                string path = config != null ? AssetDatabase.GetAssetPath(config) : "";
                EditorPrefs.SetString(PREFS_CONFIG_PATH_KEY, path);
                selectedResolutionIndex = 0;
                EditorPrefs.SetInt(PREFS_RES_INDEX_KEY, selectedResolutionIndex);
                configSO = null;
            }

            if (config == null)
            {
                EditorGUILayout.HelpBox("请先创建并分配 HtmlToUGUIConfig 配置文件。\n(右键 Project 窗口 -> Create -> UI Architecture -> HtmlToUGUI Config)", MessageType.Warning);
                GUILayout.EndVertical();
                GUILayout.Space(10);
                return;
            }

            if (configSO == null || configSO.targetObject != config)
            {
                configSO = new SerializedObject(config);
                resolutionsProp = configSO.FindProperty("supportedResolutions");
            }

            configSO.Update();
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(resolutionsProp, new GUIContent("分辨率预设列表 (可自由增删)"), true);
            if (EditorGUI.EndChangeCheck())
            {
                configSO.ApplyModifiedProperties();
            }

            if (config.supportedResolutions == null || config.supportedResolutions.Count == 0)
            {
                EditorGUILayout.HelpBox("配置文件中未定义任何分辨率数据，请点击上方列表的 '+' 号添加。", MessageType.Error);
                GUILayout.EndVertical();
                GUILayout.Space(10);
                return;
            }

            selectedResolutionIndex = Mathf.Clamp(selectedResolutionIndex, 0, config.supportedResolutions.Count - 1);
            string[] resNames = new string[config.supportedResolutions.Count];
            for (int i = 0; i < config.supportedResolutions.Count; i++)
            {
                resNames[i] = config.supportedResolutions[i].displayName;
            }

            EditorGUI.BeginChangeCheck();
            selectedResolutionIndex = EditorGUILayout.Popup("目标分辨率", selectedResolutionIndex, resNames);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetInt(PREFS_RES_INDEX_KEY, selectedResolutionIndex);
            }

            if (GUILayout.Button("复制对应分辨率的 DSL 规范文档", GUILayout.Height(25)))
            {
                CopyDSLToClipboard();
            }

            GUILayout.EndVertical();
            GUILayout.Space(10);
        }

        private void DrawExternalToolchainUI()
        {
            GUILayout.Label("外部工具链桥接", EditorStyles.label);
            GUILayout.BeginVertical("box");

            EditorGUI.BeginChangeCheck();
            converterUrl = EditorGUILayout.TextField("HTML 转换器地址", converterUrl);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString(PREFS_URL_KEY, converterUrl);
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("浏览...", GUILayout.Width(80)))
            {
                string path = EditorUtility.OpenFilePanel("选择 HTML 转换器", "", "html");
                if (!string.IsNullOrEmpty(path))
                {
                    converterUrl = "file:///" + path.Replace("\\", "/");
                    EditorPrefs.SetString(PREFS_URL_KEY, converterUrl);
                    GUI.FocusControl(null);
                }
            }

            if (GUILayout.Button("在浏览器中打开", GUILayout.Width(120)))
            {
                if (string.IsNullOrWhiteSpace(converterUrl))
                {
                    Debug.LogError("[HtmlToUGUIBaker] 唤起中断: 转换器路径或 URL 为空，请先配置路径或点击浏览选择文件。");
                }
                else
                {
                    Application.OpenURL(converterUrl);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUILayout.Space(10);
        }

        private void DrawSpriteFolderUI()
        {
            GUILayout.Label("图片资源索引", EditorStyles.label);
            GUILayout.BeginVertical("box");

            EditorGUI.BeginChangeCheck();
            spriteFolderAsset = (DefaultAsset)EditorGUILayout.ObjectField("图片资源文件夹", spriteFolderAsset, typeof(DefaultAsset), false);
            if (EditorGUI.EndChangeCheck())
            {
                spriteFolderPath = spriteFolderAsset != null ? AssetDatabase.GetAssetPath(spriteFolderAsset) : "";
                EditorPrefs.SetString(PREFS_SPRITE_FOLDER_KEY, spriteFolderPath);
                RebuildSpriteIndex();
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("刷新图片索引", GUILayout.Height(24)))
            {
                RebuildSpriteIndex();
            }

            if (GUILayout.Button("一键转 Sprite 并刷新", GUILayout.Height(24)))
            {
                ConvertFolderTexturesToSprites();
                RebuildSpriteIndex();
            }

            using (new EditorGUI.DisabledScope(spriteNameList.Count == 0))
            {
                if (GUILayout.Button("复制图片名清单", GUILayout.Height(24)))
                {
                    GUIUtility.systemCopyBuffer = string.Join("\n", spriteNameList);
                    Debug.Log($"[HtmlToUGUIBaker] 已复制 {spriteNameList.Count} 个图片名称到剪贴板。");
                }
            }
            GUILayout.EndHorizontal();

            if (GUILayout.Button("浏览文件夹...", GUILayout.Height(24)))
            {
                string absolutePath = EditorUtility.OpenFolderPanel("选择图片资源文件夹", Application.dataPath, "");
                if (!string.IsNullOrEmpty(absolutePath))
                {
                    string projectPath = AbsolutePathToProjectPath(absolutePath);
                    if (!string.IsNullOrEmpty(projectPath))
                    {
                        spriteFolderPath = projectPath;
                        spriteFolderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(projectPath);
                        EditorPrefs.SetString(PREFS_SPRITE_FOLDER_KEY, spriteFolderPath);
                        RebuildSpriteIndex();
                    }
                    else
                    {
                        Debug.LogWarning("[HtmlToUGUIBaker] 请选择 Assets 目录内的文件夹，这样才能被 Unity 资源系统识别。");
                    }
                }
            }

            if (!string.IsNullOrEmpty(spriteFolderPath))
            {
                EditorGUILayout.LabelField("当前路径", spriteFolderPath);
            }

            EditorGUILayout.LabelField("已索引图片数", spriteNameList.Count.ToString());
            GUILayout.EndVertical();
            GUILayout.Space(10);
        }

        private void DrawFileModeUI()
        {
            jsonAsset = (TextAsset)EditorGUILayout.ObjectField("JSON 数据源", jsonAsset, typeof(TextAsset), false);
            EditorGUILayout.HelpBox("请将工程目录下的 UI 数据文件拖拽至此，推荐 .txt 或 .uijson，避免 Spine 等插件误扫 .json。", MessageType.Info);
        }

        private void DrawStringModeUI()
        {
            GUILayout.Label("在此粘贴 JSON 文本:", EditorStyles.label);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.Height(200));
            rawJsonString = EditorGUILayout.TextArea(rawJsonString, GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();
            GUILayout.Space(5);

            if (GUILayout.Button("将当前 JSON 保存为文件到工程目录..."))
            {
                SaveRawJsonToProject();
            }
        }

        private void CopyDSLToClipboard()
        {
            if (config == null || config.supportedResolutions == null || config.supportedResolutions.Count <= selectedResolutionIndex)
            {
                Debug.LogError("[HtmlToUGUIBaker] 复制失败: 配置文件缺失或分辨率索引越界。");
                return;
            }

            if (config.dslTemplateAsset == null)
            {
                Debug.LogError("[HtmlToUGUIBaker] 复制失败: 配置文件中未指定 DSL 模板文件 (TextAsset)，请在 SO 面板中拖入 .md 模板文件。");
                return;
            }

            Vector2 res = config.supportedResolutions[selectedResolutionIndex].resolution;
            string dsl = config.dslTemplateAsset.text.Replace("{WIDTH}", res.x.ToString()).Replace("{HEIGHT}", res.y.ToString());
            GUIUtility.systemCopyBuffer = dsl;
            Debug.Log($"[HtmlToUGUIBaker] 已成功复制分辨率为 {res.x}x{res.y} 的 DSL 规范文档到剪贴板。");
        }

        private void SaveRawJsonToProject()
        {
            if (string.IsNullOrWhiteSpace(rawJsonString))
            {
                Debug.LogError("[HtmlToUGUIBaker] 保存失败: 当前 JSON 字符串为空，请先粘贴数据。");
                return;
            }

            string savePath = EditorUtility.SaveFilePanelInProject(
                "保存 UI 数据",
                "NewUIWindow.uijson.txt",
                "txt",
                "请选择要保存的目录，建议使用 .txt 或 .uijson，避免被 Spine 等插件误判"
            );

            if (string.IsNullOrEmpty(savePath))
            {
                return;
            }

            if (savePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                savePath = Path.ChangeExtension(savePath, ".uijson.txt");
            }

            try
            {
                File.WriteAllText(savePath, rawJsonString);
                AssetDatabase.Refresh();
                TextAsset savedAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(savePath);
                if (savedAsset != null)
                {
                    jsonAsset = savedAsset;
                    currentMode = InputMode.FileAsset;
                    Debug.Log($"[HtmlToUGUIBaker] JSON 文件已成功保存至: {savePath}，并已自动切换至文件模式。");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[HtmlToUGUIBaker] 文件写入失败: 路径 {savePath}，错误信息: {e.Message}");
            }
        }

        #if false
        #if false
        #if false
        private void DrawPrecheckUI()
        {
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("烘焙前预检", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(precheckSummary, MessageType.Info);

            precheckScroll = EditorGUILayout.BeginScrollView(precheckScroll, GUILayout.Height(180));
            if (precheckItems.Count == 0)
            {
                EditorGUILayout.LabelField("尚未执行预检。");
            }
            else
            {
                foreach (var item in precheckItems)
                {
                    GUI.color = item.success ? new Color(0.72f, 1f, 0.72f) : new Color(1f, 0.78f, 0.78f);
                    GUILayout.BeginVertical("box");
                    EditorGUILayout.LabelField($"{item.nodeName}  [{item.nodeType}]");
                    EditorGUILayout.LabelField(item.success ? "状态: 命中 Sprite" : "状态: 未命中 Sprite");
                    if (!string.IsNullOrEmpty(item.spriteName))
                    {
                        EditorGUILayout.LabelField("spriteName", item.spriteName);
                    }
                    if (!string.IsNullOrEmpty(item.spritePath))
                    {
                        EditorGUILayout.LabelField("spritePath", item.spritePath);
                    }
                    if (!string.IsNullOrEmpty(item.matchedKey))
                    {
                        EditorGUILayout.LabelField("matchedKey", item.matchedKey);
                    }
                    if (!string.IsNullOrEmpty(item.assetPath))
                    {
                        EditorGUILayout.LabelField("assetPath", item.assetPath);
                    }
                    if (!string.IsNullOrEmpty(item.note))
                    {
                        EditorGUILayout.LabelField("note", item.note);
                    }
                    GUILayout.EndVertical();
                    GUI.color = Color.white;
                }
            }
            EditorGUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void RunPrecheck()
        {
            precheckItems.Clear();

            if (targetCanvas == null)
            {
                precheckSummary = "请先指定目标 Canvas。";
                return;
            }

            if (!TryGetJsonContentForPrecheck(out string jsonContent, out string error))
            {
                precheckSummary = error;
                return;
            }

            jsonContent = NormalizeJsonContent(jsonContent);
            if (!LooksLikeJsonObject(jsonContent))
            {
                precheckSummary = "输入内容看起来不是标准 UI JSON 对象。";
                return;
            }

            UIDataNode rootNode;
            try
            {
                rootNode = JsonUtility.FromJson<UIDataNode>(jsonContent);
            }
            catch (Exception e)
            {
                precheckSummary = $"JSON 解析失败: {e.Message}";
                return;
            }

            if (rootNode == null)
            {
                precheckSummary = "JSON 解析失败，根节点为空。";
                return;
            }

            CollectSpritePrecheckItems(rootNode);

            int okCount = 0;
            for (int i = 0; i < precheckItems.Count; i++)
            {
                if (precheckItems[i].success)
                {
                    okCount++;
                }
            }

            precheckSummary = $"预检完成: {okCount}/{precheckItems.Count} 个图片节点可绑定 Sprite，{precheckItems.Count - okCount} 个未命中。";
        }

        #endif

        private void DrawPrecheckUI()
        {
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("烘焙前预检", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(string.IsNullOrEmpty(precheckSummary) ? "尚未执行预检。" : precheckSummary, MessageType.Info);

            precheckScroll = EditorGUILayout.BeginScrollView(precheckScroll, GUILayout.Height(180));
            if (precheckItems.Count == 0)
            {
                EditorGUILayout.LabelField("尚未执行预检。");
            }
            else
            {
                foreach (var item in precheckItems)
                {
                    GUI.color = item.success ? new Color(0.72f, 1f, 0.72f) : new Color(1f, 0.78f, 0.78f);
                    GUILayout.BeginVertical("box");
                    EditorGUILayout.LabelField($"{item.nodeName}  [{item.nodeType}]");
                    EditorGUILayout.LabelField(item.success ? "状态: 命中 Sprite" : "状态: 未命中 Sprite");
                    if (!string.IsNullOrEmpty(item.spriteName))
                    {
                        EditorGUILayout.LabelField("spriteName", item.spriteName);
                    }
                    if (!string.IsNullOrEmpty(item.spritePath))
                    {
                        EditorGUILayout.LabelField("spritePath", item.spritePath);
                    }
                    if (!string.IsNullOrEmpty(item.matchedKey))
                    {
                        EditorGUILayout.LabelField("matchedKey", item.matchedKey);
                    }
                    if (!string.IsNullOrEmpty(item.assetPath))
                    {
                        EditorGUILayout.LabelField("assetPath", item.assetPath);
                    }
                    if (!string.IsNullOrEmpty(item.note))
                    {
                        EditorGUILayout.LabelField("note", item.note);
                    }
                    GUILayout.EndVertical();
                    GUI.color = Color.white;
                }
            }
            EditorGUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void RunPrecheck()
        {
            precheckItems.Clear();

            if (targetCanvas == null)
            {
                precheckSummary = "请先指定目标 Canvas。";
                return;
            }

            if (!TryGetJsonContentForPrecheck(out string jsonContent, out string error))
            {
                precheckSummary = error;
                return;
            }

            jsonContent = NormalizeJsonContent(jsonContent);
            if (!LooksLikeJsonObject(jsonContent))
            {
                precheckSummary = "输入内容看起来不是标准 UI JSON 对象。";
                return;
            }

            UIDataNode rootNode;
            try
            {
                rootNode = JsonUtility.FromJson<UIDataNode>(jsonContent);
            }
            catch (Exception e)
            {
                precheckSummary = $"JSON 解析失败: {e.Message}";
                return;
            }

            if (rootNode == null)
            {
                precheckSummary = "JSON 解析失败，根节点为空。";
                return;
            }

            CollectSpritePrecheckItems(rootNode);

            int okCount = 0;
            for (int i = 0; i < precheckItems.Count; i++)
            {
                if (precheckItems[i].success)
                {
                    okCount++;
                }
            }

            precheckSummary = $"预检完成: {okCount}/{precheckItems.Count} 个图片节点可绑定 Sprite，{precheckItems.Count - okCount} 个未命中。";
        }

        #endif

        #endif

        private void DrawSpritePrecheckUI()
        {
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Precheck", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(string.IsNullOrEmpty(precheckSummary) ? "Precheck has not run yet." : precheckSummary, MessageType.Info);

            precheckScroll = EditorGUILayout.BeginScrollView(precheckScroll, GUILayout.Height(180));
            if (precheckItems.Count == 0)
            {
                EditorGUILayout.LabelField("No precheck results yet.");
            }
            else
            {
                List<SpritePrecheckItem> matchedItems = new List<SpritePrecheckItem>();
                List<SpritePrecheckItem> missingFileItems = new List<SpritePrecheckItem>();
                List<SpritePrecheckItem> nameMismatchItems = new List<SpritePrecheckItem>();
                List<SpritePrecheckItem> notSpriteItems = new List<SpritePrecheckItem>();

                for (int i = 0; i < precheckItems.Count; i++)
                {
                    SpritePrecheckItem item = precheckItems[i];
                    if (item == null)
                    {
                        continue;
                    }

                    if (item.success)
                    {
                        matchedItems.Add(item);
                        continue;
                    }

                    if (string.Equals(item.failCategory, "缺少文件", StringComparison.Ordinal))
                    {
                        missingFileItems.Add(item);
                    }
                    else if (string.Equals(item.failCategory, "名字不一致", StringComparison.Ordinal))
                    {
                        nameMismatchItems.Add(item);
                    }
                    else if (string.Equals(item.failCategory, "没转 Sprite", StringComparison.Ordinal))
                    {
                        notSpriteItems.Add(item);
                    }
                    else
                    {
                        missingFileItems.Add(item);
                    }
                }

                DrawSpritePrecheckSection("命中图片", matchedItems, true);
                DrawSpritePrecheckSection("缺少文件", missingFileItems, false);
                DrawSpritePrecheckSection("名字不一致", nameMismatchItems, false);
                DrawSpritePrecheckSection("没转 Sprite", notSpriteItems, false);
            }
            EditorGUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawSpritePrecheckSection(string title, List<SpritePrecheckItem> items, bool matchedSection)
        {
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField($"{title} ({items.Count})", EditorStyles.boldLabel);

            if (items.Count == 0)
            {
                EditorGUILayout.LabelField("None");
                GUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < items.Count; i++)
            {
                SpritePrecheckItem item = items[i];
                if (item == null)
                {
                    continue;
                }

                GUI.color = matchedSection ? new Color(0.72f, 1f, 0.72f) : new Color(1f, 0.78f, 0.78f);
                GUILayout.BeginVertical("box");
                EditorGUILayout.LabelField($"{item.nodeName}  [{item.nodeType}]");
                EditorGUILayout.LabelField(item.success ? "Status: Sprite matched" : $"Status: {item.failCategory}");
                if (!string.IsNullOrEmpty(item.spriteName))
                {
                    EditorGUILayout.LabelField("spriteName", item.spriteName);
                }
                if (!string.IsNullOrEmpty(item.spritePath))
                {
                    EditorGUILayout.LabelField("spritePath", item.spritePath);
                }
                if (!string.IsNullOrEmpty(item.matchedKey))
                {
                    EditorGUILayout.LabelField("matchedKey", item.matchedKey);
                }
                if (!string.IsNullOrEmpty(item.assetPath))
                {
                    EditorGUILayout.LabelField("assetPath", item.assetPath);
                }
                if (!string.IsNullOrEmpty(item.note))
                {
                    EditorGUILayout.LabelField("note", item.note);
                }
                GUILayout.EndVertical();
                GUI.color = Color.white;
            }
            GUILayout.EndVertical();
        }

        private void RunSpritePrecheck()
        {
            precheckItems.Clear();

            if (targetCanvas == null)
            {
                precheckSummary = "Please assign a target Canvas first.";
                return;
            }

            if (string.IsNullOrEmpty(spriteFolderPath) || !AssetDatabase.IsValidFolder(spriteFolderPath))
            {
                precheckSummary = "Please select a valid sprite folder first. Precheck only scans the provided folder.";
                return;
            }

            if (!TryGetJsonContentForPrecheck(out string jsonContent, out string error))
            {
                precheckSummary = error;
                return;
            }

            jsonContent = NormalizeJsonContent(jsonContent);
            if (!LooksLikeJsonObject(jsonContent))
            {
                precheckSummary = "Input does not look like a valid UI JSON object.";
                return;
            }

            UIDataNode rootNode;
            try
            {
                rootNode = JsonUtility.FromJson<UIDataNode>(jsonContent);
            }
            catch (Exception e)
            {
                precheckSummary = $"JSON parse failed: {e.Message}";
                return;
            }

            if (rootNode == null)
            {
                precheckSummary = "JSON parse failed, root node is null.";
                return;
            }

            CollectSpritePrecheckItems(rootNode);

            int okCount = 0;
            int missingFileCount = 0;
            int nameMismatchCount = 0;
            int notSpriteCount = 0;
            for (int i = 0; i < precheckItems.Count; i++)
            {
                SpritePrecheckItem item = precheckItems[i];
                if (item == null)
                {
                    continue;
                }

                if (item.success)
                {
                    okCount++;
                    continue;
                }

                if (string.Equals(item.failCategory, "缺少文件", StringComparison.Ordinal))
                {
                    missingFileCount++;
                }
                else if (string.Equals(item.failCategory, "名字不一致", StringComparison.Ordinal))
                {
                    nameMismatchCount++;
                }
                else if (string.Equals(item.failCategory, "没转 Sprite", StringComparison.Ordinal))
                {
                    notSpriteCount++;
                }
                else
                {
                    missingFileCount++;
                }
            }

            precheckSummary = $"Precheck done: {okCount}/{precheckItems.Count} matched, 缺少文件 {missingFileCount}, 名字不一致 {nameMismatchCount}, 没转 Sprite {notSpriteCount}.";
        }

        private void CollectSpritePrecheckItems(UIDataNode node)
        {
            if (node == null)
            {
                return;
            }

            if (ShouldCheckSpriteBindingForPrecheck(node))
            {
                SpritePrecheckItem item = new SpritePrecheckItem
                {
                    nodeName = node.name,
                    nodeType = node.type,
                    spriteName = node.spriteName,
                    spritePath = node.spritePath
                };

                if (TryResolveSprite(node, out Sprite sprite, out string matchedKey))
                {
                    item.success = true;
                    item.matchedKey = matchedKey;
                    item.assetPath = AssetDatabase.GetAssetPath(sprite);
                    item.note = "Will bind to Image.sprite during bake.";
                }
                else
                {
                    item.success = false;
                    item.failCategory = ClassifySpritePrecheckFailure(node);
                    item.note = BuildSpritePrecheckFailureNote(node, item.failCategory);
                }

                precheckItems.Add(item);
            }

            if (node.children == null)
            {
                return;
            }

            foreach (var child in node.children)
            {
                CollectSpritePrecheckItems(child);
            }
        }

        private static bool ShouldCheckSpriteBindingForPrecheck(UIDataNode node)
        {
            if (node == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(node.spriteName) || !string.IsNullOrEmpty(node.spritePath))
            {
                return true;
            }

            return node.type != null && node.type.Equals("image", StringComparison.OrdinalIgnoreCase);
        }

        private string ClassifySpritePrecheckFailure(UIDataNode node)
        {
            if (node == null || string.IsNullOrEmpty(spriteFolderPath) || !AssetDatabase.IsValidFolder(spriteFolderPath))
            {
                return "缺少文件";
            }

            string[] candidates = GetSpriteLookupCandidates(node);
            bool hasAnyRelatedFile = false;
            bool hasAnyRelatedTexture = false;

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string normalizedCandidate = NormalizeSpriteKey(candidate);
                string[] guids = AssetDatabase.FindAssets(string.IsNullOrWhiteSpace(normalizedCandidate) ? candidate : normalizedCandidate, new[] { spriteFolderPath });
                for (int g = 0; g < guids.Length; g++)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guids[g]);
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        continue;
                    }

                    Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
                    if (texture == null)
                    {
                        continue;
                    }

                    string fileKey = Path.GetFileNameWithoutExtension(assetPath);
                    string normalizedFileKey = NormalizeSpriteKey(fileKey);
                    string normalizedTextureName = NormalizeSpriteKey(texture.name);
                    if (MatchesCandidate(normalizedCandidate, fileKey, normalizedFileKey, normalizedTextureName))
                    {
                        hasAnyRelatedFile = true;
                        TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                        if (sprite != null && importer != null && importer.textureType == TextureImporterType.Sprite)
                        {
                            hasAnyRelatedTexture = true;
                            return "名字不一致";
                        }

                        if (importer != null && importer.textureType != TextureImporterType.Sprite)
                        {
                            return "没转 Sprite";
                        }
                    }
                }
            }

            if (hasAnyRelatedFile)
            {
                return hasAnyRelatedTexture ? "名字不一致" : "没转 Sprite";
            }

            return "缺少文件";
        }

        private static string BuildSpritePrecheckFailureNote(UIDataNode node, string failCategory)
        {
            string spriteName = node != null ? node.spriteName : string.Empty;
            string spritePath = node != null ? node.spritePath : string.Empty;
            string nodeName = node != null ? node.name : string.Empty;

            if (failCategory == "没转 Sprite")
            {
                return $"Found related file in folder, but it is not imported as Sprite. node={nodeName}, spriteName={spriteName}, spritePath={spritePath}";
            }

            if (failCategory == "名字不一致")
            {
                return $"Found a related image candidate in the folder, but its name does not match the current binding key. node={nodeName}, spriteName={spriteName}, spritePath={spritePath}";
            }

            return $"No related file found under the selected folder. node={nodeName}, spriteName={spriteName}, spritePath={spritePath}";
        }

        private bool TryGetJsonContentForPrecheck(out string jsonContent, out string error)
        {
            jsonContent = string.Empty;
            error = string.Empty;

            if (currentMode == InputMode.FileAsset)
            {
                if (jsonAsset == null)
                {
                    error = "Please select a JSON data source.";
                    return false;
                }

                jsonContent = jsonAsset.text;
                return true;
            }

            if (string.IsNullOrWhiteSpace(rawJsonString))
            {
                error = "Current string mode JSON content is empty.";
                return false;
            }

            jsonContent = rawJsonString;
            return true;
        }

        #if false
        private void CollectPrecheckItems(UIDataNode node)
        {
            if (node == null)
            {
                return;
            }

            if (ShouldCheckSpriteBinding(node))
            {
                SpritePrecheckItem item = new SpritePrecheckItem
                {
                    nodeName = node.name,
                    nodeType = node.type,
                    spriteName = node.spriteName,
                    spritePath = node.spritePath
                };

                if (TryResolveSprite(node, out Sprite sprite, out string matchedKey))
                {
                    item.success = true;
                    item.matchedKey = matchedKey;
                    item.assetPath = AssetDatabase.GetAssetPath(sprite);
                    item.note = "烘焙时会绑定到 Image.sprite";
                }
                else
                {
                    item.success = false;
                    item.note = "未找到可绑定的 Sprite，请检查文件夹、命名或导入类型";
                }

                precheckItems.Add(item);
            }

            if (node.children == null)
            {
                return;
            }

            foreach (var child in node.children)
            {
                CollectPrecheckItems(child);
            }
        }

        private static bool ShouldCheckSpriteBinding(UIDataNode node)
        {
            if (node == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(node.spriteName) || !string.IsNullOrEmpty(node.spritePath))
            {
                return true;
            }

            return node.type != null && node.type.Equals("image", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryGetJsonContent(out string jsonContent, out string error)
        {
            jsonContent = string.Empty;
            error = string.Empty;

            if (currentMode == InputMode.FileAsset)
            {
                if (jsonAsset == null)
                {
                    error = "请先指定 JSON 数据源。";
                    return false;
                }

                jsonContent = jsonAsset.text;
                return true;
            }

            if (string.IsNullOrWhiteSpace(rawJsonString))
            {
                error = "当前字符串模式下 JSON 内容为空。";
                return false;
            }

            jsonContent = rawJsonString;
            return true;
        }

        private void CollectPrecheckItems(UIDataNode node)
        {
            if (node == null)
            {
                return;
            }

            if (ShouldCheckSpriteBinding(node))
            {
                SpritePrecheckItem item = new SpritePrecheckItem
                {
                    nodeName = node.name,
                    nodeType = node.type,
                    spriteName = node.spriteName,
                    spritePath = node.spritePath
                };

                if (TryResolveSprite(node, out Sprite sprite, out string matchedKey))
                {
                    item.success = true;
                    item.matchedKey = matchedKey;
                    item.assetPath = AssetDatabase.GetAssetPath(sprite);
                    item.note = "烘焙时会绑定到 Image.sprite";
                }
                else
                {
                    item.success = false;
                    item.note = "未找到可绑定的 Sprite，请检查文件夹、命名或导入类型";
                }

                precheckItems.Add(item);
            }

            if (node.children == null)
            {
                return;
            }

            foreach (var child in node.children)
            {
                CollectPrecheckItems(child);
            }
        }

        private static bool ShouldCheckSpriteBinding(UIDataNode node)
        {
            if (node == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(node.spriteName) || !string.IsNullOrEmpty(node.spritePath))
            {
                return true;
            }

            return node.type != null && node.type.Equals("image", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryGetJsonContent(out string jsonContent, out string error)
        {
            jsonContent = string.Empty;
            error = string.Empty;

            if (currentMode == InputMode.FileAsset)
            {
                if (jsonAsset == null)
                {
                    error = "请先指定 JSON 数据源。";
                    return false;
                }

                jsonContent = jsonAsset.text;
                return true;
            }

            if (string.IsNullOrWhiteSpace(rawJsonString))
            {
                error = "当前字符串模式下 JSON 内容为空。";
                return false;
            }

            jsonContent = rawJsonString;
            return true;
        }

        #endif

        private void ExecuteBake()
        {
            if (targetCanvas == null)
            {
                Debug.LogError("[HtmlToUGUIBaker] 烘焙中断: 未指定目标 Canvas，无法确定 UI 挂载点。");
                return;
            }

            spriteBindingFailures.Clear();

            string jsonContent;
            if (currentMode == InputMode.FileAsset)
            {
                if (jsonAsset == null)
                {
                    Debug.LogError("[HtmlToUGUIBaker] 烘焙中断: 文件模式下未指定 JSON 数据源。");
                    return;
                }
                jsonContent = jsonAsset.text;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(rawJsonString))
                {
                    Debug.LogError("[HtmlToUGUIBaker] 烘焙中断: 字符串模式下 JSON 内容为空。");
                    return;
                }
                jsonContent = rawJsonString;
            }

            jsonContent = NormalizeJsonContent(jsonContent);
            if (!LooksLikeJsonObject(jsonContent))
            {
                Debug.LogError($"[HtmlToUGUIBaker] 烘焙中断: 输入内容看起来不是 UI JSON 对象。请确认你粘贴/导入的是由 HTML 工具导出的 JSON，而不是 HTML、Markdown、或带说明文字的文本。片段预览: {PreviewText(jsonContent)}");
                return;
            }

            ConfigureCanvasScaler(targetCanvas);

            UIDataNode rootNode;
            try
            {
                rootNode = JsonUtility.FromJson<UIDataNode>(jsonContent);
            }
            catch (ArgumentException e)
            {
                Debug.LogError($"[HtmlToUGUIBaker] 烘焙中断: JSON 解析失败，请确认内容是标准 JSON 对象，且根节点包含 name/type/x/y/width/height/children 等字段。原始错误: {e.Message}\n内容片段: {PreviewText(jsonContent)}");
                return;
            }

            if (rootNode == null)
            {
                Debug.LogError("[HtmlToUGUIBaker] 烘焙中断: JSON 解析失败，请检查数据格式是否符合规范。");
                return;
            }

            GameObject rootGo = CreateUINode(rootNode, targetCanvas.transform, 0f, 0f);
            Undo.RegisterCreatedObjectUndo(rootGo, "Bake UI Prototype");
            Selection.activeGameObject = rootGo;

            Debug.Log($"[HtmlToUGUIBaker] 烘焙完成: 成功生成 UI 树 [{rootGo.name}]，当前基准分辨率已适配。");
            if (spriteBindingFailures.Count > 0)
            {
                Debug.LogWarning($"[HtmlToUGUIBaker] 有 {spriteBindingFailures.Count} 个图片节点未绑定到实际 Sprite。前 20 条:\n- {string.Join("\n- ", spriteBindingFailures.GetRange(0, Mathf.Min(20, spriteBindingFailures.Count)))}");
            }
        }

        private void ConfigureCanvasScaler(Canvas canvas)
        {
            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null)
            {
                scaler = canvas.gameObject.AddComponent<CanvasScaler>();
            }

            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;

            Vector2 targetRes = new Vector2(1920, 1080);
            if (config != null && config.supportedResolutions != null && config.supportedResolutions.Count > selectedResolutionIndex)
            {
                targetRes = config.supportedResolutions[selectedResolutionIndex].resolution;
            }

            scaler.referenceResolution = targetRes;
            scaler.matchWidthOrHeight = 0.5f;
        }

        private static string NormalizeJsonContent(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }

            content = content.Trim();
            if (content.Length > 0 && content[0] == '\ufeff')
            {
                content = content.Substring(1).Trim();
            }

            if (content.StartsWith("```", StringComparison.Ordinal))
            {
                int firstBrace = content.IndexOf('{');
                int lastBrace = content.LastIndexOf('}');
                if (firstBrace >= 0 && lastBrace > firstBrace)
                {
                    content = content.Substring(firstBrace, lastBrace - firstBrace + 1);
                }
            }
            else if (!content.StartsWith("{", StringComparison.Ordinal) && !content.StartsWith("[", StringComparison.Ordinal))
            {
                int firstBrace = content.IndexOf('{');
                int lastBrace = content.LastIndexOf('}');
                if (firstBrace >= 0 && lastBrace > firstBrace)
                {
                    content = content.Substring(firstBrace, lastBrace - firstBrace + 1);
                }
            }

            return content.Trim();
        }

        private static bool LooksLikeJsonObject(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            return content.StartsWith("{", StringComparison.Ordinal) && content.EndsWith("}", StringComparison.Ordinal);
        }

        private static string PreviewText(string content, int maxLen = 220)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            content = content.Replace("\r", " ").Replace("\n", " ").Trim();
            if (content.Length <= maxLen)
            {
                return content;
            }

            return content.Substring(0, maxLen) + "...";
        }

        private void RebuildSpriteIndex()
        {
            spriteIndex.Clear();
            spriteNameList.Clear();

            if (string.IsNullOrEmpty(spriteFolderPath))
            {
                return;
            }

            if (!AssetDatabase.IsValidFolder(spriteFolderPath))
            {
                Debug.LogWarning($"[HtmlToUGUIBaker] 图片文件夹无效: {spriteFolderPath}");
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:Sprite", new[] { spriteFolderPath });
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                if (sprite == null)
                {
                    continue;
                }

                string fileKey = Path.GetFileNameWithoutExtension(assetPath);
                AddSpriteIndexKey(fileKey, sprite);
                AddSpriteIndexKey(sprite.name, sprite);
                AddSpriteIndexKey(AssetDatabase.AssetPathToGUID(assetPath), sprite);
                spriteNameList.Add(fileKey);
            }

            spriteNameList.Sort(StringComparer.OrdinalIgnoreCase);
            Debug.Log($"[HtmlToUGUIBaker] 图片索引已刷新，共 {spriteNameList.Count} 个可匹配条目。");
        }

        private void ConvertFolderTexturesToSprites()
        {
            if (string.IsNullOrEmpty(spriteFolderPath))
            {
                Debug.LogWarning("[HtmlToUGUIBaker] 请先选择图片资源文件夹。");
                return;
            }

            if (!AssetDatabase.IsValidFolder(spriteFolderPath))
            {
                Debug.LogWarning($"[HtmlToUGUIBaker] 图片文件夹无效: {spriteFolderPath}");
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { spriteFolderPath });
            int converted = 0;

            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer == null)
                {
                    continue;
                }

                bool changed = false;
                if (importer.textureType != TextureImporterType.Sprite)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    changed = true;
                }

                if (importer.spriteImportMode != SpriteImportMode.Single)
                {
                    importer.spriteImportMode = SpriteImportMode.Single;
                    changed = true;
                }

                if (changed)
                {
                    importer.SaveAndReimport();
                    converted++;
                }
            }

            Debug.Log($"[HtmlToUGUIBaker] 已处理 {converted} 个纹理导入设置，尽可能切换为 Sprite。");
        }

        private void AddSpriteIndexKey(string rawKey, Sprite sprite)
        {
            if (string.IsNullOrEmpty(rawKey) || sprite == null)
            {
                return;
            }

            spriteIndex[rawKey] = sprite;

            string normalized = NormalizeSpriteKey(rawKey);
            if (!string.IsNullOrEmpty(normalized))
            {
                spriteIndex[normalized] = sprite;
            }
        }

        private static string AbsolutePathToProjectPath(string absolutePath)
        {
            absolutePath = absolutePath.Replace("\\", "/");
            string dataPath = Application.dataPath.Replace("\\", "/");
            if (!absolutePath.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return "Assets" + absolutePath.Substring(dataPath.Length);
        }

        private GameObject CreateUINode(UIDataNode nodeData, Transform parent, float parentAbsX, float parentAbsY)
        {
            GameObject go = new GameObject(nodeData.name);
            go.transform.SetParent(parent, false);

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);

            float localX = nodeData.x - parentAbsX;
            float localY = nodeData.y - parentAbsY;
            rect.anchoredPosition = new Vector2(localX, -localY);
            rect.sizeDelta = new Vector2(nodeData.width, nodeData.height);

            Transform childrenContainer = ApplyComponentByType(go, nodeData);
            if (nodeData.children != null && nodeData.children.Count > 0)
            {
                foreach (var childNode in nodeData.children)
                {
                    CreateUINode(childNode, childrenContainer, nodeData.x, nodeData.y);
                }
            }

            return go;
        }

        private Transform ApplyComponentByType(GameObject go, UIDataNode nodeData)
        {
            Color bgColor = ParseHexColor(nodeData.color, Color.white);
            Color fontColor = ParseHexColor(nodeData.fontColor, Color.black);
            int fontSize = nodeData.fontSize > 0 ? nodeData.fontSize : 24;
            TextAlignmentOptions alignment = ParseTextAlign(nodeData.textAlign);
            bool isMultiLine = nodeData.height > (fontSize * 1.5f);

            switch (nodeData.type.ToLower())
            {
                case "div":
                case "image":
                {
                    Image img = go.AddComponent<Image>();
                    img.color = bgColor;
                    ApplySpriteBinding(img, nodeData);
                    if (nodeData.type != null && nodeData.type.Equals("image", StringComparison.OrdinalIgnoreCase) && img.sprite != null)
                    {
                        // 图片节点默认不要继承透明 tint，否则 Sprite 会被直接乘成透明。
                        img.color = Color.white;
                    }
                    if (img.color.a <= 0.01f)
                    {
                        img.raycastTarget = false;
                    }
                    return go.transform;
                }
                case "text":
                {
                    TextMeshProUGUI txt = go.AddComponent<TextMeshProUGUI>();
                    txt.text = nodeData.text;
                    txt.color = fontColor;
                    txt.fontSize = fontSize;
                    txt.alignment = alignment;
                    txt.enableWordWrapping = isMultiLine;
                    txt.overflowMode = isMultiLine ? TextOverflowModes.Truncate : TextOverflowModes.Overflow;
                    txt.raycastTarget = false;
                    return go.transform;
                }
                case "button":
                {
                    Image btnImg = go.AddComponent<Image>();
                    btnImg.color = bgColor;
                    ApplySpriteBinding(btnImg, nodeData);

                    Button btn = go.AddComponent<Button>();
                    btn.targetGraphic = btnImg;

                    GameObject btnTxtGo = CreateChildRect(go, "Text (TMP)", Vector2.zero, Vector2.one);
                    TextMeshProUGUI btnTxt = btnTxtGo.AddComponent<TextMeshProUGUI>();
                    btnTxt.text = nodeData.text;
                    btnTxt.color = fontColor;
                    btnTxt.fontSize = fontSize;
                    btnTxt.alignment = alignment;
                    btnTxt.enableWordWrapping = false;
                    btnTxt.overflowMode = TextOverflowModes.Overflow;
                    btnTxt.raycastTarget = false;
                    return go.transform;
                }
                case "input":
                {
                    Image inputBg = go.AddComponent<Image>();
                    inputBg.color = bgColor;
                    ApplySpriteBinding(inputBg, nodeData);

                    TMP_InputField inputField = go.AddComponent<TMP_InputField>();
                    inputField.targetGraphic = inputBg;

                    GameObject textAreaGo = CreateChildRect(go, "Text Area", Vector2.zero, Vector2.one, new Vector2(10, 5), new Vector2(-10, -5));
                    textAreaGo.AddComponent<RectMask2D>();

                    GameObject phGo = CreateChildRect(textAreaGo, "Placeholder", Vector2.zero, Vector2.one);
                    TextMeshProUGUI phTxt = phGo.AddComponent<TextMeshProUGUI>();
                    phTxt.text = nodeData.text;
                    Color phColor = fontColor;
                    phColor.a = 0.5f;
                    phTxt.color = phColor;
                    phTxt.fontSize = fontSize;
                    phTxt.alignment = alignment;
                    phTxt.enableWordWrapping = false;
                    phTxt.raycastTarget = false;

                    GameObject textGo = CreateChildRect(textAreaGo, "Text", Vector2.zero, Vector2.one);
                    TextMeshProUGUI inTxt = textGo.AddComponent<TextMeshProUGUI>();
                    inTxt.color = fontColor;
                    inTxt.fontSize = fontSize;
                    inTxt.alignment = alignment;
                    inTxt.enableWordWrapping = false;
                    inTxt.raycastTarget = false;

                    inputField.textViewport = textAreaGo.GetComponent<RectTransform>();
                    inputField.textComponent = inTxt;
                    inputField.placeholder = phTxt;
                    return go.transform;
                }
                case "scroll":
                {
                    Image scrollBg = go.AddComponent<Image>();
                    scrollBg.color = bgColor;
                    ApplySpriteBinding(scrollBg, nodeData);
                    if (scrollBg.color.a <= 0.01f)
                    {
                        scrollBg.raycastTarget = false;
                    }

                    ScrollRect scrollRect = go.AddComponent<ScrollRect>();
                    bool isVertical = string.IsNullOrEmpty(nodeData.dir) || nodeData.dir.ToLower() == "v";
                    scrollRect.horizontal = !isVertical;
                    scrollRect.vertical = isVertical;

                    GameObject viewportGo = CreateChildRect(go, "Viewport", Vector2.zero, Vector2.one);
                    viewportGo.AddComponent<RectMask2D>();

                    GameObject contentGo = CreateChildRect(viewportGo, "Content", new Vector2(0, 1), new Vector2(0, 1));
                    RectTransform contentRect = contentGo.GetComponent<RectTransform>();
                    contentRect.pivot = new Vector2(0, 1);
                    contentRect.sizeDelta = new Vector2(nodeData.width, nodeData.height);

                    scrollRect.viewport = viewportGo.GetComponent<RectTransform>();
                    scrollRect.content = contentRect;
                    return contentGo.transform;
                }
                case "toggle":
                {
                    Toggle toggle = go.AddComponent<Toggle>();
                    toggle.isOn = nodeData.isChecked;

                    float boxSize = Mathf.Min(nodeData.height, 30f);
                    GameObject tBgGo = CreateChildRect(go, "Background", new Vector2(0, 0.5f), new Vector2(0, 0.5f));
                    RectTransform tBgRect = tBgGo.GetComponent<RectTransform>();
                    tBgRect.sizeDelta = new Vector2(boxSize, boxSize);
                    tBgRect.anchoredPosition = new Vector2(boxSize / 2, 0);
                    Image tBgImg = tBgGo.AddComponent<Image>();
                    tBgImg.color = Color.white;
                    ApplySpriteBinding(tBgImg, nodeData);

                    GameObject checkGo = CreateChildRect(tBgGo, "Checkmark", Vector2.zero, Vector2.one);
                    Image checkImg = checkGo.AddComponent<Image>();
                    checkImg.color = Color.black;
                    RectTransform checkRect = checkGo.GetComponent<RectTransform>();
                    checkRect.offsetMin = new Vector2(4, 4);
                    checkRect.offsetMax = new Vector2(-4, -4);

                    GameObject tLblGo = CreateChildRect(go, "Label", Vector2.zero, Vector2.one);
                    RectTransform tLblRect = tLblGo.GetComponent<RectTransform>();
                    tLblRect.offsetMin = new Vector2(boxSize + 10, 0);
                    TextMeshProUGUI tLblTxt = tLblGo.AddComponent<TextMeshProUGUI>();
                    tLblTxt.text = nodeData.text;
                    tLblTxt.color = fontColor;
                    tLblTxt.fontSize = fontSize;
                    tLblTxt.alignment = TextAlignmentOptions.MidlineLeft;
                    tLblTxt.enableWordWrapping = false;

                    toggle.targetGraphic = tBgImg;
                    toggle.graphic = checkImg;
                    return go.transform;
                }
                case "slider":
                {
                    Slider slider = go.AddComponent<Slider>();
                    slider.value = Mathf.Clamp01(nodeData.value);

                    GameObject sBgGo = CreateChildRect(go, "Background", new Vector2(0, 0.25f), new Vector2(1, 0.75f));
                    Image sBgImg = sBgGo.AddComponent<Image>();
                    sBgImg.color = bgColor;
                    ApplySpriteBinding(sBgImg, nodeData);

                    GameObject fillAreaGo = CreateChildRect(go, "Fill Area", Vector2.zero, Vector2.one, new Vector2(5, 0), new Vector2(-15, 0));
                    GameObject fillGo = CreateChildRect(fillAreaGo, "Fill", Vector2.zero, Vector2.one);
                    Image fillImg = fillGo.AddComponent<Image>();
                    fillImg.color = fontColor;
                    ApplySpriteBinding(fillImg, nodeData);

                    GameObject handleAreaGo = CreateChildRect(go, "Handle Slide Area", Vector2.zero, Vector2.one, new Vector2(10, 0), new Vector2(-10, 0));
                    GameObject handleGo = CreateChildRect(handleAreaGo, "Handle", Vector2.zero, Vector2.one);
                    RectTransform handleRect = handleGo.GetComponent<RectTransform>();
                    handleRect.sizeDelta = new Vector2(20, 0);
                    Image handleImg = handleGo.AddComponent<Image>();
                    handleImg.color = Color.white;
                    ApplySpriteBinding(handleImg, nodeData);

                    slider.targetGraphic = handleImg;
                    slider.fillRect = fillGo.GetComponent<RectTransform>();
                    slider.handleRect = handleRect;
                    return go.transform;
                }
                case "dropdown":
                {
                    Image dBgImg = go.AddComponent<Image>();
                    dBgImg.color = bgColor;
                    ApplySpriteBinding(dBgImg, nodeData);

                    TMP_Dropdown dropdown = go.AddComponent<TMP_Dropdown>();

                    GameObject dLblGo = CreateChildRect(go, "Label", Vector2.zero, Vector2.one, new Vector2(10, 0), new Vector2(-30, 0));
                    TextMeshProUGUI dLblTxt = dLblGo.AddComponent<TextMeshProUGUI>();
                    dLblTxt.color = fontColor;
                    dLblTxt.fontSize = fontSize;
                    dLblTxt.alignment = TextAlignmentOptions.MidlineLeft;
                    dLblTxt.enableWordWrapping = false;

                    GameObject arrowGo = CreateChildRect(go, "Arrow", new Vector2(1, 0.5f), new Vector2(1, 0.5f));
                    RectTransform arrowRect = arrowGo.GetComponent<RectTransform>();
                    arrowRect.sizeDelta = new Vector2(20, 20);
                    arrowRect.anchoredPosition = new Vector2(-15, 0);
                    Image arrowImg = arrowGo.AddComponent<Image>();
                    arrowImg.color = fontColor;

                    GameObject templateGo = CreateChildRect(go, "Template", new Vector2(0, 0), new Vector2(1, 0));
                    RectTransform templateRect = templateGo.GetComponent<RectTransform>();
                    templateRect.pivot = new Vector2(0.5f, 1);
                    templateRect.sizeDelta = new Vector2(0, 150);
                    templateRect.anchoredPosition = new Vector2(0, -2);
                    Image tempImg = templateGo.AddComponent<Image>();
                    tempImg.color = Color.white;

                    ScrollRect tempScroll = templateGo.AddComponent<ScrollRect>();
                    tempScroll.horizontal = false;
                    tempScroll.vertical = true;
                    templateGo.SetActive(false);

                    GameObject dViewportGo = CreateChildRect(templateGo, "Viewport", Vector2.zero, Vector2.one);
                    dViewportGo.AddComponent<Image>().color = Color.white;
                    dViewportGo.AddComponent<Mask>();

                    GameObject dContentGo = CreateChildRect(dViewportGo, "Content", new Vector2(0, 1), new Vector2(1, 1));
                    RectTransform dContentRect = dContentGo.GetComponent<RectTransform>();
                    dContentRect.pivot = new Vector2(0.5f, 1);
                    dContentRect.sizeDelta = new Vector2(0, 28);

                    GameObject itemGo = CreateChildRect(dContentGo, "Item", new Vector2(0, 0.5f), new Vector2(1, 0.5f));
                    RectTransform itemRect = itemGo.GetComponent<RectTransform>();
                    itemRect.sizeDelta = new Vector2(0, 28);
                    Toggle itemToggle = itemGo.AddComponent<Toggle>();

                    GameObject itemBgGo = CreateChildRect(itemGo, "Item Background", Vector2.zero, Vector2.one);
                    Image itemBgImg = itemBgGo.AddComponent<Image>();
                    itemBgImg.color = Color.white;

                    GameObject itemCheckGo = CreateChildRect(itemGo, "Item Checkmark", new Vector2(0, 0.5f), new Vector2(0, 0.5f));
                    RectTransform itemCheckRect = itemCheckGo.GetComponent<RectTransform>();
                    itemCheckRect.sizeDelta = new Vector2(20, 20);
                    itemCheckRect.anchoredPosition = new Vector2(15, 0);
                    Image itemCheckImg = itemCheckGo.AddComponent<Image>();
                    itemCheckImg.color = Color.black;

                    GameObject itemLblGo = CreateChildRect(itemGo, "Item Label", Vector2.zero, Vector2.one, new Vector2(30, 0), new Vector2(-10, 0));
                    TextMeshProUGUI itemLblTxt = itemLblGo.AddComponent<TextMeshProUGUI>();
                    itemLblTxt.color = Color.black;
                    itemLblTxt.fontSize = fontSize;
                    itemLblTxt.alignment = TextAlignmentOptions.MidlineLeft;
                    itemLblTxt.enableWordWrapping = false;

                    itemToggle.targetGraphic = itemBgImg;
                    itemToggle.graphic = itemCheckImg;

                    tempScroll.viewport = dViewportGo.GetComponent<RectTransform>();
                    tempScroll.content = dContentRect;

                    dropdown.targetGraphic = dBgImg;
                    dropdown.template = templateRect;
                    dropdown.captionText = dLblTxt;
                    dropdown.itemText = itemLblTxt;

                    if (nodeData.options != null && nodeData.options.Count > 0)
                    {
                        dropdown.ClearOptions();
                        List<TMP_Dropdown.OptionData> optList = new List<TMP_Dropdown.OptionData>();
                        foreach (var opt in nodeData.options)
                        {
                            optList.Add(new TMP_Dropdown.OptionData(opt));
                        }
                        dropdown.AddOptions(optList);
                    }

                    return go.transform;
                }
                default:
                    Debug.LogWarning($"[HtmlToUGUIBaker] 未知节点类型: {nodeData.type}");
                    return go.transform;
            }
        }

        private void ApplySpriteBinding(Image image, UIDataNode nodeData)
        {
            if (image == null || nodeData == null)
            {
                return;
            }

            if (TryResolveSprite(nodeData, out Sprite sprite, out string matchedKey))
            {
                image.sprite = sprite;
                image.type = Image.Type.Simple;
                image.preserveAspect = true;
                return;
            }

            if (nodeData.type != null && nodeData.type.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                string failKey = GetSpriteLookupKey(nodeData);
                spriteBindingFailures.Add($"{nodeData.name} -> {failKey}");
            }
        }

        private bool TryResolveSprite(UIDataNode nodeData, out Sprite sprite, out string matchedKey)
        {
            sprite = null;
            matchedKey = string.Empty;

            if (nodeData == null)
            {
                return false;
            }

            string[] candidates = GetSpriteLookupCandidates(nodeData);
            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                if (spriteIndex.TryGetValue(candidate, out sprite) && sprite != null)
                {
                    matchedKey = candidate;
                    return true;
                }

                string normalized = NormalizeSpriteKey(candidate);
                if (!string.IsNullOrEmpty(normalized) && spriteIndex.TryGetValue(normalized, out sprite) && sprite != null)
                {
                    matchedKey = normalized;
                    return true;
                }

                if (TrySearchSpriteInProject(candidate, out sprite, out matchedKey))
                {
                    return true;
                }
            }

            string assetPath = ResolveSpriteAssetPath(nodeData);
            if (!string.IsNullOrEmpty(assetPath))
            {
                sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                if (sprite != null)
                {
                    matchedKey = assetPath;
                    return true;
                }
            }

            return false;
        }

        private string GetSpriteLookupKey(UIDataNode nodeData)
        {
            if (!string.IsNullOrEmpty(nodeData.spriteName))
            {
                return Path.GetFileNameWithoutExtension(nodeData.spriteName);
            }

            if (!string.IsNullOrEmpty(nodeData.spritePath))
            {
                return Path.GetFileNameWithoutExtension(nodeData.spritePath);
            }

            if (nodeData.type != null && nodeData.type.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                return nodeData.name;
            }

            return string.Empty;
        }

        private string[] GetSpriteLookupCandidates(UIDataNode nodeData)
        {
            var list = new List<string>();

            void AddCandidate(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                string trimmed = value.Trim();
                if (!list.Contains(trimmed))
                {
                    list.Add(trimmed);
                }

                string stripped = StripCommonSpriteSuffixes(trimmed);
                if (!string.IsNullOrEmpty(stripped) && !list.Contains(stripped))
                {
                    list.Add(stripped);
                }
            }

            AddCandidate(nodeData.spriteName);
            AddCandidate(nodeData.spritePath);
            AddCandidate(Path.GetFileNameWithoutExtension(nodeData.spriteName));
            AddCandidate(Path.GetFileNameWithoutExtension(nodeData.spritePath));
            AddCandidate(nodeData.name);
            AddCandidate(Path.GetFileNameWithoutExtension(nodeData.name));

            return list.ToArray();
        }

        private static string StripCommonSpriteSuffixes(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            string stripped = value.Trim();
            string[] suffixes = { "Icon", "Btn", "Button", "Img", "Image", "Sprite" };
            foreach (string suffix in suffixes)
            {
                if (stripped.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && stripped.Length > suffix.Length)
                {
                    return stripped.Substring(0, stripped.Length - suffix.Length);
                }
            }

            return stripped;
        }

        private string ResolveSpriteAssetPath(UIDataNode nodeData)
        {
            if (nodeData == null)
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(spriteFolderPath) || !AssetDatabase.IsValidFolder(spriteFolderPath))
            {
                return string.Empty;
            }

            string[] candidates = new[]
            {
                nodeData.spritePath,
                nodeData.spriteName,
                nodeData.name
            };

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string normalized = candidate.Replace("\\", "/");
                if (normalized.StartsWith(spriteFolderPath + "/", StringComparison.OrdinalIgnoreCase) && File.Exists(normalized))
                {
                    return normalized;
                }
            }

            return string.Empty;
        }

        private bool TrySearchSpriteInProject(string candidate, out Sprite sprite, out string matchedKey)
        {
            sprite = null;
            matchedKey = string.Empty;

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            if (string.IsNullOrEmpty(spriteFolderPath) || !AssetDatabase.IsValidFolder(spriteFolderPath))
            {
                return false;
            }

            string normalizedCandidate = NormalizeSpriteKey(candidate);
            string filter = string.IsNullOrWhiteSpace(normalizedCandidate) ? candidate : normalizedCandidate;
            string[] guids = AssetDatabase.FindAssets(filter, new[] { spriteFolderPath });

            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                Sprite found = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                if (found == null)
                {
                    continue;
                }

                string fileKey = Path.GetFileNameWithoutExtension(assetPath);
                string normalizedFileKey = NormalizeSpriteKey(fileKey);
                string normalizedSpriteName = NormalizeSpriteKey(found.name);

                if (MatchesCandidate(normalizedCandidate, fileKey, normalizedFileKey, normalizedSpriteName))
                {
                    sprite = found;
                    matchedKey = assetPath;
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesCandidate(string normalizedCandidate, string fileKey, string normalizedFileKey, string normalizedSpriteName)
        {
            if (string.IsNullOrWhiteSpace(normalizedCandidate))
            {
                return false;
            }

            if (normalizedCandidate == normalizedFileKey || normalizedCandidate == normalizedSpriteName)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(normalizedFileKey) && normalizedFileKey.Contains(normalizedCandidate))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(normalizedSpriteName) && normalizedSpriteName.Contains(normalizedCandidate))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(fileKey))
            {
                string stripped = NormalizeSpriteKey(StripCommonSpriteSuffixes(fileKey));
                if (!string.IsNullOrEmpty(stripped) && (stripped == normalizedCandidate || stripped.Contains(normalizedCandidate)))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeSpriteKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            char[] chars = key.ToLowerInvariant().ToCharArray();
            var sb = new System.Text.StringBuilder(chars.Length);
            foreach (char c in chars)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private TextAlignmentOptions ParseTextAlign(string alignStr)
        {
            if (string.IsNullOrEmpty(alignStr))
            {
                return TextAlignmentOptions.Midline;
            }

            switch (alignStr.ToLower())
            {
                case "left":
                case "start":
                    return TextAlignmentOptions.MidlineLeft;
                case "right":
                case "end":
                    return TextAlignmentOptions.MidlineRight;
                case "center":
                default:
                    return TextAlignmentOptions.Midline;
            }
        }

        private GameObject CreateChildRect(GameObject parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2? offsetMin = null, Vector2? offsetMax = null)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin ?? Vector2.zero;
            rect.offsetMax = offsetMax ?? Vector2.zero;
            return go;
        }

        private Color ParseHexColor(string hex, Color defaultColor)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return defaultColor;
            }

            if (ColorUtility.TryParseHtmlString(hex, out Color color))
            {
                return color;
            }

            return defaultColor;
        }
    }
}
