# Re4Native：在本进程里运行 re4.exe 的粒子代码

把 RE4R 的 `re4.exe`（EFX 版本 3539837，文件大小 233,689,576，时间戳 0x69A8F652）原样映射到
0x140000000，直接调用其中的 EFX 函数，与 `docs/development/RE4-EFX-RUNTIME.zh-CN.md`（下称"规格"）逐位比较。
不连接游戏进程，exe 与 RE4_EXTRACT 只读。

## 运行

```bash
python -I -X utf8 -u run_all.py [输出目录] [实验名 ...]     # 默认输出到 %TEMP%\re4native
python -I -X utf8 -u exp_scaleanim.py                      # 单独运行一个实验
python -I -X utf8 -u pe_image.py "<re4.exe>" 3d1d830 80     # 反汇编一个 RVA
```

依赖 numpy；`scan_loops.py` 与 `pe_image.py` 另需 pefile、capstone。exe 路径可用环境变量 `RE4_EXE` 覆盖。
`run_all.py` 让每个实验在独立子进程中运行：fast fail（0xC0000409）或未捕获的访问违例只结束该子进程，
汇总里记为 `CRASH`。

## 文件

| 文件 | 内容 |
|---|---|
| `re4native.py` | 映射与调用框架：按节复制（不重定位、不运行入口点与 TLS 回调）；kernel32 / MSVC 运行库 / UCRT 导入按名绑定，其余导入指向不可访问的陷阱槽，调用时抛 `UnresolvedImportCalled`（带导入名）；向量化异常处理把访问违例变成 `NativeFault`（故障 RVA、访问地址、exe 内回溯）；`install_settings` 伪造特效设置单例；`Emitter` 提供 E 与 ctx+0x118 |
| `refsim.py` | 规格 §1.6 的参考随机数（种子、xorshift32、CenterSpread、ScaleAnim 与 Blink 的抽取顺序） |
| `exp_map.py` | 映射、导入绑定、故障与陷阱的捕获 |
| `exp_scaleanim.py` | ScaleAnim 闭式函数 3cfd420 与逐粒子步 3d1d830 + 基值捕获 3d1dd00 |
| `exp_blink.py` | Blink 3dc7a20 |
| `exp_loader.py` | 引擎自己的构造函数 → 加载器（槽 5）→ 块构建器（槽 15），输入为语料 .efx 的原始字节 |
| `exp_rng.py` | 两个播种函数与 ScaleAnim / Blink 出生抽取对照 `refsim.py` |
| `exp_pipeline.py` | 整个逐粒子管线 3d05350，ctx 中只挂 ScaleAnim 与 Blink |
| `scan_loops.py` | 静态扫描 3d08480 与 3d05350 可达函数的导入、全局变量、间接调用、TLS 访问 |

## 结果

| 实验 | 结果 | 状态 |
|---|---|---|
| 映射 | 映射在 0x140000000 成功，代码字节与文件一致；398 个导入绑定，208 个指向陷阱（USER32、WS2_32、WINHTTP、d3d11、dxgi 等）。映射没有障碍：重定位表、TLS 目录、DYNAMIC_BASE 在固定基址下都不需要处理。访问违例与陷阱调用都以 Python 异常返回 | 已确认 |
| ScaleAnim 闭式 3cfd420 | 29,280 组 (t, base, add, coef) 与规格公式（float32、引擎运算顺序、CRT powf）逐位一致，含 Coef 0、1、>1、<0 | 已确认 |
| ScaleAnim 逐粒子步 | 9 种情形 × LegacyMode2018 两种取值共 18 组，每帧四个尺寸槽逐位一致：Coef 1 线性、Coef 0 冻结、夹在 0 以上、30 fps 与 60 fps 同一曲线、槽位步长 0x34 | 已确认 |
| ScaleAnim 首帧增量 | Add 0.1、Coef 0.5：`[g+0xeb2]` = 0 时首帧 +0.05（Add·Coef），= 1 时 +0.1（Add） | 已确认 |
| ScaleAnim Coef < 0 | 60 fps 下 t 为整数，powf 不出 NaN，尺寸正负交替地趋于定值；t 非整数（50 fps）时得 NaN 并写入尺寸槽，夹紧挡不住 | 已确认 |
| ScaleAnim 门控 | `[p+4] & 0x800`、`[ctx+0x130] & 0x10` 时不写；`& 0x20` 时每次重走出生分支，age0 跟随年龄，尺寸不变；r9d = 0 分支照常算 U，X、Y 的 Add 乘 100.0（常量 79c2eec），不算 Z | 已确认 |
| Blink 3dc7a20 | 6 种情形共 840 帧，`p+0x50` 与相位逐位一致：三角波、HighFrequency 由慢相位驱动、fL = 0 冻结、非整数 fH、30 fps；宽度以 2W 存入槽；粒子未存活时不写；无块的新粒子写 1.0 | 已确认 |
| 加载器 + 构建器 | ScaleAnim（类型 87）与 Blink（类型 108）各取一个语料属性：加载器经伪造流对象各读一次（0x44 / 0x2C 字节），块内容与规格给出的顺序一致，块大小与槽 6 的公式一致；用引擎构建的块继续驱动逐粒子函数，出生抽取与逐帧值逐位一致。用游戏自己的代码解析 EFX 属性可行 | 已确认（两个属性类型） |
| 随机数 | 3d09990 与 3d099e0 在 20,000 组随机 (salt, state, seed, key) 上与 `xs(xs(salt+state+key)+seed)` 一致；ScaleAnim 3,000 个粒子（含 |r| ≤ 1e-6 不消耗抽取的字段）与 Blink 3,000 个粒子的出生值逐位一致 | 已确认 |
| 逐粒子管线 3d05350 | 只给 ctx 挂 ScaleAnim、Blink 两个块，另补三个指针（`[g]` 设置单例、类型上下文 `[cs+0x10]→ctx`、`ctx+0x100`），整条管线连续运行 61 次无故障；ScaleAnim、Blink 结果与单独调用一致；手动给粒子速度后位置按 `pos += vel·n` 前进 | 已确认 |
| 发射器循环 3d08480 | 只做了静态评估，见下节 | 部分待确认 |

## 卡点

- `[g+0xeb2]`（LegacyMode2018）与 `[g+0xeb0]` 在新映射的镜像里不存在：`g = qword [d62f8f0]` 是 .bss 指针，为空。
  该对象（0xee0 字节）由构造函数 3cdf250 清零，启动时 7896afe / 7896b12 再从配置块 `+0x51d` / `+0x530` 复制。
  发行版的取值只能从运行中的游戏读到，本目录不读。
- 碰撞路径 3d64580 → 3d6d280 → 3cfe610 读静态 TLS（`gs:[0x58]` 与 `_tls_index` 0xda04358）。本进程未给 re4.exe 分配 TLS 块，
  带碰撞块运行前需要补一个 TLS 槽。
- 3d08480 需要完整的 E（属性列表 E+0x288、粒子数组 E+0x190、Spawn 块）和更新参数结构（`rdx+0x8/0x18/0x20/0x28/0x30`）。
  深度 3 内可达 711 个函数，导入全部可绑定，没有陷阱；需要处理的是 .bss 单例（`d62f9f0` 的 +0x3bb9 字节、`d62f8e0`、
  `d62fa38` PtLife 事件队列、`d6edb20..28` 一组全局浮点）和引擎分配器（3d3a6e0 → 39ed9b0 → 3a85cc0 / 3a85cd0、3acc670），
  分配器需要 physsim 那样的入口补丁。
