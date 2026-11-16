# RE4 离线预览：TypePolygon 与 TypeBillboard2D

预览现在绘制三种 CPU Type：Billboard3D、Polygon、Billboard2D。三者共用同一套原生模拟、实例缓冲和外观链（[外观链](RE4-EFX-PREVIEW-P3-APPEARANCE.zh-CN.md)），只有几何展开和颜色头的来源不同。

## 语料

| Type | entry 数 | 用途 | 预览 |
|---|---|---|---|
| Billboard3D | 23143 | 面向相机的面片 | 已支持 |
| Polygon | 1630 | 世界朝向的四边形；65% 绕 X 轴转 π/2，即贴地的冲击波、脚印、水纹 | 已支持 |
| Billboard2D | 135 | 屏幕空间面片：91 个普通、18 个带 Distortion（屏幕扭曲）、26 个带表达式 | 只支持普通的 91 个 |

## Polygon

- **朝向**：实例写入器 3d6f5e0 用粒子 +0x9c / +0xa0 / +0xa4（弧度）和 RotationOrder（`[p+4] & 7`）调用 dfae10 构造四元数。经原生比对，dfae10 输出 (x, y, z, w)，等于按顺序名逆序相乘的轴旋转，例如 XYZ 为 qZ·qY·qX（先绕 X 转）。已确认：`tools/Re4Native/exp_polygon_quat.py` 用 1200 组随机欧拉角比对，最大误差 2e-7；外观测试另用 6 组原生输出核对 C# 的 `BillboardInstance.PolygonRotation`。
- **几何**：四边形位于局部 XY 平面，宽沿 X、高沿 Y，经四元数旋转后平移到粒子位置；双面绘制。尺寸取 U·X·`+0xe8`、U·Y·`+0xec`（Polygon 的 +0xe8 / +0xec 恒为 1，不随发射器缩放）。
- **颜色头**：Polygon3DConstant 在 type block 的 +0x58（Billboard3D 是 +0x54），字段间距相同，每帧读取。已用 ColorRate 10、ColorRange、AlphaRate 0 的样本核对。
- **未实现**：Offset（作用轴在着色器，未读）、Flags2 0x40 / 0x80（朝向跟随运动方向、锁定朝向）。
- **实例**：四元数放在 Size.zw 与 Extra.zw。

## Billboard2D

- **几何（折中）**：粒子位置与尺寸视为 1080 线屏幕上的像素，原点在屏幕中心，+Y 朝上；按窗口高度缩放；画在所有物体前面，不做深度相关处理（淡出、软粒子、深度偏移关闭）。坐标系没有读码依据：UI 样本的 Transform2D.LocalPosition 为 (1100, 600)，粒子 +0x28 为相对值。
- **颜色头**：Billboard2D 的 type block 在 ColorRange 之后不是文件布局，颜色头改取文件值。它的表达式属性（类型 22）在原生侧没有工厂函数，带表达式的 entry 加载时就被跳过，所以文件值与运行时一致。
- **带 Distortion 的 entry** 标为“Distortion（屏幕扭曲）尚未支持”（属于 P5）。这条规则对所有 Type 生效，Billboard3D 和 Polygon 中带 Distortion 的 entry 也不再按普通面片绘制。
- **原生侧补充**：Billboard2D 路径（3e07150 → 3d024f0）会从更新结构 U+0x18 的 +8 取一张按 ID 查找的表。模拟器原来给的是空指针，现在给一张空表，查找返回空，引擎走自己的“未找到”分支。

## 其他改动

- **原生**：`re4host.py` 接受 Billboard2D（21，可带 22）、Billboard3D（23，可带 24）、Polygon（63，可带 64、65），帧条目的 `type` 字段为实际类型 ID，type block 按实际类型复制。
- **编辑后台**：`configure_offline_preview` 允许选择这三种 Type；Billboard3D 专用的颜色 / 尺寸表单参数只对 Billboard3D 发布，其他类型使用通用属性面板。
- **贴图解码**（共用的 `VfxStudio.Renderer/Textures/GpuTexture.cs`）：没有图像头表的 .tex（例如库贴图 `tex_capcom_smoke_00_0050_ALPG`）会把像素数据当作头表读取，两个大数相加在 32 位下回绕成负数，越界检查失效后抛出异常，引用它的 entry 都无法绘制。越界检查改为 64 位后，会退回已有的 mip0 偏移路径，正常解码。

## 验证

```powershell
dotnet run -c Release --project tools/Re4AppearanceTests -- --corpus
python -I tools/Re4Native/run_all.py exp_polygon_quat
```

- Polygon 四元数：6 种 RotationOrder 与原生 dfae10 输出一致；着色器的四元数旋转式与 `Vector3.Transform` 一致。
- 语料：`efd_0015_ao_chfb_0000_footstep_0000`（27 个 entry，绘制 11 个 Polygon / Billboard3D，6 个原生排除）、`efd_0015_ui1010_firedust_0000`（Billboard2D，4 个 entry，tick 45 共 1506 个粒子），与原有 13 个文件一起渲染，没有 NaN / Inf。外观测试共 71 项通过。
- 窗口截图：`test-output/re4-polygon-smoke.png`、`test-output/re4-billboard2d-smoke.png`。

## 未做

- Polygon 的 Offset、Flags2 运动朝向。
- Billboard2D 的坐标系依据、Repeat（粒子标志位 30–31）、表达式。
- Distortion（P5）。
