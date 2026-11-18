# EFX 未知字段清单（RE4 / Wilds）

由 csdump（当前 C# 定义）与 Wilds / RE4 exe 的解析器、构建器分析结果生成，只读。RE4 的类型按 re4.exe 原生运行全部 240 个工厂取得 vtable，解析器是否为空、有哪些名字都来自 re4.exe 本身；RE4 有语料的 68 个带名字类型已在 re4.exe 上原生做过标记实验（解析器 slot 18 → 块偏移；加载器 slot 5 + 构建器 slot 15 → 文件偏移；GPU 类型和 TypeMesh 用加载后的属性对象加静态构建器映射），这些字段的说明以"re4.exe 原生"开头；其余 RE4 字段与 Wilds 同名同类型时借用 Wilds 的偏移结论（标"偏移由 Wilds 对上"）。偏移是载荷内的文件偏移（不含 UniqueID），"?" 表示前面有变长字段、偏移没算出来。"语料"是该类型在官方文件里出现的次数。

收录条件：字段名含 unk，或类型是 UndeterminedFieldType。

## 总览

| 分类 | RE4 | Wilds | 说明 |
|---|---|---|---|
| 引擎有名字 | 0 | 72 | 解析器给出了名字并且落在这个偏移上，可以直接改名；几个相邻字合成一个名字时说明 C# 拆分有误 |
| 解析器有名字，偏移没对上 | 0 | 268 | 类型的解析器里有名字，但没走通到这个偏移；名字就在候选里，需要原生标记实验定位 |
| 解析器非空，脚本没走通 | 2 | 334 | 解析器在 .trace、是内层形式或有条件分支，静态脚本没解析；需要手工或原生实验 |
| 已对上，引擎不给名字 | 95 | 23 | 解析器和构建器都走通了，这个字不进解析器（构建器不读、换算或当标志位） |
| 解析器是空函数 | 139 | 524 | 该类型的文本解析器是 `return 0`，引擎里没有名字；只能按行为命名或找其他游戏的 exe |
| 未分析 | 6 | 65 | exe 分析结果里没有这个类型 |
| 合计 | 242 | 1286 | |

## 引擎有名字的字段

