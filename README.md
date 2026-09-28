# MiniMCP

**一个「类 MCP」的运行时服务：把它塞进游戏进程，就能从外部读取和修改游戏的内部状态。**

不是 mod，不是内存外挂，也不是改存档 —— 它是一个**装进进程里的 HTTP 桥**：列对象、查字段、改数值、调方法，全部实时生效，不用重启游戏。

```
   你的 AI / 脚本 / 调试器
            │  HTTP（裸调）或 JSON-RPC / MCP（经桥转发）
            ▼
   ┌─────────────────────────┐
   │   MiniMCP（进程内）       │
   │   HTTP 服务 + 反射引擎     │
   └────────────┬────────────┘
                │ 反射（读/写真实内存对象）
                ▼
        游戏运行时（场景树、对象、字段）
```

---

## 先说清楚：它是「类 MCP」，不是标准 MCP

这是本项目最容易误解的地方，所以放在最前面。

| | 标准 MCP 服务器 | **MiniMCP** |
|---|---|---|
| 线上协议 | JSON-RPC 2.0 | **裸 HTTP + JSON** |
| 发现机制 | `initialize` / `tools/list` | `GET /` 返回端点列表 |
| 调用方式 | `tools/call` | 直接请求对应路径 |
| 传输 | stdio / SSE / streamable HTTP | **TCP HTTP** |
| 客户端兼容 | MCP 客户端直接挂 | **需要一层桥（本仓库已提供）** |

**为什么不做成真 MCP？**

1. **依赖预算**：插件运行在游戏的 .NET 运行时里（BepInEx 5 是 .NET Framework，BepInEx 6 是 .NET 6）。引 MCP SDK 会带来额外依赖与版本冲突风险；一个 dll 丢进去就能跑才是这个项目的目标。
2. **零客户端要求**：裸 HTTP 意味着 `curl`、Python、按键精灵、Postman、AHK 都能直接调，不需要任何 MCP 客户端。
3. **能力等价**：它在能力模型上和 MCP 一样是「工具调用」，只是线格式不同。

**那要和 AI 对接怎么办？** 仓库里附了一个零依赖的桥（[`tools/mcp_bridge.py`](tools/mcp_bridge.py)），它把 HTTP 端点包成**标准 MCP 服务器**（stdio / JSON-RPC 2.0），Claude Desktop / Cursor / Operit 这类客户端可以直接挂载。详见下方「怎么让 AI 接入」。

---

## 三种装配形态

MiniMCP 内核（HTTP 服务 + 反射引擎）只有一套，但按「宿主是什么」分三种装法：

| 形态 | 宿主 | 怎么进去 | 适用游戏 | 目录 |
|---|---|---|---|---|
| **A. Mono 插件** | Unity + **Mono** 后端 | BepInEx **5** 注入 | 空洞骑士 / 茶杯头 / 环世界 | [`src/`](src/) |
| **B. IL2CPP 插件** | Unity + **IL2CPP** 后端 | BepInEx **6** 注入 | 米塔 / R.E.P.O. / 吸血鬼幸存者 | [`il2cpp/`](il2cpp/) |
| **C. 内嵌形态** | 你自己的 **.NET** 程序 | 源码直接编进去 | MonoGame / XNA / 自研 | _(整理中)_ |

### 怎么判断你的游戏该用哪个

看游戏根目录：

```
有 GameAssembly.dll
    → Unity + IL2CPP  → 用 B 形态（il2cpp/）

无 GameAssembly.dll，但有 <游戏名>_Data/Managed/ 且里面一堆 dll
    → Unity + Mono    → 用 A 形态（src/）

两个都没有（根目录只有 .exe + 若干 dll，没有 Data 目录）
    → 纯 .NET 游戏（MonoGame / XNA / FNA）
    → BepInEx 挂不上（它不是 Unity）
    → 用 C 形态，或参见 Stardew Valley 的 SMAPI、Terraria 的 tModLoader 等同类思路
```

---

## 能力总览

不管哪个形态，能力都是这几类：

### 看得见

| 能力 | 说明 |
|---|---|
| **存活体检** | 插件版本 / Unity 版本 / 当前场景 / 游戏版本 |
| **对象列表** | 场景里所有对象（名称 / 类型 / 坐标 / 子对象数 / 激活状态）|
| **对象树** | 缩进文本形式打印层级结构，`*` 激活 `.` 未激活 |
| **字段查看** | 一个对象的全部字段名 + 当前值 |
| **方法列表** | 可调用方法的方法名 / 参数个数 / 返回类型 |
| **游戏统计** | 对象数 / 帧号 / 运行时间 / 时间倍率 / FPS |

### 改得动

