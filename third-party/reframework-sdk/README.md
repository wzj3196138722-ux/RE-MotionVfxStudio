# REFramework build headers and Lua source

This is the minimal vendored SDK used to build MotionVfx Live's native input component. It contains REFramework plugin API **1.15.0** headers and **Lua 5.4.3** source, copied byte-for-byte from the reference checkout used for the local 0.8.5 build. It contains no REFramework injector or game files. The reference checkout did not retain a Git commit identifier; we do not claim a particular upstream commit. `SHA256.json` records the exact included source and license bytes, and the release workflow verifies them before compiling. No floating SDK download is performed.

- REFramework by praydog: <https://github.com/praydog/REFramework>; MIT license in `LICENSE-REFramework.txt`.
- Lua by Lua.org / PUC-Rio: <https://www.lua.org/versions.html#5.4>; license in `LICENSE-Lua.txt` and `dependencies/lua/src/lua.h`.

Keep these copyright and permission notices when redistributing or modifying this subset. `dependencies/lua/README` is the original version notice; its referenced upstream documentation is not needed by the native build and is not included here.

此目录只包含构建原生输入组件所需的 API 1.15.0 头文件和 Lua 5.4.3 源码，不包含注入器或游戏文件。源码来自本地 0.8.5 使用的参考材料，未保留上游提交编号，因此通过清单固定具体文件，不能将其描述为某个已确认的上游提交。感谢 REFramework 和 Lua 的原作者。