| 游戏 | 类型 | C# 字段 | 偏移 | 引擎名 | 语料 |
|---|---|---|---|---|---|
| Wilds | VanishArea3D | `wilds_unkn0` | +4 | AreaPosition | 1301 |
| Wilds | TypeLightning3D | `unkn2_59` | +252 | FadeByTwist | 663 |
| Wilds | TypeLightning3D | `unkn2_60` | +256 | FadeByTwist | 663 |
| Wilds | TypeLightning3DMaterial | `unkn2_0` | +12 | AttractPos | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_1` | +16 | AttractPos | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_2` | +20 | AttractAngle | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_3` | +24 | AttractAngle | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_8` | +48 | SectionSinScale / FractalSinScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_9` | +52 | SectionSinScale / SubSectionHeightScale / FractalSinScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_10` | +56 | SectionCosScale / FractalCosScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_11` | +60 | SectionCosScale / FractalCosScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_12` | +64 | SectionHeightScale / FractalHeightScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_13` | +68 | SectionHeightScale / FractalHeightScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_16` | +80 | SubSectionSinScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_17` | +84 | SubSectionSinScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_18` | +88 | SubSectionCosScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_19` | +92 | SubSectionCosScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_20` | +96 | SubSectionHeightScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_21` | +100 | SubSectionHeightScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_26` | +120 | AttractRate | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_27` | +124 | AttractRate | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_28` | +128 | InitialWidth | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_29` | +132 | InitialWidth | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_30` | +136 | BetweenWidth | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_31` | +140 | BetweenWidth | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_32` | +144 | TerminalWidth | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_33` | +148 | TerminalWidth | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_34` | +152 | WidthScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_35` | +156 | WidthScale | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_36` | +160 | InitialPosition | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_37` | +164 | InitialPosition | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_38` | +168 | InitialPosition | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_39` | +172 | AddVelocity | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_40` | +176 | AddVelocity | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_41` | +180 | AddVelocityCoef | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_42` | +184 | AddVelocityCoef | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_43` | +192 | RotationX | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_44` | +196 | RotationX | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_45` | +200 | RotationY | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_46` | +204 | RotationY | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_47` | +208 | RotationZ | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_48` | +212 | RotationZ | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_49` | +216 | EdgeFadePosition | 0 |
| Wilds | TypeLightning3DMaterial | `unkn2_50` | +220 | EdgeFadeRate | 0 |
| Wilds | FluidEmitter2D | `unkn9` | +40 | Angle | 0 |
| Wilds | FluidSimulator2D | `unkn8` | +32 | PostEdgeDropoff | 0 |
| Wilds | FluidSimulator2D | `unkn19` | +76 | DensityAlphaMax | 0 |
| Wilds | FluidSimulator2D | `unkn1_27` | +116 | InternalInherit | 0 |
| Wilds | FluidSimulator2D | `unkn1_31` | +132 | ProjectionTimeScale | 0 |
| Wilds | TypeGpuLightning3D | `unkn14` | +64 | InitialWidth | 0 |
| Wilds | TypeGpuLightning3D | `unkn15` | +68 | InitialWidth | 0 |
| Wilds | TypeGpuLightning3D | `unkn18` | +80 | TerminalWidth | 0 |
| Wilds | TypeGpuLightning3D | `unkn19` | +84 | TerminalWidth | 0 |
| Wilds | TypeGpuLightning3D | `unkn20` | +88 | WidthScale | 0 |
| Wilds | TypeGpuLightning3D | `unkn21` | +92 | WidthScale | 0 |
| Wilds | TypeGpuLightning3D | `unkn22` | +96 | InitialPosition | 0 |
| Wilds | TypeGpuLightning3D | `unkn23` | +100 | InitialPosition | 0 |
| Wilds | TypeGpuLightning3D | `unkn24` | +104 | InitialPosition | 0 |
| Wilds | TypeGpuLightning3D | `unkn25` | +108 | SecondPosition | 0 |
| Wilds | TypeGpuLightning3D | `unkn26` | +112 | SecondPosition | 0 |
| Wilds | TypeGpuLightning3D | `unkn27` | +116 | SecondPosition | 0 |
| Wilds | TypeGpuLightning3D | `unkn28` | +120 | ThirdPosition | 0 |
| Wilds | TypeGpuLightning3D | `unkn29` | +124 | ThirdPosition | 0 |
| Wilds | TypeGpuLightning3D | `unkn30` | +128 | ThirdPosition | 0 |
| Wilds | TypeGpuLightning3D | `unkn31` | +132 | AddVelocity | 0 |
| Wilds | TypeGpuLightning3D | `unkn32` | +136 | AddVelocity | 0 |
| Wilds | TypeGpuLightning3D | `unkn33` | +140 | AddVelocityCoef | 0 |
| Wilds | TypeGpuLightning3D | `unkn34` | +144 | AddVelocityCoef | 0 |
| Wilds | TypeGpuLightning3D | `unkn35` | +148 | EdgeFadePosition | 0 |
| Wilds | TypeGpuLightning3D | `unkn36` | +152 | EdgeFadeRate | 0 |
| Wilds | TypeGpuLightning3D | `unkn40` | +168 | MoveFrame | 0 |
| Wilds | TypeGpuLightning3D | `unkn41` | +172 | MoveFrame | 0 |

## RE4 全部

### ShaderSettings（id 123，语料 36824，2 个）
- **已对上，引擎不给名字**：`unkn13` +56、`unkn24` +140

### TypeMesh（id 28，语料 10676，1 个）
- **已对上，引擎不给名字**：`re4_unkn1` +132

### PtBehavior（id 132，语料 2193，1 个）
- **解析器是空函数**：`unkn1` +0

### TypeGpuMesh（id 251，语料 506，3 个）
- **解析器非空，脚本没走通**：`unknDataSize` ?、`unknData` ?
- **已对上，引擎不给名字**：`unkn3` +12

### TypeGpuRibbonFollow（id 247，语料 353，1 个）
- **已对上，引擎不给名字**：`re4_unkn5` +44

### TypeRibbonChain（id 33，语料 323，5 个）
- **已对上，引擎不给名字**：`unkn2_43` +200、`unkn2_44` +204、`unkn2_45` +208、`unkn2_46` +212、`unkn2_47` +216

### EmitterColorClip（id 19，语料 306，1 个）
- **解析器是空函数**：`unkn1` ?

### Transform3DClip（id 12，语料 281，1 个）
- **解析器是空函数**：`unkn1` ?

### DepthOperator（id 184，语料 249，4 个）
- **已对上，引擎不给名字**：`unkn0` +0、`unkn3` +12、`unkn4` +16、`unkn5` +20

### TypeStrainRibbon（id 114，语料 164，1 个）
- **已对上，引擎不给名字**：`unkn3_4` +48

### Velocity2D（id 78，语料 78，4 个）
- **已对上，引擎不给名字**：`unkn1_6` +28、`re4_unkn0` +32、`re4_unkn2_0` +64、`re4_unkn2_1` +68

### TypeGpuRibbonLength（id 249，语料 72，1 个）
- **已对上，引擎不给名字**：`unkn_re4` +32

### ScaleAnimDelayFrame（id 86，语料 42，1 个）
- **解析器是空函数**：`unkn2` +4

### Velocity3DDelayFrame（id 80，语料 36，1 个）
- **解析器是空函数**：`unkn2` +4

### RotateAnimDelayFrame（id 83，语料 30，1 个）
- **解析器是空函数**：`unkn2` +4

### PtColorClip（id 165，语料 29，1 个）
- **解析器是空函数**：`unkn1` ?

### Velocity3DExpression（id 82，语料 28，2 个）
- **解析器是空函数**：`rert_unkn0` +0、`rert_unkn1` +4

### TextureUnit（id 97，语料 19，1 个）
- **解析器是空函数**：`unkn1` +0

### RenderTarget（id 128，语料 15，1 个）
- **解析器是空函数**：`unkn_toggle` +0

### FluidSimulator2D（id 150，语料 13，54 个）
- **已对上，引擎不给名字**：`unkn0_1` +0、`unkn0_2` +1、`unkn0_3` +2、`unkn0_4` +3、`unkn1` +4、`unkn2` +8、`unkn3` +12、`unkn4` +16、`unkn5` +20、`unkn6` +24、`unkn8` +32、`unkn9` +36、`unkn10` +40、`unkn11` +44、`unkn12` +48、`unkn13` +52、`unkn14` +56、`unkn19` +76、`unkn20` +80、`unkn21` +84、`unkn22` +88、`re4_unkn1` +92、`re4_unkn2` +96、`unkn1_23` +100、`unkn1_24` +104、`unkn1_25` +108、`unkn1_27` +116、`unkn1_31` +132、`unkn1_32` +136、`unkn1_33` +140、`unkn1_34_v1` +144、`unkn1_35` +148、`re4_unk2_0` +152、`re4_unk2_1` +156、`re4_unk2_2` +160、`re4_unk2_3` +164、`re4_unk2_4` +168、`re4_unk2_5` +172、`re4_unk2_6` +176、`re4_unk2_7` +180、`re4_unk2_8` +184、`re4_unk2_9` +188、`re4_unk2_10` +192、`unkn1_36_v1` +196、`unkn1_37` +200、`unkn1_38` +204、`unkn1_39_re8` +208、`unkn1_40` +212、`unkn1_42_re8` +216、`unkn2_1` +220、`unkn2_2` +224、`re4_unk3_1` +228、`re4_unk3_2` +232、`re4_unk3_3` +236

### UnknownRE4_197（id 197，语料 11，20 个）
- **已对上，引擎不给名字**：`unkn0` +0、`unkn1` +4、`unkn2` +8、`unkn3` +12、`unkn4` +16、`unkn5` +20、`unkn6` +24、`unkn8` +32、`unkn9` +33、`unkn10` +34、`unkn11` +35、`unkn12` +39、`unkn13` +43、`unkn14` +47、`unkn15` +51、`unkn16` +55、`unkn17` +59、`unkn18` +63、`unkn19` +67、`unkn20` +71

### PtUvSequenceClip（id 169，语料 10，1 个）
- **解析器是空函数**：`unkn1` ?

### AngularVelocity2D（id 210，语料 8，1 个）
- **已对上，引擎不给名字**：`unkn0` +0

### PtPathTranslate（id 213，语料 6，12 个）
- **解析器是空函数**：`unkn1` +0、`unkn2` +4、`unkn3` +8、`unkn4` +16、`unkn5` +20、`unkn6` +24、`unkn7` +28、`unkn8` +32、`unkn9` +36、`unkn10` +40、`unkn11` +44、`unkn12` +48

### TypeNoDrawExpression（id 76，语料 5，12 个）
- **解析器是空函数**：`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?

### AngularVelocity3DDelayFrame（id 205，语料 3，1 个）
- **解析器是空函数**：`unkn1` +4

### ProceduralDistortionDelayFrame（id 216，语料 2，1 个）
- **解析器是空函数**：`unkn` +4

### PtTransform3DClip（id 153，语料 2，1 个）
- **解析器是空函数**：`unkn1` ?

### ScreenSpaceEmitter（id 173，语料 1，2 个）
- **解析器是空函数**：`unkn0` +0、`unkn1` +4

### TypeMeshClip（id 29，语料 1，1 个）
- **解析器是空函数**：`unkn1` ?

### TypeRibbonFixEndExpression（id 60，语料 1，13 个）
- **解析器是空函数**：`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn13` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?

