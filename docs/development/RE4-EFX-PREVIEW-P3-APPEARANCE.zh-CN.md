# RE4 离线预览 P3：外观链

预览的 Billboard3D 绘制现在走完整的颜色与 alpha 合成链（RENDER-HOST §6，RUNTIME §4.4–§4.10）：深度 / 角度淡出、AlphaCorrection、RgbCommon / RgbWater、混合方式 0–4、软粒子、深度偏移、反色调映射，以及 entry 排序。规格未读出的环节按折中实现，集中在“合成配置”里，可逐项关闭对照。

## 语料概况

23143 个 Billboard3D entry 中：

| 属性 / 字段 | entry 数 | 说明 |
|---|---|---|
| ShaderSettings.DetonemapRatio ≠ 0 | 22451 | 22118 个为 1：作者颜色原样到达屏幕 |
| 软粒子（Flags 位 2 且距离 > 0） | 20277 | 常见 0.05–1 m |
| DepthBias ≠ 0 | 17225 | 多为负值（朝相机） |
| 颜色头 AlphaRate ≠ 1 | 19162 | 16755 个为 0，所以不能当 alpha 系数 |
| FadeByDepth / AlphaCorrection / FadeByAngle | 4843 / 3169 / 1035 | AlphaCorrection 模式 1 有 2446 个，模式 2 有 723 个 |
| 混合方式 0 / 1 / 2 / 3 / 4 | 16549 / 5890 / 141 / 61 / 502 | |
| RgbWater / RgbCommon | 182 / 20 | |
| FadeByEmitterAngle | 0 | 未实现 |
| *Expression 变体（FadeByAngle 等） | 合计个位数 | 淡出参数直接取文件值 |
| PriorityBias ≠ 0 | 0 | |

## 合成链

| 步 | 内容 | 实现 | 状态 |
|---|---|---|---|
| 1–4 | 出生颜色、生命淡出、Blink、顶点 alpha | 原生粒子记录 + `BillboardInstance.Decode`（不变） | 已确认 |
| 5 | 顶点 alpha × FadeByDepth（粒子中心的裁剪 w）× FadeByAngle；乘积 < 2⁻²³ 时剔除 | 顶点着色器 | 公式按规格；CPU 家族部分待确认 |
| 6 | 纹素采样（UV 矩形）；FrameBlend 按引擎权重混合前一帧 | 片元着色器 | 权重为引擎值；UVScroll 随 Mesh 一起做 |
| 6a | TexelChannelOperator：纹素 → 黑体色 → 色相偏移 → 去饱和 → 加阴影色 | 片元着色器，常量取引擎打包值 | 公式已确认；合成方式部分待确认（见下） |
| 6b | TextureFilter：`t = saturate((a−z)/(y−z))`，`a' = (t²(3−2t)·a)^x` | 片元着色器 | 公式已确认；字段对应推定 |
| 7 | AlphaCorrection 模式 0–3，字节按引擎截断打包；模式 3 的第 3 字节取粒子生命字节 | 片元着色器 | 公式按规格；CPU 家族部分待确认 |
| 8 | RgbCommon / RgbWater 替换颜色；lit / unlit 按混合方式合并 | 片元着色器 | 公式按规格；逐粒子随机色和寿命包络未定位，按 u = 0、包络 = 1 处理（折中） |
| 9 | 混合方式颜色式：0/1/2 为 `顶点RGB·纹素RGB·ColorRate`；3 EdgeBlend 在外缘色 `ColorRange·Intensity` 与 `顶点RGB·ColorRate` 之间按 `纹素A/EdgeBlendRange` 插值；4 Multiply 为 `lerp(1, 顶点RGB·纹素RGB·ColorRate, alpha)` | 片元着色器 | 折中 |
| 9 | DetonemapRatio：`rgb = lerp(rgb, 逆ACES(rgb)/曝光, ratio)`，经合成的 ACES 后近似还原作者颜色（输入夹在 0.999 以内） | 片元着色器 | 折中 |
| — | 软粒子：`alpha × saturate((场景视深 − 片段视深)/SoftParticleDistance)`，乘在最终 alpha 上 | 片元着色器，采样不透明场景深度副本 | 折中 |
| — | DepthBias：顶点沿视线移动 DepthBias 米后的深度只用于深度测试，屏幕位置不变 | 顶点着色器 | 折中 |
| 10 | 混合因子：0 `SrcAlpha, 1−SrcAlpha`；1、2 `SrcAlpha, One`；3 同 0；4 `DstColor, Zero` | 管线状态，可在合成配置里改 | 折中 |
| — | 排序：entry 按 PriorityBias 升序，再按（粒子质心到相机距离 + DistanceBias）从远到近；entry 内默认按槽号 | `PreviewSceneRenderer.DrawOrder` | 折中 |
| — | 受光粒子（ParticleLighting 1–4、6）：RGB（Rgb 层只有 lit 部分）乘预览光照 `环境光 + 主光 × saturate(N·L × 0.5 + 0.5)` | 顶点着色器算光照，片元着色器相乘 | 折中 |