| 能力 | 说明 |
|---|---|
| **读字段** | 按对象名 + 字段名读值 |
| **写字段** | 改血量、金币、分数、状态等一切可写字段 |
| **调方法** | 调用对象上的方法（IL2CPP 版限 0 参；MonoGame 版可带参）|
| **改坐标** | 绝对赋值或相对位移 |
| **开关对象** | 等价于 `SetActive` |
| **改游戏速度** | 时间倍率（慢动作 / 加速）|
| **扫描反查** | 知道数值但不知道字段名时，按值搜字段（Mono 版）|

### 拿得到

| 能力 | 说明 |
|---|---|
| **原始请求兑底** | 任何未封装的能力都可以直接打端点，不丢任何东西 |

---

## 快速开始

### 形态 A：Mono 插件

```bash
cd src
dotnet build MiniMCP.csproj -c Release
# 产物：bin/Release/netstandard2.0/MiniMCP.dll
```

1. 给游戏装 **BepInEx 5.4.23.x（Windows x64）**
2. `MiniMCP.dll` 丢进 `BepInEx/plugins/`
3. **重启游戏**（插件只在启动时加载）
4. 验证：

```bash
curl http://127.0.0.1:18081/health
```

配置自动生成在 `BepInEx/config/com.operit.minimcp.cfg`（示例见 [`config/`](config/)）。

> ⚠️ BepInEx 5 的 cfg 是**裸值格式**：写 `BindAddress = 127.0.0.1`，**不要**加引号。

### 形态 B：IL2CPP 插件

见 **[`il2cpp/README.md`](il2cpp/README.md)**（完整步骤含 interop 程序集生成、csproj 改法、风险提示）。

关键差异：

| | Mono 版 | IL2CPP 版 |
|---|---|---|
| BepInEx | 5.4.23.x | **6（be.xxx）** |
| 目标框架 | netstandard2.0 | **net6.0** |
| 反射方式 | .NET 反射 | **il2cpp native API + Il2CppInterop** |
| 编译依赖 | 全部 NuGet | **必须引用游戏生成的 `BepInEx/interop/`** |
| 端口 | 18081（可配）| **18081（固定）** |

### 形态 C：内嵌（自己的 .NET 程序）

把 HTTP 层 + 反射层当成普通 C# 类编进你的程序，在游戏主循环里调一下 `Pump()` 就行。不需要任何注入工具。

---

## 怎么让 AI 接入

这节是本项目存在的意义：**让 AI 能自己探索一个活着的游戏**。

共有四种接入方式，从糙到精：

### 方式 1：让 AI 直接发 HTTP（零准备）

AI 只要会执行命令，就能用：

```bash
curl http://127.0.0.1:18081/health
curl http://127.0.0.1:18081/objects?name=Player
curl -X POST http://127.0.0.1:18081/set \
     -H 'Content-Type: application/json' \
     -d '{"name":"Player","field":"HP","value":9999}'
```

适合：能跑 shell 的 agent（包括直接在游戏机上操作的 AI）。

### 方式 2：经 MCP 桥接入（**推荐**）

仓库自带 [`tools/mcp_bridge.py`](tools/mcp_bridge.py)：**零依赖**（纯标准库），把 HTTP 端点包成**标准 MCP 服务器**。

它自动做的事：

- 握手 `initialize` / 列工具 `tools/list` / 调用 `tools/call`
- **自适应三种后端**：Mono 版（路径风格端点）、IL2CPP 版、MonoGame 版（对象名风格）
- 把 20+ 个端点归并成 **13 个稳定工具**，AI 不用背端点差异
- 保留 `raw_request` 兑底，什么都不会丢

#### 启起来

```bash
# 手动跑（调试用）
python3 tools/mcp_bridge.py --url http://127.0.0.1:18081

# 或用环境变量
MINIMCP_URL=http://127.0.0.1:18081 python3 tools/mcp_bridge.py
```

#### 挂到 MCP 客户端

**Claude Desktop**（`claude_desktop_config.json`）：

```json
{
  "mcpServers": {
    "minimcp": {
      "command": "python3",
      "args": ["/path/to/MiniMCP/tools/mcp_bridge.py", "--url", "http://127.0.0.1:18081"]
    }
  }
}
```

**Cursor / 其他支持 MCP 的客户端**：同样填 `command` + `args` 就行。

**远程游戏机**（游戏在另一台电脑上）：先把端口用隧道拓到本地，再把桥指向本地端口：

```bash
# 例：SSH 隧道
ssh -N -L 18081:127.0.0.1:18081 user@game-host

# 然后桥照常连本地
python3 tools/mcp_bridge.py --url http://127.0.0.1:18081
```

#### 桥暴露的 13 个工具