### AttractorClip（id 193，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### DistortionExpression（id 126，语料 0，5 个）
- **解析器是空函数**：`unkn1` ?、`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?

### EffectShader（id 2，语料 0，23 个）
- **解析器是空函数**：`unknShaderCRCHash0` +0、`unknShaderCRCHash1` +4、`unknShaderCRCHash2` +8、`unknShaderCRCHash3` +12、`unknShaderCRCHash4` +16、`unknShaderCRCHash5` +20、`unkn3` +24、`unknShaderCRCHash6` +28、`unknShaderCRCHash7` +32、`unknShaderCRCHash8` +36、`unknShaderCRCHash9` +40、`unknShaderCRCHash10` +44、`unknShaderCRCHash11` +48、`unkn10` +52、`unknShaderCRCHash12` +56、`unknShaderCRCHash13` +60、`unknShaderCRCHash14` +64、`unknShaderCRCHash15` +68、`unknShaderCRCHash16` +72、`unknShaderCRCHash17` +76、`unkn18` +80、`unknShaderCRCHash21` +84、`unkn20` +88

### MeshEmitterClip（id 171，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### MeshEmitterExpression（id 172，语料 0，15 个）
- **解析器是空函数**：`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?

### ProceduralDistortionExpression（id 219，语料 0，18 个）
- **解析器是空函数**：`unkn1` ?、`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?

### PtVelocity3DClip（id 158，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### TexelChannelOperatorClip（id 112，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### Transform2DModifierDelayFrame（id 5，语料 0，1 个）
- **解析器是空函数**：`unkn1` +4

### Transform3DModifierDelayFrame（id 10，语料 0，1 个）
- **解析器是空函数**：`unkn1` +4

### TypeBillboard3DMaterialClip（id 26，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### TypeGpuMeshExpression（id 252，语料 0，13 个）
- **解析器是空函数**：`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?、`unkn19` ?

### TypePolygonClip（id 64，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### TypePolygonTrailMaterialExpression（id 74，语料 0，6 个）
- **未分析**：`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?

### VectorFieldParameterClip（id 176，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### Velocity2DDelayFrame（id 77，语料 0，1 个）
- **解析器是空函数**：`Unkn1` +4

## Wilds 全部

### Spawn（id 17，语料 91183，1 个）
- **已对上，引擎不给名字**：`mhws_unkn_toggle` +83

### ShaderSettings（id 127，语料 67641，5 个）
- **已对上，引擎不给名字**：`unkn13` +56、`dd2_unkn1` +112、`unkn24` +144、`mhws_unkn1` +148、`mhws_unkn2` +152

### EffectShader（id 3，语料 55225，26 个）
- **解析器是空函数**：`unknShaderCRCHash0` +0、`unknShaderCRCHash1` +4、`unknShaderCRCHash2` +8、`unknShaderCRCHash3` +12、`unknShaderCRCHash4` +16、`unknShaderCRCHash5` +20、`unkn3` +24、`unknShaderCRCHash6` +28、`unknShaderCRCHash7` +32、`unknShaderCRCHash8` +36、`unknShaderCRCHash9` +40、`unknShaderCRCHash10` +44、`unknShaderCRCHash11` +48、`unkn10` +52、`unknShaderCRCHash12` +56、`unknShaderCRCHash13` +60、`unknShaderCRCHash14` +64、`unknShaderCRCHash15` +68、`unknShaderCRCHash16` +72、`unknShaderCRCHash17` +76、`unkn18` +80、`unknShaderCRCHash18` +84、`unknShaderCRCHash19` +88、`unknShaderCRCHash20` +92、`unknShaderCRCHash21` +96、`unkn20` +100

### PtVortexelWind（id 203，语料 20184，1 个）
- **解析器非空，脚本没走通**：`unkn7` +8

### PtBehavior（id 137，语料 10355，1 个）
- **解析器是空函数**：`unkn1` +0

### RgbWater（id 244，语料 8058，1 个）
- **解析器非空，脚本没走通**：`mhws_unkn` +4

### TypeMesh（id 30，语料 7366，3 个）
- **解析器有名字，偏移没对上**（EmissiveColor、EmissiveRate、PlaySpeed、PlaySpeedCoef、RotationX、RotationY、RotationZ、SizeX、SizeY、SizeZ、SizeScalar）：`re4_unkn1` +136、`dd2_unkn1` +140、`dd2_unkn2` +148

### Layout（id 21，语料 4769，1 个）
- **解析器是空函数**：`Unkn3` +8

### TypeGpuMesh（id 266，语料 3520，2 个）
- **解析器有名字，偏移没对上**（RotationX、RotationY、RotationZ、SizeX、SizeY、SizeZ、SizeScalar、PlaySpeed、PlaySpeedCoef）：`unknDataSize` ?、`unknData` ?

### PtProjection（id 169，语料 2123，8 个）
- **解析器非空，脚本没走通**：`unkn2` +4、`unkn3` +8、`unkn4` +12、`unkn5` +16、`unkn6` +20、`unkn7` +24、`len_unknString0` +28、`unknString0` +32

### RgbCommonExpression（id 243，语料 1736，15 个）
- **解析器是空函数**：`unkn2` ?、`unkn5` ?、`unkn7` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?、`unkn19` ?、`unkn20` ?、`unkn21` ?、`unkn22` ?

### TypeRibbonChain（id 35，语料 1349，14 个）
- **解析器有名字，偏移没对上**（SizeScalar、WidthSize、FrontDirectionType、FadeSide、ParallelCamera、ToCamera）：`mhws_unkn1` +48、`unkn2_43` +204、`unkn2_44` +208、`unkn2_45` +212、`unkn2_46` +216、`unkn2_47` +220、`dd2_unkn1` +232、`dd2_unkn2` +236、`dd2_unkn3` +240、`dd2_unkn4` +244、`dd2_unkn5` +248、`dd2_unkn6` +252、`dd2_unkn7` +256、`mhws_unkn2` +260

### VanishArea3D（id 92，语料 1301，1 个）
- **引擎有名字**：`wilds_unkn0` +4 → AreaPosition

### Velocity3DExpression（id 85，语料 1217，2 个）
- **解析器是空函数**：`rert_unkn0` +0、`rert_unkn1` +4

### PtColorClip（id 173，语料 1118，1 个）
- **解析器是空函数**：`unkn1` ?

### Attractor（id 200，语料 1107，2 个）
- **解析器非空，脚本没走通**：`unkn1` +36、`unknWild2` +48

### TypeStrainRibbon（id 117，语料 916，5 个）
- **解析器非空，脚本没走通**：`unkn3_4` +48、`wilds_Ukn3_2` +260（名字已知，类型未定）、`wilds_Ukn3_3` +264（名字已知，类型未定）、`wilds_Ukn3_4` +268（名字已知，类型未定）、`wilds_UknLast` +280（名字已知，类型未定）

### TypeLightning3D（id 119，语料 663，76 个）
- **引擎有名字**：`unkn2_59` +252 → FadeByTwist；`unkn2_60` +256 → FadeByTwist
- **解析器有名字，偏移没对上**（RangeDivideNum、LocalRotation）：`unknBitFlag` +0、`unkn2_0` +12、`unkn2_1` +16、`unkn2_2` +20、`unkn2_3` +24、`sb_unkn1` +28、`unkn2_4` +32、`unkn2_5` +36、`unkn2_6` +40、`unkn2_7` +44、`unkn2_8` +48、`unkn2_9` +52、`unkn2_10` +56、`unkn2_11` +60、`unkn2_12` +64、`unkn2_13` +68、`unkn2_14` +72、`unkn2_15` +76、`unkn2_16` +80、`unkn2_17` +84、`unkn2_18` +88、`unkn2_19` +92、`unkn2_20` +96、`unkn2_21` +100、`unkn2_22` +104、`unkn2_23` +108、`unkn2_24` +112、`unkn2_25` +116、`unkn2_26` +120、`unkn2_27` +124、`unkn2_28` +128、`unkn2_29` +132、`unkn2_30` +136、`unkn2_31` +140、`unkn2_32` +144、`unkn2_33` +148、`unkn2_34` +152、`unkn2_35` +156、`unkn2_36` +160、`unkn2_37` +164、`unkn2_38` +168、`unkn2_39` +172、`unkn2_40` +176、`unkn2_41` +180、`unkn2_42` +184、`unkn2_43` +188、`unkn2_44` +192、`unkn2_45` +196、`unkn2_46` +200、`unkn2_47` +204、`unkn2_48` +208、`unkn2_49` +212、`unkn2_50` +216、`unkn2_51` +220、`unkn2_52` +224、`unkn2_53` +228、`unkn2_54` +232、`unkn2_55` +236、`unkn2_56` +240、`unkn2_57` +244、`unkn2_58` +248、`unkn2_61` +260、`unkn2_62` +264、`unkn2_63` +268、`dd2_unkn` +272、`unkn2_64` +276、`unkn2_65` +280、`unkn2_66` +284、`unkn2_67` +288、`unkn2_68` +292、`unkn2_69` +296、`unkn2_70` +300、`unkn2_71` +304、`unkn2_72` +308

### DepthOperator（id 192，语料 630，6 个）
- **解析器有名字，偏移没对上**（Distance、YNormalRange、FrontDistance、BackDistance、StickDistance、BounceRate、DripSpeed、DripSpeedCoef、DripLength、DripThreshold、BounceAngleRate）：`unkn0` +0、`unkn3` +12、`unkn4` +16、`unkn5` +20、`mhws_unkn1` +24、`mhws_unkn2` +48

### PtColliderAction（id 168，语料 625，3 个）
- **解析器有名字，偏移没对上**（Radius）：`dd2_unkn0` +60、`dd2_unkn1` +64、`wilds_unkn0` ?

### TypeNoDrawExpression（id 79，语料 594，14 个）
- **解析器是空函数**：`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?