- 颜色头每帧从原生导出的 type block 读取（Polygon3DConstant 起于 block+0x54）：混合方式、ColorRate（0x160）、Intensity（0x140）、ColorRange（0x144）、EdgeBlendRange（0x15c）、AlphaRate（0x138）。这些偏移都与文件值逐项核对过；表达式写入的值会进入画面。
- ShaderSettings、FadeBy*、AlphaCorrection、Rgb 层取自文件。
- FadeByAngle 的 ConeDir 按发射器局部方向直接使用。发射器位置取 Transform3D.LocalPosition（预览里发射器世界矩阵为单位阵，见 RENDER-HOST §4.3）。
- 颜色头的 AlphaRate 默认不参与。语料里它多数为 0，若当 alpha 系数，大部分特效会消失。
- Multiply 同时缩放 bloom 亮度目标；低分辨率粒子层只用于全部 entry 为混合方式 0/1 的场景。
- Rgb 层常量的“小浮点”量化未实现：哪些常量用 11 位、哪些用 10 位还没确定。

## TextureFilter、TexelChannelOperator、FrameBlend、受光粒子

语料（全部 RE4 文件）：TextureFilter 共 613 个，其中 Billboard3D 374 个；TexelChannelOperator 共 2532 个，其中 RibbonLength 2088 个、Billboard3D 382 个；FrameBlend（UVSequence Flags 位 10）6339 个；Billboard3D 的 ParticleLighting 为 1 / 2 / 3 / 4 的分别有 7134 / 3973 / 3413 / 626 个。

### TextureFilter

`primitivevfx.vsdf` 的像素着色器（b020 第 871–891 行）在 Rgb 层之后**无条件**执行：

```
t  = saturate((a − z) / (y − z))
a' = (t² · (3 − 2t) · a) ^ x            // (x, y, z) = cbTextureFilter（Polygon3DConstant @432）
```

- 没有 TextureFilter 时，原生块里的常量是 (1, 0, 0)：y − z = 0，t 恒为 1，结果为恒等。有 TextureFilter 时块里仍是 (1, 0, 0)，属性值在绘制时另行写入，所以预览从文件取值。
- 字段对应 x = TexelAlphaRate、y = TexelAlphaHPThreshold、z = TexelAlphaHPMinValue 是**推定**：exe 里写这个常量的代码没有找到。反过来对应的话，语料最常见的一组（0.56 / 0.16 / 0.07）会把亮纹素切掉，不合理。
- 有 Rgb 层时整形作用于分层后的 alpha，否则作用于纹素 alpha；AlphaCorrection 的输入是整形后的 alpha（RUNTIME §4.7）。

### TexelChannelOperator

引擎把它打包进 Polygon3DConstant @512（`cbTexelChannelOperatorParam`，uint4），原生块里就有，预览每帧直接读取：Billboard3D 在类型块 +0x54+512，条带在 +0x50+512。以 bluefire 为例逐项对照文件值：

| 字 | 解码 | bluefire 文件值 |
|---|---|---|
| 0 | 字节 0 / 255 色相偏移；字节 1 G 通道权重；字节 2 / 255 去饱和；字节 3 R/B 通道权重 | HueShift 0.72、HueIntensity 25.7（取整 25）、Desaturate 0.82、AmbientColorIntensity 1 |
| 1 | RGB = 阴影色；字节 3 / 255 = R→B 与 A→B 的插值系数 t | ShadeColor (8,19,48)、ShadeAlphaBlendRate 0 |
| 2 | float | EmissiveRate 3 |
| 3 | 两个半精度 | EmissivePower 1、Opacity 1 |

