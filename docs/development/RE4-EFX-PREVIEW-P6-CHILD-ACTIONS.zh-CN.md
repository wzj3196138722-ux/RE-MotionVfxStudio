# RE4 离线预览：子 Action（entry 依赖）

PtLife 在粒子生命阶段触发 Action，Action 里的 PlayEmitter 播放一份内嵌的 EFX（一组 entry）。预览现在会运行这条链：触发时在对应位置启动内嵌 entry，画出它们的粒子。以前这部分只在节点图里能看到，画面上没有。

## 语料里的结构

统计 RE4 全部 3946 个文件：

- 每个 Action 只有一个属性：PlayEmitter（475 个）或 PlayEfx（61 个，引用外部文件，预览不运行）。
- 一个 Action 可以被多个触发器引用：97 个 Action 有 2 到 5 个以上的触发器（例如两个 entry 的 PtLife 都在出生时触发同一个 Action）。一个 entry 也可以有两个触发器（6 个，PtLife 和 PtColliderAction 各指向不同的 Action）。
- 内嵌文件里没有 Action，所以 RE4 只有一层嵌套。
- 带触发器的 entry：NoDraw 418、Billboard3D 225、Mesh 120、RibbonFollow 22、Polygon 4。
- PlayEmitter 内嵌 1 到 10 个以上的 entry，类型以 Mesh（870）、Billboard3D（695）、RibbonLength（226）、NoDraw（128）为主。

节点图本来就是这样表示的：触发器所在的行各有一个输出点连到 Action，多个触发器可以连到同一个 Action 的输入；Action 的 PlayEmitter 行再连到它的每个内嵌 entry，和 Root 连主 entry 的方式一样。

## 运行方式

引擎的做法（RUNTIME §5）：每个 PtLife 属性有一个 Action 实例池（2 × MaxParticles）。触发时取一个槽，标记“待启动”，写入粒子当时的 4×4 变换。PtLife 的发射器级步骤 3d3e0a0 再启动、更新、回收这些实例。跟随位（Flags 位 2–4）置位时，引擎每帧从粒子重建实例矩阵；粒子死后矩阵冻结。

预览保留引擎自己的触发和实例池，只把 3d3e0a0 换成空函数，由 re4host 接管执行：

- 每个 tick，父 entry 更新之后，检查它的 PtLife 实例池。标记“待启动”的槽启动（或重启）这个槽的子发射器，然后清掉该标记，和执行器做的一样。
- 运行中的槽每帧读取槽里的矩阵，作为子发射器的世界矩阵，所以跟随与冻结都由引擎决定。
- 子发射器的每个 entry 都已出过最后一批粒子（粒子 +4 的 0x20000 位），并且没有存活粒子时，这个槽被释放（标志清零），引擎之后可以重新分配它。永远循环的子特效会一直运行，直到引擎因池满覆盖最旧的槽。
- 子发射器在一个槽第一次启动时才建造，之后重启时清空内存、重新原地构造（`Emitter.reset`），内存不增长。读档回退时这些发射器随快照一起丢弃，重放结果与第一次完全相同。
- 每个“Action × 内嵌 entry”输出一张表，里面是所有运行中实例的粒子，每个实例占一段。粒子自带参考矩阵（+0x100），所以不同实例的粒子在同一张表里位置仍然正确。

NoDraw entry 本身不画，但它们多数就是用来带触发器的。现在带可运行子 Action 的 NoDraw entry 会被模拟（不画），也可以选作编辑对象，所以只有 NoDraw 主 entry 的文件（不少命中特效是这样）现在也能打开和预览。

## 界面与 MCP

- 主面板显示“子 Action 运行 x / y 个内嵌 entry”，悬停列出每个内嵌 entry（`[A动作.序号]`）和未运行的原因。
- 节点图里内嵌 entry 的提示改为“触发时预览会运行它”，NoDraw 节点注明“不绘制，用来触发子 Action”。
- MCP `status.preview.entries` 对 NoDraw 载体给出 `logicOnly` 和 `firesActions`；新增 `childActionEntries`（action、child、label、类型、是否运行、表序号、未运行原因）。`frame` 的 entries 里内嵌 entry 带 `label`。

## 限制

- 只运行 PtLife 触发的子 Action。PtColliderAction（命中触发，216 个）的实例由引擎自己的执行器处理（把它换掉会让 deadbody 的另外两个 entry 在 3d62ce8 出错），子特效暂不显示。
- 内嵌 entry 只运行 Billboard3D、Polygon、Billboard2D。Mesh 和各种条带暂不运行，在悬停列表里标出原因。
- 每个触发器最多运行 32 个实例槽（引擎的池是 2 × MaxParticles）；超出的槽不显示。
- 父发射器停止时释放子实例（PtLife Flags 位 0）没有模拟，子特效会继续跑完。
- 内嵌 entry 还不能在窗口里编辑或选中。
- 一次最多 64 张表（主 entry 加内嵌 entry）。

## 验证

- `tools/Re4MultiEntryTests` 第 11 节（ground_walk_0000，7 个 NoDraw 载体在 Keep 阶段触发 Action 0–2，每个 Action 两个 Billboard3D）：载体不画但运行；6 张内嵌表；第 120 帧子 entry 有粒子；每个子粒子的参考矩阵平移等于载体粒子的平移（−62.0038，来自文件里 Transform3D 的 y 偏移）；从第 30 帧回退后重放到第 120 帧，结果逐字节相同。原有 30 项检查不变（deadbody 仍只排除第 58 个 entry）。
- 原生端单独验证：同一个子发射器重置后重放 90 帧，粒子数组哈希相同，arena 增长 0。
- 窗口：csa048 hit_0001 只有两个 NoDraw 主 entry，以前打不开；现在两者在出生时触发，8 个内嵌 Billboard3D 在第 2–15 帧发出烟雾后消失，截图可见。