### PtFreezer（id 246，语料 557，10 个）
- **解析器非空，脚本没走通**：`unkn0` +0、`unkn1` +4、`unkn2` +8、`unkn3` +12、`unkn4` +16、`unkn5` +20、`unkn6` +24、`unkn7` +28、`unkn8` +32、`unkn9` +36

### VortexelWindEmitter（id 205，语料 505，11 个）
- **解析器非空，脚本没走通**：`unkn0` +36、`unkn1` +44、`unkn2` +48、`unkn3` +52、`unkn4` +56、`unkn5` +60、`unkn6` +64、`unkn7` +68、`unkn8` +72、`unkn9` +76、`unkn10` +80

### PtPathTranslate（id 228，语料 368，12 个）
- **解析器有名字，偏移没对上**（Paths）：`unkn1` +0、`unkn2` +4、`unkn3` +8、`unkn4` +16、`unkn5` +20、`unkn6` +24、`unkn7` +28、`unkn8` +32、`unkn9` +36、`unkn10` +40、`unkn11` +44、`unkn12` +48

### VortexelCollider（id 208，语料 337，7 个）
- **解析器非空，脚本没走通**：`Unkn0` +4、`Unkn1` +8、`Unkn2` +12、`Unkn3` +16、`Unkn4` +20、`Unkn5` +24、`Unkn6` +28

### Transform3DClip（id 13，语料 300，1 个）
- **解析器是空函数**：`unkn1` ?

### Transform3DModifierDelayFrame（id 11，语料 296，1 个）
- **解析器是空函数**：`unkn1` +4

### ScaleAnimDelayFrame（id 89，语料 265，1 个）
- **解析器是空函数**：`unkn2` +4

### PtLightningBranchAction（id 126，语料 225，7 个）
- **解析器是空函数**：`unkn0` +0、`unkn1` +4、`unkn2` +8、`unkn3` +12、`unkn4` +16、`unkn5` +20、`unkn6` +24

### TypeRibbonLengthMaterial（id 42，语料 203，35 个）
- **解析器非空，脚本没走通**：`unkn4` +12、`unkn5` +16、`unkn6` +20、`unkn7` +24、`unkn8` +28、`unkn9` +32、`unkn10` +36、`unkn11` +40、`unkn12` +44、`dd2_unkn2` +52、`wilds_unkn1` +56、`dd2_unkn3` +60、`unkn13` +64、`unkn14` +68、`unkn15` +72、`unkn17` +76、`unkn18` +80、`unkn19` +84、`unkn20` +88、`unkn21` +92、`unkn22` +96、`unkn23` +100、`unkn24` +104、`unkn25` +108、`unkn26` +112、`unkn30` +128、`unkn31` +132、`unkn32` +136、`unkn33` +140、`unkn34` +144、`unkn35` +148、`unkn36` +152、`unkn37` +156、`unkn38` +160、`unkn39` +164

### PtTransform3DClip（id 160，语料 169，1 个）
- **解析器是空函数**：`unkn1` ?

### DistortionExpression（id 130，语料 137，5 个）
- **解析器是空函数**：`unkn1` ?、`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?

### AttractorExpression（id 202，语料 125，1 个）
- **解析器是空函数**：`unkn11_Wilds` ?

### TypeGpuMeshExpression（id 268，语料 116，13 个）
- **解析器是空函数**：`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?、`unkn19` ?

### Velocity3DDelayFrame（id 83，语料 104，1 个）
- **解析器是空函数**：`unkn2` +4

### TypeGpuRibbonLengthExpression（id 265，语料 98，10 个）
- **解析器是空函数**：`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?

### RgbWaterExpression（id 245，语料 80，20 个）
- **解析器是空函数**：`unkn2` ?、`unkn5` ?、`unkn7` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?、`unkn19` ?、`unkn20` ?、`unkn21` ?、`unkn22` ?、`unkn23` ?、`unkn24` ?、`unkn25` ?、`unkn26` ?、`unkn27` ?

### Trigger（id 254，语料 74，3 个）
- **解析器非空，脚本没走通**：`unkn1` +0、`unkn2` +4、`unkn3` +8

### VortexelIndoorMask（id 210，语料 68，5 个）
- **解析器非空，脚本没走通**：`Unkn0` +4、`Unkn1` +8、`Unkn2` +12、`Unkn3` +16、`Unkn4` +20

### VortexelWindEmitterExpression（id 206，语料 63，10 个）
- **解析器是空函数**：`unkn1` ?、`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?

