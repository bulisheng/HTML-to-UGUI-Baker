using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Editor.UIBaker
{
    public class EffectImageToHtmlMultiWindow : EditorWindow
    {
        private enum PlatformPreset
        {
            OpenAI,
            Gemini,
            Doubao,
            Custom
        }

        private const string PrefProvider = "HtmlToUGUI_MultiImageToHtml_Provider";
        private const string PrefApiKey = "HtmlToUGUI_MultiImageToHtml_ApiKey";
        private const string PrefModel = "HtmlToUGUI_MultiImageToHtml_Model";
        private const string PrefBaseUrl = "HtmlToUGUI_MultiImageToHtml_BaseUrl";
        private const string PrefSpriteFolder = "HtmlToUGUI_MultiImageToHtml_SpriteFolder";

        private PlatformPreset preset = PlatformPreset.OpenAI;
        private string apiKey = "";
        private string modelName = "gpt-4.1";
        private string apiBaseUrl = "https://api.openai.com/v1/chat/completions";
        private string systemHint = "你是专业的 UI 原型开发专家。请根据图片生成符合 UI-DSL 规范的 HTML。";

        private string screenshotPath = "";
        private Texture2D screenshotPreview;
        private byte[] screenshotBytes;

        private DefaultAsset spriteFolderAsset;
        private string spriteFolderPath = "";
        private readonly List<string> spriteNameList = new List<string>();
        private readonly Dictionary<string, string> envValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> envKeyNames = new List<string>();
        private string envFilePath = "Assets/HtmlToUGUI/.env";
        private string selectedEnvKeyName = "";

        private string generatedHtml = "";
        private string statusMessage = "";
        private MessageType statusType = MessageType.Info;
        private Vector2 inputScroll;
        private Vector2 outputScroll;

        [MenuItem("Tools/UI Architecture/Effect Image Recognition -> HTML (Multi Platform)")]
        public static void ShowWindow()
        {
            GetWindow<EffectImageToHtmlMultiWindow>("Effect Image Recognition");
        }

        private void OnEnable()
        {
            preset = (PlatformPreset)EditorPrefs.GetInt(PrefProvider, (int)PlatformPreset.OpenAI);
            apiKey = EditorPrefs.GetString(PrefApiKey, Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "");
            modelName = EditorPrefs.GetString(PrefModel, modelName);
            apiBaseUrl = EditorPrefs.GetString(PrefBaseUrl, apiBaseUrl);
            ApplyPreset(false);

            spriteFolderPath = EditorPrefs.GetString(PrefSpriteFolder, "");
            if (!string.IsNullOrEmpty(spriteFolderPath))
            {
                spriteFolderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(spriteFolderPath);
                RefreshSpriteNames();
            }

            LoadEnvFile(envFilePath, false);
            if (envKeyNames.Count > 0 && string.IsNullOrEmpty(apiKey))
            {
                selectedEnvKeyName = envKeyNames[0];
                ApplySelectedEnvKey();
            }
        }

        private void DrawSettings()
        {
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("平台预设", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            preset = (PlatformPreset)EditorGUILayout.EnumPopup("平台", preset);
            apiKey = EditorGUILayout.PasswordField("密钥", apiKey);
            modelName = EditorGUILayout.TextField("模型", modelName);
            apiBaseUrl = EditorGUILayout.TextField("接口地址", apiBaseUrl);
            if (EditorGUI.EndChangeCheck())
            {
                ApplyPreset();
                PersistPrefs();
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("根据 API Key 识别平台", GUILayout.Height(22), GUILayout.Width(180)))
            {
                preset = ResolvePresetFromApiKey(apiKey);
                ApplyPreset();
                PersistPrefs();
            }
            EditorGUILayout.EndHorizontal();

            systemHint = EditorGUILayout.TextArea(systemHint, GUILayout.MinHeight(44));
            EditorGUILayout.HelpBox(
                "OpenAI、Gemini、豆包和其他兼容平台都可以在这里使用。选择预设后会自动填写模型名和接口地址。",
                MessageType.None
            );

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(".env 导入", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            envFilePath = EditorGUILayout.TextField("Env 路径", envFilePath);
            if (GUILayout.Button("加载", GUILayout.Width(60)))
            {
                LoadEnvFile(envFilePath, true);
            }
            EditorGUILayout.EndHorizontal();

            if (envKeyNames.Count > 0)
            {
                int selectedIndex = Mathf.Max(0, envKeyNames.IndexOf(selectedEnvKeyName));
                int newIndex = EditorGUILayout.Popup("Key 变量", selectedIndex, envKeyNames.ToArray());
                if (newIndex != selectedIndex)
                {
                selectedEnvKeyName = envKeyNames[newIndex];
                ApplySelectedEnvKey();
                PersistPrefs();
            }

                if (GUILayout.Button("按当前 Key 自动切换平台", GUILayout.Height(24)))
                {
                    ApplySelectedEnvKey();
                    PersistPrefs();
                }
            }
            else
            {
                EditorGUILayout.HelpBox("未从 .env 文件加载到任何 Key。", MessageType.None);
            }

            GUILayout.EndVertical();
        }

        private void DrawStatusBanner()
        {
            if (string.IsNullOrWhiteSpace(statusMessage))
            {
                return;
            }

            EditorGUILayout.HelpBox(statusMessage, statusType);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("效果图识别 -> HTML", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("选择一张效果图，再选一个资源文件夹，然后生成符合 UI-DSL 的 HTML。", MessageType.Info);

            DrawSettings();
            DrawStatusBanner();

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            DrawInputPanel();
            DrawOutputPanel();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            DrawActions();
        }

        private void DrawInputPanel()
        {
            GUILayout.BeginVertical(GUILayout.Width(position.width * 0.42f));
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("输入", EditorStyles.boldLabel);

            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("效果图", EditorStyles.boldLabel);
            if (GUILayout.Button("选择效果图", GUILayout.Height(28)))
            {
                PickScreenshot();
            }

            if (!string.IsNullOrEmpty(screenshotPath))
            {
                EditorGUILayout.LabelField("图片路径", screenshotPath);
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("资源文件夹", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            spriteFolderAsset = (DefaultAsset)EditorGUILayout.ObjectField("图片资源文件夹", spriteFolderAsset, typeof(DefaultAsset), false);
            if (EditorGUI.EndChangeCheck())
            {
                spriteFolderPath = spriteFolderAsset != null ? AssetDatabase.GetAssetPath(spriteFolderAsset) : "";
                EditorPrefs.SetString(PrefSpriteFolder, spriteFolderPath);
                RefreshSpriteNames();
            }

            if (GUILayout.Button("浏览资源文件夹...", GUILayout.Height(24)))
            {
                string abs = EditorUtility.OpenFolderPanel("选择图片资源文件夹", Application.dataPath, "");
                if (!string.IsNullOrEmpty(abs))
                {
                    string projectPath = AbsoluteToProjectPath(abs);
                    if (!string.IsNullOrEmpty(projectPath))
                    {
                        spriteFolderPath = projectPath;
                        spriteFolderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(projectPath);
                        EditorPrefs.SetString(PrefSpriteFolder, spriteFolderPath);
                        RefreshSpriteNames();
                    }
                    else
                    {
                        SetStatus("请选择 Assets 目录内的文件夹。", MessageType.Warning);
                    }
                }
            }

            EditorGUILayout.LabelField("可用图片数", spriteNameList.Count.ToString());
            int showCount = Mathf.Min(8, spriteNameList.Count);
            for (int i = 0; i < showCount; i++)
            {
                EditorGUILayout.LabelField("- " + spriteNameList[i]);
            }
            if (spriteNameList.Count > showCount)
            {
                EditorGUILayout.LabelField($"+ 还有 {spriteNameList.Count - showCount} 个");
            }
            GUILayout.EndVertical();

            inputScroll = EditorGUILayout.BeginScrollView(inputScroll, GUILayout.ExpandHeight(true));
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("效果图预览", EditorStyles.boldLabel);
            if (screenshotPreview != null)
            {
                float aspect = (float)screenshotPreview.width / Mathf.Max(1, screenshotPreview.height);
                Rect rect = GUILayoutUtility.GetRect(10, 260, GUILayout.ExpandWidth(true));
                rect.height = rect.width / Mathf.Max(0.01f, aspect);
                EditorGUI.DrawPreviewTexture(rect, screenshotPreview, null, ScaleMode.ScaleToFit);
            }
            else
            {
                EditorGUILayout.HelpBox("尚未选择效果图。", MessageType.None);
            }
            GUILayout.EndVertical();

            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("图片列表", EditorStyles.boldLabel);
            if (spriteNameList.Count == 0)
            {
                EditorGUILayout.HelpBox("尚未加载资源文件夹。", MessageType.None);
            }
            else
            {
                int listCount = Mathf.Min(24, spriteNameList.Count);
                for (int i = 0; i < listCount; i++)
                {
                    EditorGUILayout.LabelField("- " + spriteNameList[i]);
                }
            }
            GUILayout.EndVertical();
            EditorGUILayout.EndScrollView();

            GUILayout.EndVertical();
            GUILayout.EndVertical();
        }

        private void DrawOutputPanel()
        {
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("输出 HTML", EditorStyles.boldLabel);

            outputScroll = EditorGUILayout.BeginScrollView(outputScroll);
            generatedHtml = EditorGUILayout.TextArea(generatedHtml, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            GUILayout.EndVertical();
            GUILayout.EndVertical();
        }

        private void DrawActions()
        {
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("操作", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("生成 HTML", GUILayout.Height(32)))
            {
                GenerateHtml();
            }

            if (GUILayout.Button("复制 HTML", GUILayout.Height(32)))
            {
                if (!string.IsNullOrWhiteSpace(generatedHtml))
                {
                    GUIUtility.systemCopyBuffer = generatedHtml;
                }
            }

            if (GUILayout.Button("保存 HTML", GUILayout.Height(32)))
            {
                SaveHtmlToProject();
            }

            if (GUILayout.Button("复制提示词", GUILayout.Height(32)))
            {
                GUIUtility.systemCopyBuffer = BuildPrompt();
            }
            EditorGUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        private void PickScreenshot()
        {
            string path = EditorUtility.OpenFilePanel("Select UI Screenshot", "", "png,jpg,jpeg,bmp");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            screenshotPath = path;
            screenshotBytes = File.ReadAllBytes(path);

            if (screenshotPreview != null)
            {
                DestroyImmediate(screenshotPreview);
            }

            screenshotPreview = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            screenshotPreview.LoadImage(screenshotBytes);
            Repaint();
        }

        private void GenerateHtml()
        {
            if (screenshotBytes == null || screenshotBytes.Length == 0)
            {
                SetStatus("请先选择一张效果图。", MessageType.Warning);
                return;
            }

            apiKey = NormalizeSecret(apiKey);
            if (!ValidateCurrentProvider())
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                SetStatus("API Key 为空，已切换到复制提示词模式。", MessageType.Warning);
                GUIUtility.systemCopyBuffer = BuildPrompt();
                return;
            }

            try
            {
                string raw = CallOpenAICompatible();
                generatedHtml = ExtractHtml(raw);
                generatedHtml = NormalizeRootCanvasSize(generatedHtml);
                SetStatus("生成成功。", MessageType.Info);
                Repaint();
            }
            catch (Exception e)
            {
                string friendly = DescribeError(e.Message);
                SetStatus(friendly, MessageType.Warning);
                Debug.LogError($"[EffectImageToHtmlMultiWindow] 生成失败: {friendly}\n原始错误: {e.Message}");
                if (friendly.IndexOf("额度", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    friendly.IndexOf("quota", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    GUIUtility.systemCopyBuffer = BuildPrompt();
                }
            }
        }

        private string CallOpenAICompatible()
        {
            string mime = GuessMimeType(screenshotPath);
            string dataUrl = $"data:{mime};base64,{Convert.ToBase64String(screenshotBytes)}";
            string prompt = BuildPrompt();

            string requestJson =
                "{"
                + $"\"model\":{JsonEscape(modelName)},"
                + "\"messages\":["
                + $"{{\"role\":\"system\",\"content\":{JsonEscape(systemHint)}}},"
                + "{"
                + "\"role\":\"user\",\"content\":["
                + $"{{\"type\":\"text\",\"text\":{JsonEscape(prompt)}}},"
                + $"{{\"type\":\"image_url\",\"image_url\":{{\"url\":{JsonEscape(dataUrl)},\"detail\":\"high\"}}}}"
                + "]"
                + "}"
                + "],"
                + "\"temperature\":0.2"
                + "}";

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(5);
                if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out Uri baseUri))
                {
                    throw new Exception("API URL 格式不正确。");
                }

                client.BaseAddress = baseUri;
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                using (var content = new StringContent(requestJson, Encoding.UTF8, "application/json"))
                {
                    var response = client.PostAsync(baseUri, content).GetAwaiter().GetResult();
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (!response.IsSuccessStatusCode)
                    {
                        if (body.IndexOf("AuthenticationError", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            body.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            throw new Exception("API Key 无效或格式不正确，请检查平台、Key 类型和 .env 内容是否带了引号或空格。");
                        }

                        throw new Exception(body);
                    }

                    var parsed = JsonUtility.FromJson<ChatCompletionResponse>(body);
                    if (parsed == null || parsed.choices == null || parsed.choices.Length == 0)
                    {
                        throw new Exception("返回内容为空。");
                    }

                    return parsed.choices[0].message != null ? parsed.choices[0].message.content : "";
                }
            }
        }

        private void SaveHtmlToProject()
        {
            if (string.IsNullOrWhiteSpace(generatedHtml))
            {
                SetStatus("当前没有可保存的 HTML。", MessageType.Warning);
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject("保存 HTML", "GeneratedUI.html", "html", "请选择保存路径");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            File.WriteAllText(path, generatedHtml, Encoding.UTF8);
            AssetDatabase.Refresh();
        }

        private void RefreshSpriteNames()
        {
            spriteNameList.Clear();

            if (string.IsNullOrEmpty(spriteFolderPath) || !AssetDatabase.IsValidFolder(spriteFolderPath))
            {
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:Sprite", new[] { spriteFolderPath });
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                string fileName = Path.GetFileNameWithoutExtension(assetPath);
                if (!string.IsNullOrEmpty(fileName))
                {
                    spriteNameList.Add(fileName);
                }
            }

            spriteNameList.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private void LoadEnvFile(string path, bool persist)
        {
            envValues.Clear();
            envKeyNames.Clear();

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string fullPath = path;
            if (!Path.IsPathRooted(fullPath))
            {
                fullPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
            }

            if (!File.Exists(fullPath))
            {
                SetStatus(".env 文件不存在，请检查路径。", MessageType.Warning);
                return;
            }

            foreach (string rawLine in File.ReadAllLines(fullPath))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                {
                    continue;
                }

                int index = line.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, index).Trim();
                string value = NormalizeSecret(line.Substring(index + 1));
                envValues[key] = value;
            }

            foreach (string key in envValues.Keys)
            {
                envKeyNames.Add(key);
            }

            envKeyNames.Sort(StringComparer.OrdinalIgnoreCase);

            if (envKeyNames.Count > 0)
            {
                if (string.IsNullOrEmpty(selectedEnvKeyName) || !envValues.ContainsKey(selectedEnvKeyName))
                {
                    selectedEnvKeyName = envKeyNames[0];
                }

                ApplySelectedEnvKey();
                if (persist)
                {
                    PersistPrefs();
                }
            }
        }

        private void ApplySelectedEnvKey()
        {
            if (string.IsNullOrEmpty(selectedEnvKeyName) || !envValues.TryGetValue(selectedEnvKeyName, out string keyValue))
            {
                return;
            }

            apiKey = NormalizeSecret(keyValue);
            preset = ResolvePresetFromKeyName(selectedEnvKeyName);
            if (preset == PlatformPreset.Custom)
            {
                preset = ResolvePresetFromApiKey(apiKey);
            }
            ApplyPreset();
        }

        private static PlatformPreset ResolvePresetFromKeyName(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName))
            {
                return PlatformPreset.Custom;
            }

            string upper = keyName.ToUpperInvariant();
            if (upper.Contains("OPENAI"))
            {
                return PlatformPreset.OpenAI;
            }

            if (upper.Contains("GEMINI") || upper.Contains("GOOGLE"))
            {
                return PlatformPreset.Gemini;
            }

            if (upper.Contains("VOLCANO") || upper.Contains("DOUBAO"))
            {
                return PlatformPreset.Doubao;
            }

            return PlatformPreset.Custom;
        }

        private static PlatformPreset ResolvePresetFromApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return PlatformPreset.Custom;
            }

            string normalized = apiKey.Trim();
            string lower = normalized.ToLowerInvariant();

            if (normalized.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
            {
                return PlatformPreset.OpenAI;
            }

            if (normalized.StartsWith("AIza", StringComparison.OrdinalIgnoreCase))
            {
                return PlatformPreset.Gemini;
            }

            if (lower.Contains("volc") || lower.Contains("doubao") || lower.Contains("ark."))
            {
                return PlatformPreset.Doubao;
            }

            if (lower.Contains("gemini") || lower.Contains("google"))
            {
                return PlatformPreset.Gemini;
            }

            return PlatformPreset.Custom;
        }

        private string BuildPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("请根据这张 UI 效果图生成 HTML 原型代码，并严格遵守以下规则：");
            sb.AppendLine("1. 只能输出 HTML，不要 Markdown，不要解释。");
            sb.AppendLine("2. 必须只有一个根节点，data-u-type=\"div\"，data-u-name=\"root\" 或具体窗口名。");
            sb.AppendLine("3. 根节点 style 必须与目标分辨率一致，不要擅自改尺寸。");
            sb.AppendLine("4. 节点 data-u-name 必须使用小驼峰命名法。");
            sb.AppendLine("5. 允许的 data-u-type 只有 div, image, text, button, input, scroll, toggle, slider, dropdown。");
            sb.AppendLine("6. dropdown 必须使用 select/option。");
            sb.AppendLine("7. 图片、图标、徽标尽量使用 image，并写上 data-u-src。");
            sb.AppendLine("8. 如果提供了资源文件夹，请优先从以下文件名里匹配 spriteName / data-u-src：");

            if (spriteNameList.Count == 0)
            {
                sb.AppendLine("   - (none)");
            }
            else
            {
                int limit = Mathf.Min(spriteNameList.Count, 300);
                for (int i = 0; i < limit; i++)
                {
                    sb.AppendLine("   - " + spriteNameList[i]);
                }
            }

            sb.AppendLine("9. 尽量贴近原图布局，层级清晰，命名语义化。");
            sb.AppendLine("10. 不确定的图片可以保留为 image 节点，并起一个合理名字。");
            sb.AppendLine("11. 文本节点尽量保留真实内容、字号、对齐和颜色。");
            return sb.ToString();
        }

        private static string NormalizeSecret(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim().Trim('\uFEFF');
            if (trimmed.Length >= 2)
            {
                bool quoted = (trimmed.StartsWith("\"", StringComparison.Ordinal) && trimmed.EndsWith("\"", StringComparison.Ordinal)) ||
                              (trimmed.StartsWith("'", StringComparison.Ordinal) && trimmed.EndsWith("'", StringComparison.Ordinal));
                if (quoted)
                {
                    trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
                }
            }

            return trimmed;
        }

        private bool ValidateCurrentProvider()
        {
            if (string.IsNullOrWhiteSpace(apiBaseUrl))
            {
                SetStatus("API URL 不能为空。", MessageType.Warning);
                return false;
            }

            if (preset == PlatformPreset.Gemini && !string.IsNullOrWhiteSpace(apiKey) && !apiKey.StartsWith("AIza", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("当前选择的是 Gemini。请确认 API Key 是 Google AI Studio 的 Key，通常以 AIza 开头。", MessageType.Warning);
                return false;
            }

            if (preset == PlatformPreset.OpenAI && !string.IsNullOrWhiteSpace(apiKey) && !apiKey.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("当前选择的是 OpenAI。请确认 API Key 格式正确，通常以 sk- 开头。", MessageType.Warning);
                return false;
            }

            return true;
        }

        private void ApplyPreset(bool persist = true)
        {
            switch (preset)
            {
                case PlatformPreset.OpenAI:
                    apiBaseUrl = "https://api.openai.com/v1/chat/completions";
                    if (string.IsNullOrWhiteSpace(modelName) || modelName.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) || modelName.StartsWith("doubao", StringComparison.OrdinalIgnoreCase))
                    {
                        modelName = "gpt-4.1";
                    }
                    break;
                case PlatformPreset.Gemini:
                    apiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
                    if (string.IsNullOrWhiteSpace(modelName) || modelName.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) || modelName.StartsWith("doubao", StringComparison.OrdinalIgnoreCase))
                    {
                        modelName = "gemini-3-flash-preview";
                    }
                    break;
                case PlatformPreset.Doubao:
                    apiBaseUrl = "https://ark.cn-beijing.volces.com/api/v3/chat/completions";
                    if (string.IsNullOrWhiteSpace(modelName) || modelName.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) || modelName.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
                    {
                        modelName = "doubao-seed-1-6-250615";
                    }
                    break;
                case PlatformPreset.Custom:
                    if (string.IsNullOrWhiteSpace(apiBaseUrl))
                    {
                        apiBaseUrl = "https://your-provider.example/v1/chat/completions";
                    }
                    break;
            }

            if (persist)
            {
                PersistPrefs();
            }
        }

        private void PersistPrefs()
        {
            EditorPrefs.SetInt(PrefProvider, (int)preset);
            EditorPrefs.SetString(PrefApiKey, apiKey);
            EditorPrefs.SetString(PrefModel, modelName);
            EditorPrefs.SetString(PrefBaseUrl, apiBaseUrl);
        }

        private static string ExtractHtml(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            int start = text.IndexOf("<div", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                start = text.IndexOf("<", StringComparison.OrdinalIgnoreCase);
            }

            int end = text.LastIndexOf("</div>", StringComparison.OrdinalIgnoreCase);
            if (start >= 0 && end >= start)
            {
                end += "</div>".Length;
                return text.Substring(start, end - start).Trim();
            }

            return text.Trim().Trim('`');
        }

        private static string DescribeError(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage))
            {
                return "生成失败：错误信息为空。";
            }

            if (rawMessage.IndexOf("insufficient_quota", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "额度不足或尚未开通计费，请检查 Billing / Usage Limits / Project Budget。";
            }

            if (rawMessage.IndexOf("rate_limit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "触发了频率限制，请稍后再试。";
            }

            if (rawMessage.IndexOf("invalid_api_key", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawMessage.IndexOf("authentication", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "API Key 无效或配置不正确。";
            }

            if (rawMessage.IndexOf("sending the request", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawMessage.IndexOf("HttpRequestException", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "请求发送失败，请检查 API URL、网络连接和代理设置。";
            }

            return "生成失败，请检查平台配置或切换到其他平台。";
        }

        private void SetStatus(string message, MessageType type)
        {
            statusMessage = message;
            statusType = type;
            Repaint();
        }

        private string NormalizeRootCanvasSize(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return html;
            }

            if (!TryGetTargetResolution(out int targetWidth, out int targetHeight))
            {
                return html;
            }

            Match match = Regex.Match(
                html,
                @"<(?<tag>div|section|main|article|body)\b[^>]*data-u-type\s*=\s*""div""[^>]*style\s*=\s*""(?<style>[^""]*)""",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!match.Success)
            {
                return html;
            }

            string style = match.Groups["style"].Value;
            style = Regex.Replace(style, @"width\s*:\s*[^;]+;?", $"width: {targetWidth}px;", RegexOptions.IgnoreCase);
            style = Regex.Replace(style, @"height\s*:\s*[^;]+;?", $"height: {targetHeight}px;", RegexOptions.IgnoreCase);

            if (!Regex.IsMatch(style, @"\bwidth\s*:", RegexOptions.IgnoreCase))
            {
                style = $"width: {targetWidth}px; " + style;
            }

            if (!Regex.IsMatch(style, @"\bheight\s*:", RegexOptions.IgnoreCase))
            {
                style = $"height: {targetHeight}px; " + style;
            }

            string updatedTag = match.Value.Replace(match.Groups["style"].Value, style);
            return html.Substring(0, match.Index) + updatedTag + html.Substring(match.Index + match.Length);
        }

        private bool TryGetTargetResolution(out int width, out int height)
        {
            width = 0;
            height = 0;

            if (TryParseResolution(systemHint, out width, out height))
            {
                return true;
            }

            return false;
        }

        private static bool TryParseResolution(string text, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            Match named = Regex.Match(
                text,
                @"(?:目标分辨率|分辨率|resolution)[^\d]*(\d{3,5})\s*(?:x|X|×|\s)\s*(\d{3,5})",
                RegexOptions.IgnoreCase);
            if (named.Success && int.TryParse(named.Groups[1].Value, out width) && int.TryParse(named.Groups[2].Value, out height))
            {
                return true;
            }

            Match explicitStyle = Regex.Match(
                text,
                @"width\s*[:=]?\s*(\d{3,5})\s*px?[^\d]+height\s*[:=]?\s*(\d{3,5})\s*px?",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (explicitStyle.Success && int.TryParse(explicitStyle.Groups[1].Value, out width) && int.TryParse(explicitStyle.Groups[2].Value, out height))
            {
                return true;
            }

            Match generic = Regex.Match(text, @"\b(\d{3,5})\s*(?:x|X|×|\s)\s*(\d{3,5})\b");
            if (generic.Success && int.TryParse(generic.Groups[1].Value, out width) && int.TryParse(generic.Groups[2].Value, out height))
            {
                return true;
            }

            return false;
        }

        private static string GuessMimeType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".bmp":
                    return "image/bmp";
                case ".gif":
                    return "image/gif";
                default:
                    return "image/png";
            }
        }

        private static string JsonEscape(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        private static string AbsoluteToProjectPath(string absolutePath)
        {
            absolutePath = absolutePath.Replace("\\", "/");
            string dataPath = Application.dataPath.Replace("\\", "/");
            if (!absolutePath.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return "Assets" + absolutePath.Substring(dataPath.Length);
        }

        [Serializable]
        private class ChatCompletionResponse
        {
            public Choice[] choices;
        }

        [Serializable]
        private class Choice
        {
            public Message message;
        }

        [Serializable]
        private class Message
        {
            public string content;
        }
    }
}

