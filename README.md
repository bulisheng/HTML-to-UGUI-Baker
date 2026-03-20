# HtmlToUGUI 工具说明

这套工具全部放在 `Assets/HtmlToUGUI` 目录下，用于把 UI 原型 HTML / 效果图识别结果转换成 Unity UGUI 可烘焙的数据。

## 目录结构

- `Assets/HtmlToUGUI/Editor/`
  - `HtmlToUGUIBaker.cs`
  - `EffectImageToHtmlMultiWindow.cs`
  - `EffectImageToHtmlWindow.cs`
- `Assets/HtmlToUGUI/HtmlToJson/`
  - `HTML 转 JSON 坐标烘焙器.html`
- `Assets/HtmlToUGUI/DSL/`
  - `UI 原型 HTML 生成规范 (UI-DSL) - 全控件版.md`
- `Assets/HtmlToUGUI/UIjson/`
  - 示例 JSON
- `Assets/HtmlToUGUI/HTML/`
  - 示例 HTML
- `Assets/HtmlToUGUI/.env`
  - 本地密钥配置文件

## 主要入口

### 1. 效果图识别 -> HTML

Unity 菜单：

- `Tools -> UI Architecture -> 效果图识别 -> HTML (多平台)`

功能：

- 选择一张 UI 效果图
- 选择一个图片资源文件夹
- 通过 OpenAI / Gemini / 豆包 / 自定义兼容接口生成符合 UI-DSL 的 HTML
- 支持从 `.env` 自动读取多个 Key
- 支持根据 Key 自动切换平台预设

### 2. HTML -> JSON 坐标烘焙器

文件位置：

- `Assets/HtmlToUGUI/HtmlToJson/HTML 转 JSON 坐标烘焙器.html`

功能：

- 将 HTML 原型解析成 JSON 坐标数据
- 识别 `img`
- 识别 `data-u-src`
- 识别 `data-u-sprite`
- 识别 `background-image`
- 提供图片绑定预览
- 支持点击未绑定项并高亮对应节点
- 支持导出未绑定图片的修复清单

### 3. Unity 烘焙器

Unity 菜单：

- `Tools -> UI Architecture -> HTML to UGUI Baker (Full Controls)`

功能：

- 粘贴或读取 JSON 数据源
- 烘焙为 Unity 预制体
- 根据图片资源文件夹自动绑定 `Sprite`
- 烘焙前预检
- 预检结果按以下分类显示：
  - `命中图片`
  - `缺少文件`
  - `名字不一致`
  - `没转 Sprite`

## 推荐使用流程

### 流程 A：效果图 -> HTML -> JSON -> Unity

1. 打开 `效果图识别 -> HTML (多平台)`。
2. 选择一张效果图。
3. 选择一个图片资源文件夹。
4. 选择平台预设，或者从 `.env` 自动切换平台。
5. 点击 `生成 HTML`。
6. 将生成结果粘贴到 `HTML 转 JSON 坐标烘焙器.html`。
7. 点击烘焙并复制 JSON。
8. 在 Unity 的 `HTML to UGUI Baker` 中粘贴 JSON 并执行烘焙。

### 流程 B：直接使用现成 HTML

1. 在 `HTML 转 JSON 坐标烘焙器.html` 中粘贴已有 HTML。
2. 点击烘焙并复制 JSON。
3. 在 Unity 中进行烘焙。

## 平台预设

当前多平台识别窗口支持以下预设：

- `OpenAI`
- `Gemini`
- `豆包`
- `Custom`

切换预设时会自动填充对应的模型名和接口地址。

## `.env` 说明

你可以在 `Assets/HtmlToUGUI/.env` 中放置多个 Key，例如：

```env
OPENAI_API_KEY=sk-xxxxxxxx
GEMINI_API_KEY=AIzaxxxxxxxx
DOUBAO_API_KEY=xxxxxxxx
```

窗口里可以：

- 读取 `.env`
- 从下拉框选择某个 Key 变量
- 按当前 Key 自动识别平台

注意：

- Key 建议不要提交到仓库
- 真实密钥如果泄露过，建议及时轮换

## 图片资源绑定规则

Unity 烘焙器会优先使用你指定的图片文件夹进行匹配，不会默认扫描整个项目。

匹配顺序大致是：

1. `spriteName`
2. `spritePath`
3. 节点名 `name`
4. 名称归一化匹配
5. 文件夹内 Sprite 索引

如果图片没有成功绑定，请优先检查：

- 图片是否放在你选中的资源文件夹里
- 图片导入类型是否已经是 `Sprite`
- 文件名是否和 JSON 中的 `spriteName` 对得上
- 是否只是带了透明度 tint，导致看起来像没显示

## 常见问题

### 1. 生成时报 `Quota exhausted`

说明当前账号或项目额度不足，需要检查：

- Billing
- Usage Limits
- Project Budget

### 2. 生成时报 `API Key 无效或格式不正确`

通常是以下原因之一：

- 选错平台了
- Key 不是这个平台的格式
- `.env` 里多了引号或空格

### 3. 生成出来的图片是透明的

如果 `data-u-type="image"` 节点原始颜色带了透明通道，烘焙时会被视为 tint。  
当前实现已经对成功绑定 Sprite 的图片节点做了默认白色修正，避免 Sprite 被意外乘成透明。

### 4. 预检里只有一部分图片命中

看预检分类：

- `缺少文件`：目录里没有对应文件
- `名字不一致`：有图，但名字没对上
- `没转 Sprite`：文件存在，但不是 Sprite 导入

## 代码位置

当前相关代码都在 `Assets/HtmlToUGUI` 路径下：

- `Assets/HtmlToUGUI/Editor/HtmlToUGUIBaker.cs`
- `Assets/HtmlToUGUI/Editor/EffectImageToHtmlMultiWindow.cs`
- `Assets/HtmlToUGUI/Editor/EffectImageToHtmlWindow.cs`
- `Assets/HtmlToUGUI/HtmlToJson/HTML 转 JSON 坐标烘焙器.html`
- `Assets/HtmlToUGUI/DSL/UI 原型 HTML 生成规范 (UI-DSL) - 全控件版.md`

如果你后续要继续扩展功能，建议也统一放到这个目录下，保持工具入口和资源说明集中管理。