### PtColorMixer（id 174，语料 62，3 个）
- **解析器非空，脚本没走通**：`Unkn0` +0、`Unkn2` +8、`Unkn3` +12

### TypeMeshClip（id 31，语料 60，1 个）
- **未分析**：`unkn1` ?

### Listener（id 182，语料 55，2 个）
- **解析器非空，脚本没走通**：`Unkn1` +0、`Unkn2` +4

### TypeLightning3DExpression（id 120，语料 55，44 个）
- **解析器是空函数**：`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?、`unkn19` ?、`unkn20` ?、`unkn21` ?、`unkn22` ?、`unkn23` ?、`unkn24` ?、`unkn25` ?、`unkn26` ?、`unkn27` ?、`unkn28` ?、`unkn29` ?、`unkn30` ?、`unkn31` ?、`unkn32` ?、`unkn33` ?、`unkn34` ?、`unkn35` ?、`unkn36` ?、`unkn37` ?、`unkn38` ?、`unkn39` ?、`unkn40` ?、`unkn41` ?、`unkn42` ?、`unkn43` ?、`unkn44` ?、`unkn45` ?、`unkn46` ?、`unkn47` ?、`unkn48` ?

### FluidParticleEmitterTarget（id 279，语料 50，4 个）
- **解析器是空函数**：`Unkn1` +4、`Unkn2` +8、`Unkn3` +12、`Unkn4` +16

### TypeGpuPolygonExpression（id 261，语料 50，19 个）
- **未分析**：`unkn1` ?、`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?、`unkn19` ?

### TypeGpuMeshClip（id 267，语料 44，1 个）
- **解析器是空函数**：`unkn1` ?

### FluidParticle2DSimulator（id 157，语料 43，26 个）
- **未分析**：`unkn1` +4、`unkn2` +8、`unkn3` +12、`unkn4` +16、`unkn5` +20、`unkn9` +36、`unkn10` +40、`unkn11` +44、`unkn17` +68、`unkn18` +72、`unkn19` +76、`unkn22` +88、`unkn23` +92、`unkn26` +104、`unkn27` +108、`unkn28` +112、`unkn29` +116、`unkn30` +120、`unkn31` +124、`unkn32` +128、`unkn35` +140、`unkn36` +144、`unkn37` +148、`unkn38` +152、`unkn39` +156、`unkn40` +160

### TypeRibbonChainExpression（id 62，语料 28，17 个）
- **未分析**：`unkn1` ?、`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?

### TypeRibbonLengthMaterialClip（id 43，语料 26，1 个）
- **解析器是空函数**：`unkn1` ?

### TypeBillboard3DMaterialClip（id 28，语料 24，1 个）
- **解析器是空函数**：`unkn1` ?

### RotateAnimDelayFrame（id 86，语料 23，1 个）
- **解析器是空函数**：`unkn2` +4

### TypeRibbonFollowExpression（id 60，语料 23，14 个）
- **解析器是空函数**：`unkn2` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?

### PtVortexelWindExpression（id 204，语料 18，4 个）
- **解析器是空函数**：`unkn1` ?、`unkn2` ?、`unkn3` ?、`unkn4` ?

### TypeRibbonParticleExpression（id 65，语料 15，16 个）
- **解析器是空函数**：`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?

### PointCloudEmitter（id 274，语料 13，1 个）
- **解析器非空，脚本没走通**：`Unkn1` +4

### TypeStrainRibbonExpression（id 118，语料 13，21 个）
- **解析器是空函数**：`unkn1` ?、`unkn2` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?、`unkn19` ?、`unkn20` ?、`unkn21` ?、`unkn22` ?、`unkn23` ?、`unkn24` ?

### EmitterHSVExpression（id 240，语料 12，20 个）
- **解析器是空函数**：`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?、`unkn19` ?、`unkn20` ?、`unkn21` ?

### TypeRibbonLengthMaterialExpression（id 44，语料 12，14 个）
- **解析器是空函数**：`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?

### PtAngularVelocity3D（id 222，语料 10，1 个）
- **已对上，引擎不给名字**：`unkn1` +0

### AngularVelocity3DDelayFrame（id 220，语料 8，1 个）
- **解析器是空函数**：`unkn1` +4

### PtCollisionInfluence（id 171，语料 8，8 个）
- **解析器非空，脚本没走通**：`Unkn1` +4、`Unkn2` +8、`Unkn3` +12、`Unkn4` +16、`Unkn5` +20、`Unkn6` +24、`Unkn7` +28、`Unkn8` +32

### TypePolygonClip（id 67，语料 7，1 个）
- **解析器是空函数**：`unkn1` ?

### EmitterColorClip（id 20，语料 6，1 个）
- **解析器是空函数**：`unkn1` ?

### ProceduralDistortionClip（id 234，语料 6，1 个）
- **解析器是空函数**：`unkn1` ?

### ParentOptionsExpression（id 16，语料 5，8 个）
- **解析器是空函数**：`unkn1_1` ?、`unkn1_2` ?、`unkn1_3` ?、`unkn1_4` ?、`unkn1_5` ?、`unkn1_6` ?、`unkn1_7` ?、`unkn1_8` ?

