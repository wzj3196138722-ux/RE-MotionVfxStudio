# RE4 离线预览：曲线时间轴

“曲线时间轴”窗口显示属性面板当前选中属性的 Clip 曲线：每个通道一条线，带播放头。可以拖动、添加、删除关键帧，修改插值。修改走后端已有的 `curve_key`（与 MCP 相同），每次提交是一个新修订，可以撤销。

RE4 语料里曲线不多：EmitterColorClip 301 个，PtColorClip 29 个，PtUvSequenceClip 8 个，Transform3DClip 5 个，PtTransform3DClip 2 个，TypeMeshClip 1 个。通道数多为 3（RGB）或 1，时长 p10 / p50 / p90 为 20 / 100 / 300 帧。关键帧插值只出现 Type2（2670 个）和 Type1（27 个）。

## 求值规则（`PreviewCurveMath`）

| 插值（C# 名 / 共享枚举名） | 曲线 | 依据 |
|---|---|---|
| Type1 / Discrete | 保持关键帧的值，到下一个关键帧才跳变 | RE4 原生模拟器实测 |
| Type2 / Linear | 线性 | RE4 原生模拟器实测 |
| Type3 / Event | 形状同 Discrete，保留原始类型 | MHWs-EFX-Editor（MHWilds 实机） |
| Bezier（原始值 5）/ Hermite | 三次曲线；切线 (out_x, out_y)、(in_x, in_y) 是相对关键帧的偏移，换成 Bezier 控制点乘 1/3；下一个关键帧不是 Hermite 时取它本身 | MHWs-EFX-Editor（MHWilds 实机） |
| 其他（0、4、6–13） | 画成 0，编辑器不提供 | MHWilds 实机：恒为 0、飞离或崩溃 |

- 关键帧的插值决定从它出发的那一段。实测时 200 帧（Type2）到 215 帧（Type1）这一段是线性的。
- 第一个关键帧之前保持首值，最后一个关键帧之后保持末值。
- 发射器时间的 clip 以帧为单位：第 t 个 tick 取 clip 时间 (t − 1) mod (时长 + 1)，也就是时长那一帧的关键帧仍会取到，下一帧才回到 0。

实测样本：`efd_0015_ao_chfa_0001_sakebi_0000` 的 EmitterColorClip（alpha 一个通道：0:0 L、5:255 L、200:255 L、215:0 D、300:0 L）。引擎每 tick 写入发射器 +0x134..+0x137（发射器颜色 RGBA），alpha 逐帧为 0、51、102、153、204、255；200–215 帧每帧 −17；301 帧之后回到 0。把 300 帧的值改成 255 后，Discrete 段一直保持 0，到 300 帧才变成 255。修改的是 scratchpad 里的副本，游戏目录没有改动。

## 界面

- 通道名来自 Clip 属性的 ClipBits（后端 inspect 新增 `clipChannels`，按置位比特从低到高），颜色 R / G / B / A 对应红 / 绿 / 蓝 / 白。每个通道可以隐藏，点名字选中。
- 拖动关键帧时显示新位置，松手才提交；“吸附到帧”默认开，Int 通道的值取整。双击空白处在选中的通道加一个线性关键帧。
- 检查区：时间、值、插值（保持 / 线性 / 事件 / Hermite）、应用、删除。通道至少保留一个关键帧（后端的规则）。切到 Hermite 时保留原切线；原来没有切线的，给左右各四分之一时长的水平切线，因为后端不会自己生成切线。
- 播放头：发射器时间的 clip 按上面的规则换算。Pt* 的时间是粒子年龄，各粒子不同，不画播放头。
- 属性面板有未应用的草稿、编辑器正忙或处于压力模式时，修改被拒绝，原因显示在标题行。
- MCP 的 `inspect` 现在也会让窗口的属性面板和曲线时间轴切到被查看的属性（有草稿时不切），结果里带 `windowFocused` 和 `clipChannels`。

## 验证

- `tools/Re4MultiEntryTests` 第 9 节：用 sakebi 的关键帧求值，与原生模拟器的 alpha 序列逐帧比较（1–8、196–216、296–305 帧），另加 Discrete 保持段。
- 窗口：打开 sakebi，通过管道查看 EmitterColorClip，曲线面板显示通道 A，播放头在 204 帧落在下降段。用 `curve_key` 把 200 帧的关键帧移到 150 帧后，面板刷新为新曲线，预览同步到 r2。
- 回归：MCP 56/56、MCP 自检 107 项、通用编辑、P2、Re4PreviewTests、播放控制（含原生）通过。LiveEditor 自检因缺少 Wilds 测试素材提前中止，后端的曲线测试没有单独运行。
- 没有测试的：鼠标拖动、双击添加在窗口里没有实际操作过（请求体与 MCP 的相同）；Hermite 和 Event 在 RE4 里没有实测。

## 未做

- 表达式曲线（*Expression 属性）的编辑。
- 时间轴缩放、平移、框选多个关键帧、复制粘贴关键帧。
- Pt* 曲线按所选粒子的年龄画播放头。