| 工具 | 作用 | 对应端点 |
|---|---|---|
| `game_health` | 存活 + 版本 + 场景 | `/health` |
| `game_endpoints` | 列该版本实际支持的端点 | `/` |
| `list_objects` | 列/搜对象 | `/objects`（Mono 版退化为 `/find`）|
| `object_tree` | 对象层级树 | `/tree` |
| `inspect_object` | 看字段（含值）/ 看方法 | `/fields` `/listfields` |
| `get_field` | 读字段 | `/get` |
| `set_field` | 写字段 | `/set` |
| `call_method` | 调方法（可带参）| `/call` |
| `move_object` | 改坐标 | `/move` |
| `set_active` | 开关对象 | `/setactive` |
| `set_timescale` | 游戏速度 | `/timescale` |
| `scan_value` | 按值搜字段 | `/scan`（仅 Mono 版）|
| `raw_request` | 原样发请求（兑底）| 任意 |

#### 建议的 AI 使用流程

```
1. game_health        → 确认连上了没
2. game_endpoints     → 看看这个版本有什么能调
3. list_objects / object_tree
                      → 探索场景里有什么
4. inspect_object     → 看目标对象的字段和方法（这步能拿到字段名和当前值）
5. get_field / set_field / call_method
                      → 读、改、调
6. raw_request        → 碰到未封装的功能时直接打端点
```

### 方式 3：Function Calling / 工具声明

不想跑桥？把下面这个 schema 贴进支持 function calling 的模型即可（以 IL2CPP 版为例）：

```json
[
  {
    "name": "game_get_field",
    "description": "读取游戏中某个对象的字段值",
    "parameters": {
      "type": "object",
      "properties": {
        "name": {"type": "string", "description": "对象名，如 Player"},
        "field": {"type": "string", "description": "字段名，如 HP"}
      },
      "required": ["name", "field"]
    }
  },
  {
    "name": "game_set_field",
    "description": "修改游戏中某个对象的字段值",
    "parameters": {
      "type": "object",
      "properties": {
        "name": {"type": "string"},
        "field": {"type": "string"},
        "value": {"description": "新值"}
      },
      "required": ["name", "field", "value"]
    }
  }
]
```

AI 决定调用后，把参数拼成对应的 HTTP 请求即可（`POST /set`）。

### 方式 4：封成一行命令

放到 `~/.local/bin/mcp`：

```bash
#!/bin/sh
BASE="${MINIMCP_URL:-http://127.0.0.1:18081}"
case "$1" in
  get) curl -s "$BASE/get" -X POST -H 'Content-Type: application/json' \
            -d "{\"name\":\"$2\",\"field\":\"$3\"}" ;;
  tree) curl -s "$BASE/tree?name=$2&depth=${3:-2}" ;;
  stats) curl -s "$BASE/stats" ;;
  *) echo "usage: mcp {get|tree|stats} ..." ;;
esac
```

然后 AI 只需要会跑 `mcp get Player HP` 这种形式的命令。

---

## 端点参考

两个版本的端点集合不同（历史原因），完整清单见 **[`docs/ENDPOINTS.md`](docs/ENDPOINTS.md)**。

### Mono 版（形态 A）

路径风格，擅长深路径寻址与反查：

| 端点 | 说明 |
|---|---|
| `GET /health` | 存活 / Unity 版本 / 场景 |
| `GET /tree?depth=&max=` | 场景对象树 |
| `GET /find?name=&component=` | 搜对象，返回 instanceId |
| `GET /inspect?name=` | 对象成员（字段 + 属性 + 值）|
| `GET /fields?name=` | 成员清单 |
| `GET /getpath?id=&path=` | **按路径读值**，如 `save.Charas[1].Attr.NowLike` |
| `POST /setpath` | **按路径写值** |
| `GET /scan?value=` | 按数值反查字段 |
| `POST /set` | 按 id + 组件 + 成员写值 |
| `POST /call` | 按 id + 组件 + 方法调用 |

### IL2CPP 版（形态 B）

对象名风格，所有响应统一 `{"ok": ...}`：

| 端点 | 说明 |
|---|---|
| `GET /health` `/ping` `/log` | 体检 / 心跳 / 日志 |
| `GET /scene` `/stats` | 场景 / 统计（对象数 / 帧 / FPS / 时间倍率）|
| `GET /objects?name=` | 搜对象 |
| `GET /tree?name=&depth=` | 对象树 |
| `GET /fields?name=` | 字段（仅游戏脚本类 + 值类型）|
| `GET /player` | 玩家坐标与物理状态 |
| `POST /get` `/set` `/listfields` | 读字段 / 写字段 / 列组件 |
| `POST /call` | 调方法（**仅 0 参**）|
| `POST /move` `/setactive` `/timescale` | 坐标 / 开关 / 时间倍率 |
| `POST /noclip` `/ground` | 穿墙 / 落地复位 |