### PtAngularVelocity3DExpression（id 223，语料 5，7 个）
- **解析器是空函数**：`unkn1` ?、`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?

### TypeGpuMeshTrail（id 269，语料 5，54 个）
- **解析器非空，脚本没走通**：`unknFlags` +0、`unkn1` +4、`unkn2` +16、`unkn5` +28、`unkn6` +32、`unkn7` +36、`unkn8` +40、`unkn9` +44、`unkn10` +48、`unkn11` +52、`unkn12` +56、`unkn13` +60、`unkn14` +64、`unkn15` +68、`unkn16` +72、`unkn17` +76、`unkn18` +80、`unkn19` +84、`unkn20` +88、`unkn21` +92、`unkn22` +96、`unkn23` +100、`unkn24` +104、`unkn25` +108、`unkn26` +112、`unkn27` +116、`unkn28` +120、`unkn29` +124、`unkn30` +128、`unkn31` +132、`unkn32` +156、`unkn37` +160、`unkn38` +164、`unkn40` +176、`unkn41` +180、`unkn42` +184、`unkn43` +188、`unkn44` +192、`unkn45` +196、`unkn46` +200、`unkn47` +204、`unkn48` +208、`unkn49` +212、`unkn50` +216、`unkn51` +220、`unkn52` +224、`unkn53` +228、`Wilds_Ukn11` +236（名字已知，类型未定）、`unkn54` +240、`unkn55` +244、`Wilds_Ukn12` +248（名字已知，类型未定）、`unkn56` +260、`len_unkPath` ?、`unkPath` ?

### TypeRibbonFollowMaterial（id 39，语料 5，4 个）
- **解析器非空，脚本没走通**：`Unkn1` +80、`Unkn2` +124、`Unkn3` +128、`Unkn4` +132

### PtTransform2DClip（id 163，语料 4，1 个）
- **解析器是空函数**：`unkn1` ?

### DestinationCSV（id 248，语料 3，13 个）
- **解析器非空，脚本没走通**：`unkn1` +0、`unkn2` +4、`unkn3` +8、`unkn4` +12、`unkn5` +16、`unkn6` +20、`unkn7` +24、`len_unknPath2` ?、`unknPath2` ?、`len_unknPath3` ?、`unknPath3` ?、`len_unknPath4` ?、`unknPath4` ?

### RenderTarget（id 132，语料 3，1 个）
- **解析器是空函数**：`unkn_toggle` +0

### TypeRibbonFixEndExpression（id 63，语料 2，13 个）
- **解析器是空函数**：`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn13` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?

### AttractorClip（id 201，语料 1，1 个）
- **解析器是空函数**：`unkn1` ?

### PtLightningColliderAction（id 125，语料 1，2 个）
- **未分析**：`unkn2` +4、`unkn3` +8

### PtVelocity3DClip（id 165，语料 1，1 个）
- **解析器是空函数**：`unkn1` ?

### TypeGpuMeshTrailClip（id 270，语料 1，1 个）
- **解析器是空函数**：`unkn1` ?

### TypeRibbonParticleMaterialExpression（id 59，语料 1，12 个）
- **解析器是空函数**：`unkn1` ?、`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn10` ?、`unkn12` ?、`unkn14` ?、`unkn15` ?

### AngularVelocity2D（id 225，语料 0，1 个）
- **解析器有名字，偏移没对上**（Radius、AddRadius、Rotation、RotationScaleX、RotationScaleY、RotationSpeed）：`unkn0` +0

### AngularVelocity2DDelayFrame（id 224，语料 0，1 个）
- **解析器是空函数**：`Unkn1` +4

### DrawOverlay（id 216，语料 0，1 个）
- **解析器是空函数**：`UnknWilds` +12

### FluidEmitter2D（id 153，语料 0，13 个）
- **引擎有名字**：`unkn9` +40 → Angle
- **已对上，引擎不给名字**：`unkn0` +0、`dd2_unkn0` +4、`unkn1` +8、`unkn2` +12、`unkn3` +16、`unkn4` +20、`unkn5` +24、`unkn6` +28、`unkn7` +32、`unkn8` +36、`unkn10` +44、`unkn11` +48

### FluidEmitter2DExpression（id 155，语料 0，2 个）
- **解析器是空函数**：`unkn2` ?、`unkn3` ?

### FluidSimulator2D（id 156，语料 0，56 个）
- **引擎有名字**：`unkn8` +32 → PostEdgeDropoff；`unkn19` +76 → DensityAlphaMax；`unkn1_27` +116 → InternalInherit；`unkn1_31` +132 → ProjectionTimeScale
- **解析器有名字，偏移没对上**（EdgeDropoff）：`unkn0_1` +0、`unkn0_2` +1、`unkn0_3` +2、`unkn0_4` +3、`unkn1` +4、`unkn2` +8、`unkn3` +12、`unkn4` +16、`unkn5` +20、`unkn6` +24、`unkn9` +36、`unkn10` +40、`unkn11` +44、`unkn12` +48、`unkn13` +52、`unkn14` +56、`unkn20` +80、`unkn21` +84、`unkn22` +88、`re4_unkn1` +92、`re4_unkn2` +96、`unkn1_23` +100、`unkn1_24` +104、`unkn1_25` +108、`unkn1_32` +136、`unkn1_33` +140、`unkn1_34_v2` +144、`unkn1_35` +148、`re4_unk2_0` +152、`re4_unk2_1` +156、`re4_unk2_2` +160、`re4_unk2_3` +164、`re4_unk2_4` +168、`re4_unk2_5` +172、`re4_unk2_6` +176、`re4_unk2_7` +180、`re4_unk2_8` +184、`re4_unk2_9` +188、`re4_unk2_10` +192、`unkn1_36_v2` +196、`unkn1_37` +200、`unkn1_38` +204、`unkn1_39_re8` +208、`unkn1_40` +212、`unkn1_42_re8` +216、`dd2_unk1` +220、`dd2_unk2` +224、`unkn2_1` +228、`unkn2_2` +232、`re4_unk3_1` +236、`re4_unk3_2` +240、`re4_unk3_3` +244

### MeshEmitterClip（id 179，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### MeshEmitterExpression（id 180，语料 0，15 个）
- **解析器是空函数**：`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?

### ProceduralDistortionDelayFrame（id 232，语料 0，1 个）
- **解析器是空函数**：`unkn` +4

