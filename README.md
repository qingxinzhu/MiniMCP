# MiniMCP

**把 Windows 上运行的 Unity 游戏，变成一个可通过 HTTP 访问的运行时桥。**

本仓库包含 **两套独立实现**：

| 版本 | 适用游戏后端 | BepInEx | 目录 | 产物 |
|---|---|---|---|---|
| **Mono 版**（本 README 主体）| Mono | 5.4.23.x | [`src/`](src/) | `MiniMCP.dll` |
| **IL2CPP 版** | **IL2CPP** | **6 (be.xxx)** | [`il2cpp/`](il2cpp/) | `MiniMCP.Il2Cpp.dll` |

> 怎么看你的游戏是哪种？看游戏根目录有没有 `GameAssembly.dll`：
>
> **有 → 用 IL2CPP 版**；没有（且 `<游戏名>_Data/Managed/` 里是一堆 dll）→ 用 Mono 版。

---

## Mono 版（下文）

**把 Windows 上运行的 Mono Unity 游戏，变成一个可通过 HTTP 访问的运行时桥。**
一个 **Windows x64 的 BepInEx 插件**：放好 `MiniMCP.dll` 就能让 AI / 脚本 / 调试器在本机或局域网直连活着的游戏进程，实时读取场景树、查改字段、调用方法 —— 不改游戏本体、不重启、不读档。

> 主要使用场景是**原生 Windows**。
> 在**兼容层与虚拟机**（Proton / Wine + Box64 / 各类 Windows 虚拟机与安卓模拟器）上**同样可以运行**，这部分属于**附加支持** —— 相关注意点见下方「兼容层说明」与 [`docs/PITFALLS.md`](docs/PITFALLS.md)。

> ⚠️ **本插件不做鉴权，也不加密通信**（纯明文 HTTP），默认监听 `0.0.0.0`。
> 同网段内**任何设备**都能读写你的游戏内存、调用任意方法。
> 请只在**本机**或**完全可信的网络**里使用；需要远程访问时请绑定 `127.0.0.1`，再走 SSH 隧道转发（见下方「安全」一节）。

---

## 为什么自写 HTTP 层（而不是直接用 HttpListener）

**主因**：本项目要的是一个**零外部依赖**的运行时桥 —— 一个 dll 丢进 `BepInEx/plugins/` 就能用，不需要额外跑 MCP server、不需要改游戏代码、不需要游戏自带任何调试接口。为此需要自己实现 HTTP 服务层，才能完全掌控路由、错误处理与生命周期。

**技术上的补充原因**：Mono 的 `HttpListener` 在**兼容层环境**（尤其 Wine + Box64 这类）下会直接撞墙：

| 尝试 | 结果 |
|---|---|
| `HttpListener` 绑 `*` 或 `+` | Mono 不支持通配 host 前缀，任何请求都回 `400 Bad Request (Invalid host)` |
| 绑具体 IP | TCP 能建连（`/dev/tcp` 成功），但**永远 0 字节响应**，连接堆在 `CLOSE_WAIT`，日志无任何 handler 记录 |
| 改同步 `GetContext()` | 同样无效 |

结论：**Mono 的 `HttpListener` 整套在 Wine + Box64 下不可用**，所以统一改为自写网络层。
（在**原生 Windows** 上 `HttpListener` 通常是可用的，但为了**一份代码到处能跑**，本项目不做平台分支。）

于是 MiniMCP 直接自写网络层：

```
TcpListener（同步 AcceptTcpClient） + 手写极简 HTTP 解析 + 主线程命令队列
```

Unity API 只能在主线程调用，所以所有请求被塞进 `ConcurrentQueue`，由插件的 `Update()` 每帧取出执行，请求线程用 `ManualResetEventSlim` 等结果（带超时）。

---

## 快速开始

### 1. 编译

```bash
# 需要 .NET SDK 8（或任何能编译 netstandard2.0 的 SDK）
cd src
dotnet build MiniMCP.csproj -c Release
# 产物：bin/Release/netstandard2.0/MiniMCP.dll
```

**不依赖游戏目录里的任何 dll** —— 所有引用都来自 NuGet（BepInEx.Core / BepInEx.PluginInfoProps / Newtonsoft.Json / UnityEngine.Modules）。

### 2. 部署

1. 给游戏装 **BepInEx 5.4.23.x（Windows x64 版）**：把 `winhttp.dll` + `BepInEx/` + `doorstop_config.ini` 放到游戏根目录
2. 把编译好的 `MiniMCP.dll` 放进 `BepInEx/plugins/`
3. **重启游戏**（插件只在启动时加载）

> **原生 Windows** 上到此即可。
> 若在**兼容层 / 虚拟机**里运行（Proton / Wine + Box64 等），还需要让 DLL 劫持生效：
> `WINEDLLOVERRIDES=winhttp=n,b`（部分环境还需选择正确的 Windows 版本）。

### 3. 验证

```bash
curl http://<DEVICE_IP>:18081/health

# 期望
{"ok":true,"plugin":"MiniMCP","unity":"6000.3.4f1","scene":"..."}
```

配置由插件首次运行时自动生成：`BepInEx/config/com.operit.minimcp.cfg`（示例见 `config/com.operit.minimcp.cfg.example`）。

> ⚠️ BepInEx 5 的 cfg 是**裸值格式**：写 `BindAddress = 0.0.0.0`，**不要**加引号（写 `"0.0.0.0"` 会把引号当成值的一部分）。

---

## 端点