像素着色器（`primitivevfx_4.vsdf` b013 第 646–861 行；pv5、pv7、pv8、pv9 中同样）：

```
rb = lerp(R, B, t);  s = rb^P · wRB + G^P · wG            // P = EmissivePower
T  = 100 · s · env                                       // env = IntensityEnvelope 输入（预览取 1）
(u, v) = Krystek 黑体色度近似(T) → (x, y) → XYZ(Y = 1) → 线性 sRGB
rgb = floor(EmissiveRate · 1e5 · rgb · (T · 4e-4)⁴) · 1e-5
→ RGB 转 HSV，色相 + 色相偏移，转回 RGB
rgb += (luma(0.3, 0.59, 0.11) − rgb) · 去饱和 + 阴影色 · rb
alpha = lerp(A, B, t) · Opacity
```

- 输出替换纹素，之后照常走 Rgb 层、混合方式颜色式。同一着色器里混合方式 0 的颜色式为 `cbEmissiveRate · rgb · 顶点色 · cbEvOffsetPow2 · (cbEffect3DFlags 位 10 ? 1 : cbIntensityScale)`。`cbEvOffsetPow2` 是场景曝光补偿，普通粒子也乘它；预览没有场景曝光，相当于恒为 1。所以 ColorRate 很小（如 0.005）的 TCO 条带，在预览里绝对亮度偏暗，需要调高曝光查看。
- IntensityEnvelope（UseIntEnvelope / Appear / Keep / Vanish）在语料中全部未启用，预览取 1。
- 窗口核对：bluefire 的 Billboard3D entry 关掉 TCO 时是贴图原色（橙），打开后变蓝。条带 entry 在预览里很暗，画面效果没有核对。

### FrameBlend

引擎在粒子记录里写入前一帧矩形（+0xd4）和权重 acc/T（+0xe4）。预览读取权重，按播放方向推出前一帧号（正放 frame − 1 回绕，倒放 frame + 1；[p+4] 位 7），片元着色器按 `mix(前一帧, 当前帧, 权重)` 采样。条带不做。

原生模拟器里没有加载 .uvs 资源，引擎写的两个矩形都是 0，没法用来核对帧号；权重正常（PlaySpeed 1.5 的样本在 0.91–0.998 之间随帧推进）。引擎的余量算法使权重多数贴近 1，交叉淡化通常很轻。

### 受光粒子

受光粒子的真实着色在 `primitivevfx_10` 等变体中（读取 LightInfo、阴影、雾和每粒子 n² 光照图集，由 `primitivecslighting.vsdf` 计算），依赖场景光源，预览没有。折中做法：

- ParticleLighting 为 1–4、6 的 entry，RGB 乘 `环境光 + 主光 × saturate(N·L × 0.5 + 0.5)`。Rgb 层只乘 lit 部分。
- 法线：Billboard3D 和条带朝向相机；Polygon 取粒子四元数旋转后的 +Z，翻到朝向相机的一侧；Billboard2D 不受光。
- 默认环境光 0.45、主光 0.75，主光方向 (0.4, 1, 0.3)，可在合成配置里调。

## 合成配置

主面板“合成配置”逐项开关：深度 / 角度淡出、AlphaCorrection、Rgb 层、软粒子、深度偏移、反色调映射、颜色头 AlphaRate、entry 排序、entry 内深度排序、TextureFilter、TexelChannelOperator、FrameBlend、受光粒子（及环境光、主光强度），以及 Physical 受光增益和每种混合方式的源 / 目标因子。面板下方列出所选 entry 用到的外观属性。

MCP：`composition` 读写同样的开关，结果附带上一帧的绘制顺序；`frame` 的每个 entry 附带颜色头取值和外观属性摘要。

## 实现