### ProceduralDistortionExpression（id 235，语料 0，18 个）
- **解析器是空函数**：`unkn1` ?、`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?

### PtAngularVelocity2DExpression（id 227，语料 0，7 个）
- **解析器是空函数**：`unkn1` +0、`unkn2` +4、`unkn3` +8、`unkn4` +12、`unkn5` +16、`unkn6` +20、`unkn7` +24

### PtUvSequenceClip（id 177，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### ScreenSpaceEmitter（id 181，语料 0，2 个）
- **解析器是空函数**：`unkn0` +0、`unkn1` +4

### TexelChannelOperatorClip（id 115，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### TextureUnit（id 100，语料 0，1 个）
- **解析器是空函数**：`unkn1` +0

### Transform2DModifierDelayFrame（id 6，语料 0，1 个）
- **解析器是空函数**：`unkn1` +4

### TypeGpuLightning3D（id 272，语料 0，82 个）
- **引擎有名字**：`unkn14` +64 → InitialWidth；`unkn15` +68 → InitialWidth；`unkn18` +80 → TerminalWidth；`unkn19` +84 → TerminalWidth；`unkn20` +88 → WidthScale；`unkn21` +92 → WidthScale；`unkn22` +96 → InitialPosition；`unkn23` +100 → InitialPosition；`unkn24` +104 → InitialPosition；`unkn25` +108 → SecondPosition；`unkn26` +112 → SecondPosition；`unkn27` +116 → SecondPosition；`unkn28` +120 → ThirdPosition；`unkn29` +124 → ThirdPosition；`unkn30` +128 → ThirdPosition；`unkn31` +132 → AddVelocity；`unkn32` +136 → AddVelocity；`unkn33` +140 → AddVelocityCoef；`unkn34` +144 → AddVelocityCoef；`unkn35` +148 → EdgeFadePosition；`unkn36` +152 → EdgeFadeRate；`unkn40` +168 → MoveFrame；`unkn41` +172 → MoveFrame
- **解析器有名字，偏移没对上**（InitialShapeRangeX、InitialShapeRangeY、InitialShapeRangeZ、InitialShapeRangeDivideNum、InitialShapeLocalRotation、SecondShapeRangeX、SecondShapeRangeY、SecondShapeRangeZ、SecondShapeRangeDivideNum、SecondShapeLocalRotation、ThirdShapeRangeX、ThirdShapeRangeY、ThirdShapeRangeZ、ThirdShapeRangeDivideNum、ThirdShapeLocalRotation）：`unkn0` +0、`unkn1` +12、`unkn2` +16、`unkn3` +20、`unkn4` +24、`unkn5` +28、`unkn6` +32、`unkn7` +36、`unkn8` +40、`unkn9` +44、`unkn10` +48、`unkn11` +52、`unkn12` +56、`unkn13` +60、`unkn16` +72、`unkn17` +76、`unkn37` +156、`unkn38` +160、`unkn39` +164、`unkn42` +176、`unkn2_1` +180、`unkn2_2` +184、`unkn2_3` +188、`unkn2_4` +192、`unkn2_5` +196、`unkn2_6` +200、`unkn2_7` +204、`unkn2_8` +208、`unkn2_9` +212、`unkn2_10` +216、`unkn2_11` +220、`unkn2_12` +224、`unkn2_13` +228、`unkn3_1` +232、`unkn3_2` +236、`unkn3_3` +240、`unkn3_4` +244、`unkn3_5` +248、`unkn3_6` +252、`unkn3_7` +256、`unkn3_8` +260、`unkn3_9` +264、`unkn3_10` +268、`unkn3_11` +272、`unkn3_12` +276、`unkn3_13` +280、`unkn4_1` +284、`unkn4_2` +288、`unkn4_3` +292、`unkn4_4` +296、`unkn4_5` +300、`unkn4_6` +304、`unkn4_7` +308、`unkn4_8` +312、`unkn4_9` +316、`unkn4_10` +320、`unkn4_11` +324、`unkn4_12` +328、`unkn4_13` +332

### TypeGpuMeshTrailExpression（id 271，语料 0，17 个）
- **解析器是空函数**：`unkn1` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?、`unkn19` ?、`unkn20` ?、`unkn21` ?

### TypeLightning3DMaterial（id 121，语料 0，69 个）
- **引擎有名字**：`unkn2_0` +12 → AttractPos；`unkn2_1` +16 → AttractPos；`unkn2_2` +20 → AttractAngle；`unkn2_3` +24 → AttractAngle；`unkn2_8` +48 → SectionSinScale / FractalSinScale；`unkn2_9` +52 → SectionSinScale / SubSectionHeightScale / FractalSinScale；`unkn2_10` +56 → SectionCosScale / FractalCosScale；`unkn2_11` +60 → SectionCosScale / FractalCosScale；`unkn2_12` +64 → SectionHeightScale / FractalHeightScale；`unkn2_13` +68 → SectionHeightScale / FractalHeightScale；`unkn2_16` +80 → SubSectionSinScale；`unkn2_17` +84 → SubSectionSinScale；`unkn2_18` +88 → SubSectionCosScale；`unkn2_19` +92 → SubSectionCosScale；`unkn2_20` +96 → SubSectionHeightScale；`unkn2_21` +100 → SubSectionHeightScale；`unkn2_26` +120 → AttractRate；`unkn2_27` +124 → AttractRate；`unkn2_28` +128 → InitialWidth；`unkn2_29` +132 → InitialWidth；`unkn2_30` +136 → BetweenWidth；`unkn2_31` +140 → BetweenWidth；`unkn2_32` +144 → TerminalWidth；`unkn2_33` +148 → TerminalWidth；`unkn2_34` +152 → WidthScale；`unkn2_35` +156 → WidthScale；`unkn2_36` +160 → InitialPosition；`unkn2_37` +164 → InitialPosition；`unkn2_38` +168 → InitialPosition；`unkn2_39` +172 → AddVelocity；`unkn2_40` +176 → AddVelocity；`unkn2_41` +180 → AddVelocityCoef；`unkn2_42` +184 → AddVelocityCoef；`unkn2_43` +192 → RotationX；`unkn2_44` +196 → RotationX；`unkn2_45` +200 → RotationY；`unkn2_46` +204 → RotationY；`unkn2_47` +208 → RotationZ；`unkn2_48` +212 → RotationZ；`unkn2_49` +216 → EdgeFadePosition；`unkn2_50` +220 → EdgeFadeRate
- **解析器有名字，偏移没对上**（RangeX、RangeY、RangeZ、RangeDivideNum、LocalRotation）：`unknBitFlag` +0、`sb_unkn0` +28、`unkn2_4` +32、`unkn2_5` +36、`unkn2_6` +40、`unkn2_7` +44、`unkn2_14` +72、`unkn2_15` +76、`unkn2_22` +104、`unkn2_23` +108、`unkn2_24` +112、`unkn2_25` +116、`unkn2_51` +224、`unkn2_52` +228、`unkn2_53` +232、`unkn2_54` +236、`unkn2_55` +240、`unkn2_56` +244、`unkn2_57` +248、`unkn2_58` +252、`unkn2_59` +256、`unkn2_60` +260、`unkn2_61` +264、`unkn2_62` +268、`unkn2_63` +272、`unkn2_64` +276、`unkn2_65` +280、`unkn2_66` +284

### TypeLightning3DMaterialClip（id 122，语料 0，2 个）
- **解析器是空函数**：`ukn7` +24（名字已知，类型未定）、`ukn9` +36（名字已知，类型未定）

### TypeLightning3DMaterialExpression（id 123，语料 0，40 个）
- **解析器是空函数**：`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn15` ?、`unkn16` ?、`unkn17` ?、`unkn18` ?、`unkn19` ?、`unkn20` ?、`unkn21` ?、`unkn22` ?、`unkn23` ?、`unkn24` ?、`unkn25` ?、`unkn26` ?、`unkn27` ?、`unkn28` ?、`unkn29` ?、`unkn30` ?、`unkn31` ?、`unkn32` ?、`unkn33` ?、`unkn34` ?、`unkn35` ?、`unkn36` ?、`unkn37` ?、`unkn38` ?、`unkn39` ?、`unkn40` ?、`unkn41` ?、`unkn42` ?、`unkn43` ?、`unkn44` ?

### TypeNodeBillboardExpression（id 152，语料 0，10 个）
- **解析器是空函数**：`unkn33` ?、`unkn34` ?、`sizeUnkn` ?、`sizeUnknRand` ?、`scaleUnkn` ?、`scaleUnknRand` ?、`speedUnkn` ?、`speedUnknRand` ?、`unkn41` ?、`unkn42` ?

### TypePolygonTrailMaterialExpression（id 77，语料 0，6 个）
- **解析器是空函数**：`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?