| 端点 | 方法 | 说明 |
|---|---|---|
| `/health` | GET | 存活、Unity 版本、当前场景 |
| `/tree` | GET | 场景对象树（`depth` / `max` 限流）|
| `/find` | GET | 按对象名 `name=` / 组件名 `component=` 搜索，返回 instanceId |
| `/inspect` | GET | 查看对象成员（字段 + 属性 + 当前值）|
| `/fields` | GET | 列成员清单（含 `<X>k__BackingField` 与属性）|
| `/getpath` | GET | **按路径读值**，如 `save.Charas[1].Attr.NowLike` |
| `/setpath` | POST | **按路径写值**（支持 int / string / bool / 数组 / 枚举名 / 对象列表）|
| `/scan` | GET | 按数值反查字段（找未知字段用）|
| `/set` | POST | 按 `id + component + member` 写值 |
| `/call` | POST | 按 `id + component + method` 调方法 |

请求示例：

```bash
# 读
curl -g "http://<IP>:18081/getpath?id=123456&path=save.TotalCash"

# 写（枚举要写整个字段 + 字符串名；列表要整体写）
curl -X POST "http://<IP>:18081/setpath" \
     -H 'Content-Type: application/json' \
     -d '{"id":123456,"path":"_charaManager._allChara[1].MaidData.Rarity","value":"SSR"}'
```

> 路径里含 `[` `]` 时，`curl` 需要加 `-g` 关闭通配，或对 URL 做百分号编码。

完整参数说明见 [`docs/ENDPOINTS.md`](docs/ENDPOINTS.md)，坑与技巧见 [`docs/PITFALLS.md`](docs/PITFALLS.md)。

---

## 适配范围

| 游戏类型 | 可行性 |
|---|---|
| **Mono Unity**（Windows / Proton / Wine+Box64 / Android 原生）| ✅ 直接可用，零改动 |
| IL2CPP Unity | 需 BepInEx 6 + Il2CppInterop 生成代理程序集；可复用约 70%，反射层（`MiniMcp.Paths.cs`）需重写；先用 Il2CppDumper / Cpp2IL 恢复符号 |
| .NET / MonoGame / XNA | 不用 BepInEx。CoreCLR 走 CLRMD，Mono 走 Mono.Debugger.Soft；本插件的 TCP/HTTP 层与命令队列可复用 |
| WebGL（IL→wasm）、CoreCLR 实验后端 | ❌ 不可行 |

---

## 兼容层与虚拟机（附加支持）

原生 Windows 是主要场景；以下环境**同样可用**，但有几件事要额外注意：

| 环境 | 说明 |
|---|---|
| **Proton / Wine + Box64** | 已实测可用。需 `WINEDLLOVERRIDES=winhttp=n,b` 让 DLL 劫持生效 |
| **Windows 虚拟机** | 等同于原生 Windows，按上面步骤即可 |
| **安卓上的 Windows 模拟器 / 容器** | 已实测可用（同一条 Wine + Box64 路线），注意模拟器自身的性能与后台策略 |

相对原生 Windows 的**关键差异**：

1. **文件路径**：游戏进程看到的是 Windows 风格路径（`/sdcard/x` 会被解析成 `E:\sdcard\x`）。如果你后续给插件加了需要读文件的配置项，**不要猜盘符** —— 用**插件目录相对路径**最稳，或用 `Assembly.Location` 反推插件目录兜底查找。
2. **进程被挂起**：部分平台在游戏切到后台时会 `SIGSTOP` 整个 Wine 进程组 —— 此时端口仍在 `LISTEN`，但**完全不响应**、连接堆在 `CLOSE_WAIT`；切回前台即恢复（见 `docs/PITFALLS.md`）。

---

## 安全

本插件**没有鉴权、没有加密**。端口只随游戏进程存在（关掉游戏，端口自然消失）。默认配置下，同网段任何人都能用一条 `curl` 读写游戏内存、调用任意方法。

建议按需选一条：

1. **只绑本机**（最稳）：配置里改成

   ```ini
   BindAddress = 127.0.0.1
   ```

   需要远程访问时走 SSH 隧道转发：

   ```bash
   ssh -L 18081:127.0.0.1:18081 <user>@<host>
   # 然后本地 curl http://127.0.0.1:18081/health
   ```

2. **用完即关**：不玩就退出游戏，端口不会常驻。
3. **网段隔离**：只放在完全可信的网络里，或在路由器侧隔离设备。

> 如果你需要鉴权或 TLS：`MiniMcp.cs` 里的 `HandleClient` 是唯一的请求入口，在那一层加 Token 校验或 `SslStream` 包装即可，端点与路由逻辑完全不用动。

---

## 已知坑（值得单独读）

- **`/scan` 窄类型溢出**：早期版本把目标值强转成字段类型（如 `(byte)999999999` → 255），导致满屏误报；修法是整型先转 `double` 再比较。
- **`/scan` 不递归纯 C# 数据对象**：只深挖 Unity 类型字段，普通对象内部的值扫不到，请用 `/getpath` 直读。
- **instanceId 会变**：重进存档后组件的 id 可能变化，改值前先 `/find` 一次。
- **换 dll 必须重启游戏进程**（插件只在启动时加载）。
- **游戏切后台可能被 `SIGSTOP`**：端口仍显示 `LISTEN` 但完全不响应、连接堆 `CLOSE_WAIT`；切回前台即恢复（先看 `ps` 里的 `T` 状态，别误判插件挂了）。

---

## 免责声明

本项目仅供**学习、研究与单机游戏的本地调试**使用。
请在**你拥有或已获授权**的环境中使用；**不要**用于线上游戏、多人竞技或任何侵犯他人权益的场景。
使用本工具修改游戏数据可能违反游戏的用户协议，由此产生的一切后果由使用者自行承担。

---

## 许可

[MIT](LICENSE)

本项目以 NuGet 包形式引用 BepInEx（LGPL-2.1）与 Newtonsoft.Json（MIT），未修改其源码。