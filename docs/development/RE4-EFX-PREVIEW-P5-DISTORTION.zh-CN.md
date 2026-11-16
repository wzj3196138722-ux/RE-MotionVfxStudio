# RE4 离线预览：Distortion（屏幕扭曲）

带 Distortion 属性的 entry（语料 1149 个：Billboard3D 791、Polygon 166、RibbonFollow 137、RibbonFixEnd 25、Billboard2D 18、PolygonTrail 11、RibbonLength 1）不再被排除。它们读取先画出的场景，把其中一块扭曲或模糊后写回。公式来自 `primitivevfx_2` b025 / b026 和 `primitivevfx_3` b093 的像素着色器（RUNTIME §4.8）。

## 参数来源

属性的安装函数（3d28c10）把值写进 Polygon3DConstant，原生块里就有，预览每帧读取（与 TexelChannelOperator 相同）：

| 常量 | 偏移 | 内容 |
|---|---|---|
| cbEffect3DFlags 位 7–8 | @68 | DistortionType：0 Blur、1 Refract、2 BlurTexture |
| cbDistInfluence | @116 | Influence |
| cbDistortionSpecular | @448 | SpecularPower、SpecularIntensity |
| cbDistortionSpecularColor | @668 | SpecularColor（RGBA8） |
| cbEffect3DFlags2 | @444 | 位 11–12：纹素 alpha 不做 4.84 次方；位 15：偏移 X 取反 |

两个样本（downsmoke 的 Refract、gameset_0020 的 BlurTexture）的类型、Influence、高光与文件值一致。Billboard2D 没有这块常量，取文件值。

## 着色

```
A = (flags2 & 0x1800 ? a : a^4.84)，再经 TextureFilter，× 顶点 alpha × 软粒子系数；A < 0.01 丢弃
Refract：     n = 2·RG − 1；视空间偏移 Influence·A·n 米（粒子深度处），重投影后采样场景
              Billboard2D：UV 偏移 0.5·Influence·A·n（半个 NDC 单位）
Blur：        水平 9 抽头高斯，间距 Influence·A 像素（权重 0.2042 / 0.1900 / 0.1216 / 0.0578 / 0.0204）
BlurTexture： 沿 normalize(2·RG − 1) 单侧 9 抽头，步长 Influence·A 像素（权重 0.1790 … 0.0065）
高光（SpecularPower ≥ 1e-6）：SpecularColor.rgb · 2^SpecularIntensity · SpecularColor.a · B^(2.2·SpecularPower)
输出：场景 · lerp(1, 顶点色 + 高光, A)
```

D3D 与 GL 的屏幕 Y 方向相反：着色器里纹理的 +Y、视空间 +Y 和 GL 的 UV +Y 都朝上，所以方向向量不再取反。

## 绘制顺序与状态

- 每帧先画普通 entry，再把 HDR 场景色复制一份（`PreviewHdrPipeline.CaptureSceneColor`，只复制一次），最后画 Distortion entry，它们都读这份副本。
- 混合关闭，在覆盖范围内直接替换（推定：引擎的混合状态没有读到）。只写场景色，不写 bloom 亮度目标；否则背景在 bloom 里的贡献被抹掉，扭曲范围会整体变暗。
- 帧里有 Distortion entry 时强制全分辨率绘制：低分辨率粒子层拿不到场景色。
- 宿主没有提供场景副本（例如测试的渲染上下文），或合成配置里关闭了“Distortion”时，这些 entry 不绘制。

## 折中与已知问题

- 引擎的混合状态、是否还有第二遍 Blur 没有读到，按“覆盖范围内替换”实现。
- `cbInterleaveScale`（半分辨率 / 隔行渲染因子）取 1。
- 偏移和模糊都乘以 A，而 A 含 4.84 次方，所以 Influence 小或纹素 alpha 低时效果很弱。例如 downsmoke 的 Refract（Influence 0.15）在画面上几乎看不出，这与公式一致。
- 场景副本只包含先画的普通 entry 和地面网格，没有游戏场景，扭曲只能作用在这些内容上。
- GPU 类型上的 Distortion 在游戏里本来就无效（GPU 绘制着色器不采样场景色），预览也不支持 GPU 类型。

## 验证

- 窗口对比：slams_single 的 Polygon Refract 打开后，中心区域的颗粒被错位采样，变得平滑；downsmoke 打开前后背景逐像素一致（修正 bloom 写入之前，扭曲范围会整体变暗约 27%）。
- 回归：外观 108/108、多 entry 22/22、MCP 56/56、MCP 自检 107 项、通用编辑、P2、Re4PreviewTests、播放控制（含原生）、run_all 全部通过。
- 没有逐像素对照游戏，也没有自动化的扭曲结果比对。
