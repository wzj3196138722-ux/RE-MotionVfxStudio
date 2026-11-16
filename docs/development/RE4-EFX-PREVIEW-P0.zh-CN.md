# RE4 原生预览 P0：使用与实现边界

本文保留 P0 的历史范围与验证。当前入口已扩展到 P1，最新使用方式和边界见 [RE4-EFX-PREVIEW-P1.zh-CN.md](RE4-EFX-PREVIEW-P1.zh-CN.md)。

入口是 `VfxStudio.Re4Preview`。它是独立窗口，使用 Re4Native 的原生发射器，当前以点显示 CPU Billboard3D 粒子。游戏文件只读，不连接游戏进程。

## 启动

在仓库根目录执行（需要仓库的 .NET SDK、64 位 Python 和 numpy / pefile / capstone）：

```powershell
dotnet run --project VfxStudio.Re4Preview -- --fixture bb2
# 另一组官方场景
dotnet run --project VfxStudio.Re4Preview -- --fixture bb
# 使用自选文件和 root entry 的十六进制字节偏移（不是 entry 下标）
dotnet run --project VfxStudio.Re4Preview -- --efx 'E:\path\effect.efx.3539837' --offset 2E29
```

`--exe` 指定本文设计锁定版本的 re4.exe；也可设置 `RE4_EXE`。`--python` 指定 Python 程序；从其他目录启动时用 `--repo` 指定仓库根目录。内置场景路径沿用 `exp_emitter.py`，自选资源可用 `--efx`。

窗口支持播放、暂停、向前步进、时间轴回拖、重新加载；右键旋转、中键平移、滚轮缩放。检查器显示 entry、粒子槽、编号、位置、年龄、生命淡出、完整 0x140 字节记录，以及任意偏移的 u32 / float 值。读取得到的无效坐标不会提交给 GPU。

加 `--test-faults` 后显示两个故障测试按钮：在引擎 seeder 内制造真实访问违例，或让辅助进程立即退出。默认不开启测试命令。故障信息包含 NativeFault 的 entry、RVA 与 exe 回溯；无法确定 entry 的突然退出仅报告进程退出。辅助进程由客户端重启并重新加载一次，窗口不再追加第二次重载；时间轴停在故障前已显示的 tick，下一次请求从恢复后的加载状态重跑到该 tick。真实播放故障后同样暂停，保留上一幅画面，不自动重复故障 tick；用户可继续播放、回到早期 tick 或重新加载。命令超时或应答损坏时，客户端结束该辅助进程，下一次请求重启它，不再沿用已取消读取的管道。推进过程中的非原生异常把辅助进程回卷到加载后快照并报告 `RecoveredError`（非致命），回卷失败则按致命处理。辅助进程与 LiveEditor 后台都加入 KILL_ON_JOB_CLOSE 作业对象，预览程序崩溃或被结束时一并结束。P0 尚未实现按 entry 自动隔离、二分定位或备用进程。

## 当前结构

- `VfxStudio.Ui`：从 App 搬出的 ImGuiController、OrbitCamera、ViewportGridRenderer、StudioTheme。保留原命名空间以减少 App 改动；App 已改为引用共享项目。
- `VfxStudio.Re4Render`：辅助进程生命周期、协议、共享内存读取、记录解码、点绘制。不调用 EfxSimulator。
- `VfxStudio.Re4Preview`：独立窗口、固定 60 Hz 时钟、异步请求、时间轴、相机、检查器。
- `tools/Re4Native/re4host.py`：原生服务、共享内存写入、加载后快照与恢复。
- `tools/Re4PreviewTests`：用独立原有实验作基准，通过实际 C# 客户端验证共享内存与重跑。

一个请求在途，三个槽轮换。辅助进程完成复制后才应答；C# 在下一次请求前将槽复制到独立托管帧，GPU 只读取该帧生成的点缓冲。原生计算在后台任务运行，UI 不等待每 tick 的原生调用。P0 没有实现三阶段解码流水线或持久映射 GPU 实例缓冲，后者属于 P1。

加载后快照覆盖 PE 标记为可写的节、bump arena 已用部分、`ExeImage.alloc` 持有的 ctypes 缓冲（包括 E、更新结构、TLS 垫片等）、bump cursor 和资源缓存。恢复时清零新使用的 arena 尾部、释放快照后新增的 ctypes 保留项，并恢复资源缓存。只允许同一原生线程上恢复。P0 未启用带 Python 可变闭包状态的碰撞世界，不能把本次确定性结论推广到碰撞或子 Action。

## 验证

```powershell
dotnet run --project tools/Re4PreviewTests
# 自动运行窗口到 tick 45，截图并退出
dotnet run --project VfxStudio.Re4Preview -- --smoke --capture test-output/re4-preview/smoke.png
```

测试包含 bb / bb2 × 固定 / 可变 dt，每组 120 tick，对比原有 `exp_emitter.py` 独立进程的粒子数量、槽位、编号、flags、原始位置、解码后的世界位置、年龄和生命淡出；另用合成帧检查 UV 序列号 4、13（`[p+0]` 位 12 置位）不会让解码位置加上 +0x34 偏移；再在同一辅助进程内完整重跑两次，对比每 tick 的完整粒子数组 SHA-256。另有去掉 FixRandomGenerator、全局 xorshift128 以 1/2/3/4 播种的场景，检查会变化的全局随机状态也能恢复。故障验证分别检查真实 NativeFault 与突然退出后重新运行到 tick 45 的摘要一致；解码器检查损坏帧拒绝。

结果写入 `test-output/re4-preview/results.json`（本地产物，不提交）。该文件记录加载时间、快照大小、恢复时间、原生 tick 耗时和 C# 读取/解码耗时。本机首轮四组结果：加载约 4.7–5.0 秒，快照约 84 MB，恢复约 5–8 ms；这些是小粒子场景，不能作为一万粒子性能结论。这里的加载指新进程的首次加载（映射镜像、安装运行时）；同一辅助进程内的后续加载约 30 ms，见 [P2 文档](RE4-EFX-PREVIEW-P2.zh-CN.md)。

预览项目与原有 App 均已编译通过；窗口 tick 45 的点显示已通过程序截图检查。尚未通过手动拖动、故障按钮点击验收，亦未做游戏画面对照。

## 与完整设计的差异

P0 只支持 TypeBillboard3D（属性类型 23）的原始数据路径、静止单位阵发射器，以及现有实验的 .uvs 合成替身。自选 entry 的模拟仍受 Re4Native 桩支持范围限制。MaxParticles 上限 10000；E 的放置缓冲按容量保守分配，不构成复杂属性内存需求的通用证明。

帧协议包含原始粒子数组、附加数据、四个 E 标志字和发射器矩阵；属性块、条带点、实际 ctx 类型号、n/N 仍为后续扩展。位置变换采用设计 §4.3 的折中，未完成 ParentOptions 参考矩阵。当前没有贴图面片、真实 UV 表、颜色合成、编辑联动和 GPU 类型。

协议细节见 [PROTOCOL.md](../../tools/Re4Native/PROTOCOL.md)，总体设计见 [RE4-EFX-RENDER-HOST.zh-CN.md](RE4-EFX-RENDER-HOST.zh-CN.md)。
