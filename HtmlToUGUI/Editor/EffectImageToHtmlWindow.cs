using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Editor.UIBaker
{
    public class EffectImageToHtmlWindow : EditorWindow
    {
        private enum ProviderKind
        {
            OpenAI,
            GeminiOpenAICompatible,
            CustomOpenAICompatible
        }

        private const string PREFS_API_KEY = "HtmlToUGUI_ImageToHtml_OpenAIKey";
        private const string PREFS_MODEL = "HtmlToUGUI_ImageToHtml_Model";
        private const string PREFS_SPRITE_FOLDER = "HtmlToUGUI_ImageToHtml_SpriteFolder";
        private const string PREFS_PROVIDER = "HtmlToUGUI_ImageToHtml_Provider";
        private const string PREFS_BASE_URL = "HtmlToUGUI_ImageToHtml_BaseUrl";

        private ProviderKind providerKind = ProviderKind.OpenAI;
        private string apiKey;
        private string modelName = "gpt-4.1";
        private string apiBaseUrl = "https://api.openai.com/v1/chat/completions";
        private string systemHint = "你是专业的 UI 原型开发专家。请根据效果图生成符合 UI-DSL 规范的 HTML。";

        private string screenshotPath = "";
        private Texture2D screenshotPreview;
        private byte[] screenshotBytes;

        private DefaultAsset spriteFolderAsset;
        private string spriteFolderPath = "";
        private readonly List<string> spriteNameList = new List<string>();

        private string generatedHtml = "";
        private Vector2 leftScroll;
        private Vector2 rightScroll;
        private string statusMessage = "";
        private MessageType statusType = MessageType.Info;

        [MenuItem("Tools/UI Architecture/效果图识别 -> HTML")]
        public static void ShowWindow()
        {
            GetWindow<EffectImageToHtmlWindow>("效果图识别");
        }

        private void OnEnable()
        {
            apiKey = EditorPrefs.GetString(PREFS_API_KEY, Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "");
            modelName = EditorPrefs.GetString(PREFS_MODEL, modelName);
            providerKind = (ProviderKind)EditorPrefs.GetInt(PREFS_PROVIDER, (int)ProviderKind.OpenAI);
            apiBaseUrl = EditorPrefs.GetString(PREFS_BASE_URL, apiBaseUrl);
            ApplyProviderPresetIfNeeded(false);

            spriteFolderPath = EditorPrefs.GetString(PREFS_SPRITE_FOLDER, "");
            if (!string.IsNullOrEmpty(spriteFolderPath))
            {
                spriteFolderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(spriteFolderPath);
                RefreshSpriteNames();
            }
        }

        private void ApplyProviderPresetIfNeeded(bool persist = true)
        {
            switch (providerKind)
            {
                case ProviderKind.OpenAI:
                    if (string.IsNullOrWhiteSpace(apiBaseUrl))
                    {
                        apiBaseUrl = "https://api.openai.com/v1/chat/completions";
                    }
                    break;
                case ProviderKind.GeminiOpenAICompatible:
                    apiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
                    break;
                case ProviderKind.CustomOpenAICompatible:
                    if (string.IsNullOrWhiteSpace(apiBaseUrl))
                    {
                        apiBaseUrl = "https://your-provider.example/v1/chat/completions";
                    }
                    break;
            }

            if (persist)
            {
                EditorPrefs.SetInt(PREFS_PROVIDER, (int)providerKind);
                EditorPrefs.SetString(PREFS_BASE_URL, apiBaseUrl);
                EditorPrefs.SetString(PREFS_MODEL, modelName);
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("效果图识别 -> HTML", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("选择一张 UI 效果图，再可选一个图片资源文件夹，点击生成后会输出符合 UI-DSL 的 HTML。", MessageType.Info);

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

        private void DrawSettings()
        {
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("OpenAI 设置", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            apiKey = EditorGUILayout.PasswordField("API Key", apiKey);
            modelName = EditorGUILayout.TextField("Model", modelName);
            apiBaseUrl = EditorGUILayout.TextField("API URL", apiBaseUrl);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString(PREFS_API_KEY, apiKey);
                EditorPrefs.SetString(PREFS_MODEL, modelName);
            }

            EditorGUILayout.Space(4);
            systemHint = EditorGUILayout.TextArea(systemHint, GUILayout.MinHeight(40));
            GUILayout.EndVertical();
        }

        private void DrawInputPanel()
        {
            GUILayout.BeginVertical(GUILayout.Width(position.width * 0.42f));
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("输入", EditorStyles.boldLabel);

            if (GUILayout.Button("选择效果图", GUILayout.Height(28)))
            {
                PickScreenshot();
            }

            if (!string.IsNullOrEmpty(screenshotPath))
            {
                EditorGUILayout.LabelField("图片路径", screenshotPath);
            }

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

            GUILayout.Space(6);
            EditorGUILayout.LabelField("资源文件夹", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            spriteFolderAsset = (DefaultAsset)EditorGUILayout.ObjectField("图片资源文件夹", spriteFolderAsset, typeof(DefaultAsset), false);
            if (EditorGUI.EndChangeCheck())
            {
                spriteFolderPath = spriteFolderAsset != null ? AssetDatabase.GetAssetPath(spriteFolderAsset) : "";
                EditorPrefs.SetString(PREFS_SPRITE_FOLDER, spriteFolderPath);
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
                        spriteFolderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(spriteFolderPath);
                        EditorPrefs.SetString(PREFS_SPRITE_FOLDER, spriteFolderPath);
                        RefreshSpriteNames();
                    }
                    else
                    {
                        Debug.LogWarning("[EffectImageToHtmlWindow] 请选择 Assets 目录下的文件夹。");
                    }
                }
            }

            EditorGUILayout.LabelField("可用图片数", spriteNameList.Count.ToString());

            if (spriteNameList.Count > 0)
            {
                EditorGUILayout.LabelField("前几个名字");
                int showCount = Mathf.Min(12, spriteNameList.Count);
                for (int i = 0; i < showCount; i++)
                {
                    EditorGUILayout.LabelField("- " + spriteNameList[i]);
                }
            }

            GUILayout.EndVertical();
            GUILayout.EndVertical();
        }

        private void DrawOutputPanel()
        {
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("输出 HTML", EditorStyles.boldLabel);

            rightScroll = EditorGUILayout.BeginScrollView(rightScroll);
            generatedHtml = EditorGUILayout.TextArea(generatedHtml, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            GUILayout.EndVertical();
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

            if (GUILayout.Button("保存 HTML 到工程", GUILayout.Height(32)))
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
            string path = EditorUtility.OpenFilePanel("选择 UI 效果图", "", "png,jpg,jpeg,bmp");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            LoadScreenshotFromPath(path);
        }

        private void LoadScreenshotFromPath(string path)
        {
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
                Debug.LogError("[EffectImageToHtmlWindow] 请先选择一张效果图。");
                return;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Debug.LogWarning("[EffectImageToHtmlWindow] 未配置 API Key，已转为复制提示词模式。");
                GUIUtility.systemCopyBuffer = BuildPrompt();
                return;
            }

            try
            {
                generatedHtml = CallOpenAI();
                generatedHtml = ExtractHtml(generatedHtml);
                generatedHtml = NormalizeRootCanvasSize(generatedHtml);
                SetStatus("生成成功。", MessageType.Info);
                Repaint();
            }
            catch (Exception e)
            {
                string friendly = DescribeOpenAIError(e.Message);
                SetStatus(friendly, MessageType.Warning);
                Debug.LogError($"[EffectImageToHtmlWindow] 生成失败: {friendly}\n原始错误: {e.Message}");

                if (friendly.Contains("额度"))
                {
                    GUIUtility.systemCopyBuffer = BuildPrompt();
                }
            }
        }

        private void SetStatus(string message, MessageType type)
        {
            statusMessage = message;
            statusType = type;
            Repaint();
        }

        private string CallOpenAI()
        {
            string mime = GuessMimeType(screenshotPath);
            string dataUrl = $"data:{mime};base64,{Convert.ToBase64String(screenshotBytes)}";
            string prompt = BuildPrompt();

            var request = new StringBuilder();
            request.Append("{");
            request.Append("\"model\":").Append(JsonEscape(modelName)).Append(",");
            request.Append("\"messages\":[{");
            request.Append("\"role\":\"system\",\"content\":").Append(JsonEscape(systemHint)).Append("},");
            request.Append("{\"role\":\"user\",\"content\":[");
            request.Append("{\"type\":\"text\",\"text\":").Append(JsonEscape(prompt)).Append("},");
            request.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":").Append(JsonEscape(dataUrl)).Append(",\"detail\":\"high\"}}");
            request.Append("]}],");
            request.Append("\"temperature\":0.2");
            request.Append("}");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(5);
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                using (var content = new StringContent(request.ToString(), Encoding.UTF8, "application/json"))
                {
                    var response = client.PostAsync(apiBaseUrl, content).GetAwaiter().GetResult();
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception(body);
                    }

                    var parsed = JsonUtility.FromJson<ChatCompletionResponse>(body);
                    if (parsed == null || parsed.choices == null || parsed.choices.Length == 0)
                    {
                        throw new Exception("OpenAI 返回为空。");
                    }

                    return parsed.choices[0].message != null ? parsed.choices[0].message.content : "";
                }
            }
        }

        private static string DescribeOpenAIError(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage))
            {
                return "生成失败，返回了空错误信息。";
            }

            if (rawMessage.IndexOf("insufficient_quota", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "OpenAI 账号当前额度不足或未开启计费。请到平台检查 Billing / Usage Limits / Project Budget，补充余额或提高月度预算后再试。";
            }

            if (rawMessage.IndexOf("rate_limit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "触发了请求频率限制。请稍后重试，或降低调用频率。";
            }

            if (rawMessage.IndexOf("authentication", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawMessage.IndexOf("invalid_api_key", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "API Key 无效或未正确配置。请检查你填写的 OpenAI API Key。";
            }

            return "生成失败。你可以先复制提示词，改用可用的账号/项目再试。";
        }

        private string BuildPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("请根据这张 UI 效果图生成 HTML 原型代码，要求严格遵守以下规范：");
            sb.AppendLine("1. 只能输出 HTML，不要 Markdown，不要解释。");
            sb.AppendLine("2. 必须只有一个根节点，data-u-type=\"div\"，data-u-name=\"root\" 或窗口名。");
            sb.AppendLine("3. 根节点 style 必须与目标分辨率一致，不要擅自改尺寸。");
            sb.AppendLine("4. 节点 data-u-name 必须使用小驼峰命名法。");
            sb.AppendLine("5. 允许的 data-u-type 仅有 div, image, text, button, input, scroll, toggle, slider, dropdown。");
            sb.AppendLine("6. dropdown 必须使用 select/option。");
            sb.AppendLine("7. 看到图片、图标、徽标、按钮底图时优先使用 image，并尽可能补 data-u-src。");
            sb.AppendLine("8. 如果我提供了图片资源文件夹，请优先从以下文件名里匹配 spriteName / data-u-src：");

            if (spriteNameList.Count == 0)
            {
                sb.AppendLine("   - 无");
            }
            else
            {
                int limit = Mathf.Min(spriteNameList.Count, 300);
                for (int i = 0; i < limit; i++)
                {
                    sb.AppendLine("   - " + spriteNameList[i]);
                }
            }

            sb.AppendLine("9. 保持布局尽量贴近原图，层级清晰，命名语义化。");
            sb.AppendLine("10. 对于不确定的图片资源，可只保留 image 节点和合适的 name。");
            sb.AppendLine("11. 文本节点尽量保留真实内容、字号、对齐、颜色。");
            return sb.ToString();
        }

        private void SaveHtmlToProject()
        {
            if (string.IsNullOrWhiteSpace(generatedHtml))
            {
                Debug.LogWarning("[EffectImageToHtmlWindow] 当前没有可保存的 HTML。");
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject("保存 HTML", "GeneratedUI.html", "html", "选择保存路径");
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
            return TryParseResolution(systemHint, out width, out height);
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