### TypeRibbonChainMaterial（id 45，语料 0，61 个）
- **解析器非空，脚本没走通**：`unkn0` +0、`unkn3` +12、`unkn4` +16、`unkn5` +20、`unkn6` +24、`unkn7` +28、`unkn8` +32、`unkn9` +36、`unkn10` +40、`unkn11` +44、`unkn12` +48、`unkn13` +52、`unkn14` +56、`unkn15` +60、`unkn16` +64、`unkn17` +68、`unkn18` +72、`unkn19` +76、`unkn20` +80、`unkn22` +96、`unkn23` +100、`unkn24` +104、`unkn25` +108、`unkn26` +112、`unkn27` +116、`unkn28` +120、`unkn29` +124、`unkn30` +128、`unkn31` +132、`unkn32` +136、`unkn33` +140、`unkn34` +144、`unkn35` +148、`unkn36` +152、`unkn37` +156、`unkn38` +160、`unkn39` +164、`unkn40` +168、`unkn41` +172、`unkn42` +176、`unkn43` +180、`unkn44` +184、`unkn45` +188、`unkn46` +192、`unkn47` +196、`unkn48` +200、`unkn49` +204、`unkn50` +208、`unkn51` +212、`unkn52` +216、`unkn53` +220、`unkn54` +224、`unkn55` +228、`unkn56` +232、`unkn57` +236、`unkn58` +240、`unkn59` +244、`unkn60` +248、`unkn61` +252、`unkn62` +256、`unkn63` +260

### TypeRibbonChainMaterialClip（id 46，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### TypeRibbonChainMaterialExpression（id 47，语料 0，18 个）
- **解析器是空函数**：`unkn1_0` +0、`unkn1_1` +4、`unkn1_2` +8、`unkn1_3` +12、`unkn1_4` +16、`unkn1_5` +20、`unkn1_6` +24、`unkn1_7` +28、`unkn1_8` +32、`unkn1_9` +36、`unkn1_10` +40、`unkn1_12` +44、`unkn1_13` +48、`unkn1_14` +52、`unkn1_15` +56、`unkn1_16` +60、`unkn1_17` +64、`unkn1_18` +68

### TypeRibbonFixEndMaterial（id 48，语料 0，33 个）
- **解析器非空，脚本没走通**：`unkn0` +0、`unkn3` +12、`unkn4` +16、`unkn5` +20、`unkn6` +24、`unkn7` +28、`unkn8` +32、`unkn9` +36、`unkn10` +40、`unkn11` +44、`unkn12` +48、`unkn13` +52、`unkn14` +56、`unkn15` +60、`unkn16` +64、`unkn17` +68、`unkn18` +72、`unkn19` +76、`unkn20` +80、`unkn21` +84、`unkn39` +88、`unkn40` +92、`unkn41` +96、`unkn42` +100、`unkn43` +104、`unkn44` +108、`unkn45` +112、`unkn46` +116、`unkn47` +120、`unkn48` +124、`unkn49` +128、`unkn50` +132、`unkn51` +136

### TypeRibbonFixEndMaterialClip（id 49，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### TypeRibbonFixEndMaterialExpression（id 50，语料 0，18 个）
- **解析器是空函数**：`unkn1_0` +0、`unkn1_1` +4、`unkn1_2` +8、`unkn1_3` +12、`unkn1_4` +16、`unkn1_5` +20、`unkn1_6` +24、`unkn1_7` +28、`unkn1_8` +32、`unkn1_9` +36、`unkn1_10` +40、`unkn1_12` +44、`unkn1_13` +48、`unkn1_14` +52、`unkn1_15` +56、`unkn1_16` +60、`unkn1_17` +64、`unkn1_18` +68

### TypeRibbonFollowMaterialClip（id 40，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### TypeRibbonFollowMaterialExpression（id 41，语料 0，12 个）
- **解析器是空函数**：`unkn1` ?、`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn7` ?、`unkn8` ?、`unkn10` ?、`unkn12` ?、`unkn14` ?、`unkn15` ?

### TypeRibbonLightweight（id 37，语料 0，14 个）
- **解析器有名字，偏移没对上**（SizeScalar、WidthSize）：`unkn1` +0、`unkn2_0` +12、`unkn2_1` +16、`unkn2_2` +20、`unkn2_3` +24、`unkn2_4` +28、`unkn2_5` +32、`unkn2_6` +36、`sb_unkn0` +40、`unkn2_7` +44、`dd2_unkn` +48、`unkn2_8` +52、`unkn2_9` +56、`unkn2_10` +60

### TypeRibbonParticleMaterialClip（id 58，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### TypeStrainRibbonMaterial（id 54，语料 0，67 个）
- **解析器非空，脚本没走通**：`unkn0` +0、`unkn1` +4、`unkn2` +8、`unkn3` +12、`unkn4` +16、`unkn5` +20、`unkn6` +24、`unkn7` +28、`unkn8` +32、`unkn9` +36、`unkn10` +40、`unkn11` +44、`unkn12` +48、`unkn13` +52、`unkn14` +56、`unkn15` +60、`unkn16` +64、`unkn17` +68、`unkn18` +72、`unkn22` +88、`unkn23` +92、`unkn24` +96、`unkn25` +100、`unkn26` +104、`unkn27` +108、`unkn28` +112、`unkn29` +116、`unkn30` +120、`unkn31` +124、`unkn32` +128、`unkn33` +132、`unkn34` +136、`wildsUnkn0` +140、`unkn35` +144、`unkn36` +148、`unkn37` +152、`unkn38` +156、`unkn39` +160、`unkn40` +164、`unkn41` +168、`unkn42` +172、`wildsUnkn1` +176、`unkn43` +188、`wildsUnkn4` +192、`unkn44` +196、`unkn45` +200、`unkn47` +204、`unkn48` +208、`unkn49` +212、`unkn50` +216、`unkn51` +220、`unkn52` +224、`unkn53` +228、`unkn54` +232、`unkn55` +236、`unkn56` +240、`unkn57` +244、`unkn58` +248、`unkn59` +252、`unkn60` +256、`unkn61` ?、`unkn62` ?、`unkn63` ?、`unkn64` ?、`unkn65` ?、`unkn66` ?、`unkn67` ?

### TypeStrainRibbonMaterialClip（id 55，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### TypeStrainRibbonMaterialExpression（id 56，语料 0，11 个）
- **解析器是空函数**：`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn11` ?、`unkn12` ?、`unkn13` ?、`unkn14` ?、`unkn16` ?、`unkn18` ?、`unkn20` ?、`unkn21` ?

### UnitCullingExpression（id 135，语料 0，9 个）
- **解析器是空函数**：`unkn1` ?、`unkn2` ?、`unkn3` ?、`unkn4` ?、`unkn5` ?、`unkn6` ?、`unkn8` ?、`unkn9` ?、`unkn10` ?

### VectorFieldParameterClip（id 184，语料 0，1 个）
- **解析器是空函数**：`unkn1` ?

### Velocity2D（id 81，语料 0，4 个）
- **已对上，引擎不给名字**：`unkn1_6` +28、`re4_unkn0` +32、`re4_unkn2_0` +64、`re4_unkn2_1` +68

### Velocity2DDelayFrame（id 80，语料 0，1 个）
- **解析器是空函数**：`Unkn1` +4

### WindInfluence3DDelayFrame（id 198，语料 0，1 个）
- **解析器是空函数**：`unkn2` +4