| 部分 | 位置 |
|---|---|
| 数据 | `VfxStudio.Re4Render/PreviewAppearance.cs`：`ColorHeader`（type block）、`EntryAppearance`（文件）、`CompositionConfig`、C# 参考实现 `AppearanceReference` |
| 着色器公式 | `AppearanceShaderLibrary.cs`：std140 块 `EntryParams` 与 GLSL 函数（`fadeDepth`、`fadeAngle`、`alphaCorrect`、`textureFilter`、`texelChannel`、`previewLight`、`rgbCommon`、`rgbWater`、`inverseAces`、`shade`），与参考实现逐行对应；`EntryUniforms` 负责打包，`SetTexelChannel` 每帧从类型块读 TCO 常量 |
| 绘制 | `BillboardRenderer`：每个 entry 一个 uniform 块，混合因子取自配置，可选 entry 内深度排序；实例 `data.w` 存 FrameBlend 的前一帧号和权重（`FrameBlendWord`） |
| 管线 | `PreviewHdrPipeline`：RGBA16F + bloom + ACES，与共用的 `HdrBloomPipeline` 相同，但深度改为纹理，并在不透明物体之后复制一份深度供软粒子采样。共用管线 MHWilds 主程序也在用，没有改动 |
| 原生 | `re4host.py`：允许 TypeBillboard3DExpression（类型 24）；Python 层的单个 entry 构建错误（例如属性没有原生工厂）只跳过该 entry，加载回复的 `skipped` 列出；原生访问违例仍为致命错误 |

着色器源码只能用 ASCII：Silk.NET 按 UTF-16 长度传源码，一个非 ASCII 字符就会截掉末尾。

## 验证

```powershell
dotnet run -c Release --project tools/Re4AppearanceTests [-- --corpus]
```

- 打包：AlphaCorrection 字节的截断与回绕（超过 255、负值），模式 3 的指数打包；ACES 与逆 ACES 往返。
- GPU 与 C# 逐值比较：计算着色器调用与绘制相同的 GLSL 函数，每组 4096 个随机输入，与 `AppearanceReference` 比较。覆盖 fadeDepth、fadeAngle（8 种标志组合）、alphaCorrect（模式 0–3）、inverseAces，以及 `shade()` 在 5 种混合方式下的 7 种变体（无附加属性、AlphaCorrection 模式 1、模式 2、RgbCommon、RgbWater、Detonemap 1、Detonemap 0.3 + AlphaRate）。AlphaCorrection 模式 1 在 `Lo − T ≤ 1e-6` 时是硬阈值，阈值附近 ±0.02 的样本不参与比较。
- `--corpus`：用原生模拟和完整渲染链，在 tick 30 / 120 / 300 画 13 个官方文件，检查 HDR 缓冲没有 NaN / Inf，且有 entry 被画出：
  - 5 个 25–256 个 entry 的大文件。
  - 混合方式 2、3、4 各一个文件；AlphaCorrection、FadeByDepth、FadeByAngle、RgbCommon、RgbWater 各一个文件。
  - 混合方式 2 和 FadeByDepth 的第一候选文件里，所有相关 entry 都在原生模拟器中出错，测试按顺序换用下一个候选。
- TextureFilter、TexelChannelOperator（含 bluefire 的引擎打包常量）、预览光照各 4096 个随机输入逐值比较；`shade()` 另加 6 种变体（TextureFilter、TextureFilter + RgbCommon、TextureFilter + AlphaCorrection 2、受光、受光 + RgbCommon、TexelChannel）。`tools/Re4MultiEntryTests` 第 7 节检查 FrameBlend 权重范围与变化。
- 结果：比原来新增 33 项，带 `--corpus` 合计 108 项全部通过；原生排除照常列出（例如 sm82_320 的 entry 20、两个大文件各一个除零 entry、Multiply 样本里 3 个访问违例 entry）。

既有回归全部通过：Re4PreviewTests、P1、P2、通用编辑、播放控制（含原生）、多 entry、MCP 端到端（56 项）、MCP 自检（107 项）、run_all、窗口冒烟。通用编辑与 MCP 端到端测试原先用混合方式 2 代表“不支持”，现改用不存在的 5。

## 未做

- FadeByEmitterAngle（Billboard3D 语料中没有）、UVScroll（随 Mesh 一起做）、Distortion（783 个，属于 P5）。
- 受光粒子只有预览光照，没有场景光源、阴影和每粒子光照图集。
- TextureFilter 的字段对应未在 exe 中确认；TCO 的 IntensityEnvelope 未实现（语料未启用）；场景曝光 `cbEvOffsetPow2` 没有对应。
- Rgb 层的逐粒子随机色与寿命包络、小浮点量化。
- 与游戏画面的对照：各折中项的默认值还没有拿游戏截图校准。