---

## 安全

**本插件没有鉴权、没有加密。** 端口只随游戏进程存在（关掉游戏端口就消失）。

同网段内**任何设备**都能读写游戏内存、调用任意方法。请按需选一条：

1. **只绑本机**（最稳）：配置成 `BindAddress = 127.0.0.1`，要远程就拓 SSH 隧道：
   ```bash
   ssh -L 18081:127.0.0.1:18081 user@host
   ```
2. **用完就关**：不玩就退游戏。
3. **网段隔离**：只放在完全可信的网络里。

> 需要鉴权或 TLS：`HandleClient` 是唯一请求入口，在那一层加 token 校验或 `SslStream` 包装即可，路由逻辑不用动。

---

## 兼容层与虚拟机（附加支持）

主要场景是 **原生 Windows**；以下环境同样可用，但有几件事要注意：

| 环境 | 说明 |
|---|---|
| **Proton / Wine + Box64** | 已实测可用，需 `WINEDLLOVERRIDES=winhttp=n,b` |
| **Windows 虚拟机** | 等同原生 Windows |
| **Android 上的 Windows 模拟器 / 容器** | 已实测可用（同 Wine + Box64 路线）|

关键差异：

1. **文件路径**：游戏进程看到的是 Windows 风格路径（`/sdcard/x` → `E:\sdcard\x`）。配置里别猜盘符，优先用插件目录相对路径。
2. **进程可能被 `SIGSTOP`**：切后台时端口仍 `LISTEN` 但不响应、连接堆在 `CLOSE_WAIT`，切回前台即恢复。

---

## 已知坑

- **换 dll 必须重启游戏**（插件只在启动时加载，无热重载）。
- **`instanceId` 会变**：重进存档后可能变化，改值前先 `/find`。
- **IL2CPP：字段反射会崩游戏** —— 对 `Transform` 等 Unity 内置类做 native 字段反射会直接送走进程，且 try/catch 拦不住。已内置防护（跳过内置类、只读值类型），详见 [`docs/IL2CPP-NOTES.md`](docs/IL2CPP-NOTES.md)。
- **IL2CPP：`/call` 只放行 0 参方法** —— `il2cpp_runtime_invoke` 传错参数同样崩，所以主动限制。
- **Mono 版 `/scan` 窄类型溢出**：早期版本把目标值强转成字段类型导致误报，已修为先转 `double` 再比。
- **Mono 版 `/scan` 不递归纯 C# 对象**：普通对象内部的值扫不到，请用 `/getpath` 直读。

更多：**[`docs/PITFALLS.md`](docs/PITFALLS.md)**（Mono 版）、**[`docs/IL2CPP-NOTES.md`](docs/IL2CPP-NOTES.md)**（IL2CPP 版）。

---

## 适配范围

| 游戏类型 | 可行性 |
|---|---|
| **Mono Unity**（Windows / Proton / Wine+Box64 / Android 原生）| ✓ 直接可用 |
| **IL2CPP Unity** | ✓ 用 `il2cpp/`，需先让 BepInEx 6 生成 interop 程序集 |
| **.NET / MonoGame / XNA / FNA** | △ 不用 BepInEx。把桥内嵌进程序（形态 C），或参考 SMAPI / tModLoader 做法 |
| WebGL（IL→wasm）、CoreCLR 实验后端 | ✕ 不可行 |

---

## 仓库结构

```
MiniMCP/
├─ src/                形态 A：Mono 插件（BepInEx 5）
├─ il2cpp/             形态 B：IL2CPP 插件（BepInEx 6）
├─ tools/
│  └─ mcp_bridge.py    ★ MCP 桥：把 HTTP 端点包成标准 MCP 服务器
├─ docs/
│  ├─ ENDPOINTS.md     两个版本的端点全表
│  ├─ PITFALLS.md      Mono 版踩坑记录
│  └─ IL2CPP-NOTES.md  IL2CPP 版踩坑记录
├─ config/             配置示例
└─ README.md
```

---

## 免责声明

本项目仅供**学习、研究与单机游戏的本地调试**使用。
请在**你拥有或已获授权**的环境中使用；**不要**用于线上游戏、多人竞技或任何侵犯他人权益的场景。
使用本工具修改游戏数据可能违反游戏的用户协议，由此产生的一切后果由使用者自行承担。

## 许可

[MIT](LICENSE)

本项目以 NuGet 包形式引用 BepInEx（LGPL-2.1）与 Newtonsoft.Json（MIT），未修改其源码。
