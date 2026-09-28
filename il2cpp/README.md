# MiniMCP — IL2CPP 版

**把 Unity IL2CPP 游戏变成一个可通过 HTTP 访问的运行时桥。**

与仓库根目录的 [Mono 版](../README.md) 是**两套独立实现**：

| | Mono 版（根目录 `src/`）| **IL2CPP 版（本目录）** |
|---|---|---|
| 适配游戏 | Mono 后端的 Unity 游戏 | **IL2CPP 后端的 Unity 游戏** |
| 依赖 | BepInEx **5.4.23.x** | BepInEx **6 (be.xxx, IL2CPP)** |
| 目标框架 | netstandard2.0 | **net6.0** |
| 反射机制 | .NET 反射 | **il2cpp native API + Il2CppInterop** |
| 编译期依赖 | 全部来自 NuGet | **必须引用游戏生成的 interop 程序集** |
| 产物 | `MiniMCP.dll` | `MiniMCP.Il2Cpp.dll` |

> ⚠️ 跟 Mono 版一样：**不做鉴权、不加密通信**（纯明文 HTTP），默认只绑 `127.0.0.1`。
> 它是为「本机自用 / 自己的测试环境」设计的。

---

## 一、准备：先把 BepInEx 6 装好并跑一次

IL2CPP 插件**不能凭空编译** —— 它必须引用游戏运行时生成的 interop 程序集。

1. 下载 **BepInEx 6 IL2CPP**（Windows x64）
   从 <https://builds.bepinex.dev/projects/bepinex_be> 取最新 `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.XXX+xxxx.zip`
2. 解压到**游戏根目录**（与 `GameAssembly.dll`、`<游戏名>_Data/` 同级）
3. **启动游戏一次**，然后关闭
4. 确认这些文件已经生成：

```
<游戏根目录>/
├─ GameAssembly.dll              ← 游戏是 IL2CPP 的标志
├─ winhttp.dll                   ← BepInEx 注入用
├─ doorstop_config.ini
└─ BepInEx/
   ├─ core/                      ← BepInEx.Core.dll / Il2CppInterop.Runtime.dll ...
   └─ interop/                   ← ★ 首次启动后自动生成，编译时必需
      ├─ Assembly-CSharp.dll
      ├─ Il2Cppmscorlib.dll
      ├─ UnityEngine.CoreModule.dll
      └─ UnityEngine.PhysicsModule.dll
```

> `BepInEx/interop/` 是**编译的硬前提**。没有它就无法编译本插件。

---

## 二、编译

需要 **.NET SDK 6 或更高**。

### 1. 改一行配置

打开 `MiniMcpIl2Cpp.csproj`，把 `GameDir` 改成你的游戏根目录：

```xml
<GameDir Condition="'$(GameDir)' == ''">C:\Path\To\YourGame</GameDir>
```

例如：

```xml
<GameDir>C:\steam\steamapps\common\YourGame</GameDir>
```

> 也可以不改文件，编译时传参：
> ```bash
> dotnet build MiniMcpIl2Cpp.csproj -c Release -p:GameDir="D:\Games\YourGame"
> ```

### 2. 编译

```bash
cd il2cpp
dotnet build MiniMcpIl2Cpp.csproj -c Release
# 产物: bin/Release/net6.0/MiniMCP.Il2Cpp.dll
```

### 3. 部署

把 `MiniMCP.Il2Cpp.dll` 放进 `<游戏根目录>/BepInEx/plugins/`，**重启游戏**。

（插件不支持热重载，改 dll 必须重启游戏。）

---

## 三、验证

游戏起来后：

```bash
curl http://127.0.0.1:18081/health
# {"ok":true,"edition":"il2cpp","version":"0.3.0","port":18081,
#  "unity":"2021.3.35f1","product":"<你的游戏>"}
```

完整端点列表见 **[../docs/ENDPOINTS.md](../docs/ENDPOINTS.md)**。

---

## 四、端口

固定监听 `127.0.0.1:18081`（只绑本机，公网不可达）。

需要远程访问时，**不要**把它改成 `0.0.0.0` —— 用隧道转发：

```bash
# 例：cloudflared 临时隧道
cloudflared tunnel --url http://127.0.0.1:18081
```

---

## 五、IL2CPP 特有的坑（**必读**）

### 1. `il2cpp_class_get_fields` 能直接把游戏搞崩

对 `Transform` 这类 native-heavy 的 Unity 内置类做 native 字段反射 → **游戏进程瞬间消失**，且 try/catch **拦不住**（挂在 native 层）。

本插件已内置防护：跳过 `UnityEngine.*` / `System.*` / `Il2Cpp*` / `MagicaCloth*`，只读游戏自定义脚本类的值类型字段，字段上限 40，继承深度上限 8。

### 2. `il2cpp_runtime_invoke` 传错参数 = 崩

需要参数的方法传空参数数组 → 崩溃。因此 `POST /call` **只放行 0 参数方法**（其余返回明确报错）。

### 3. 插件骨架三条铁律

```csharp
// 1. 必须先注册类型
ClassInjector.RegisterTypeInIl2Cpp<MyBehaviour>();
// 2. 再挂载
AddComponent<MyBehaviour>();
// 3. 构造函数必须这样写
public MyBehaviour(IntPtr ptr) : base(ptr) { }
```

缺任何一条都跑不起来。

### 4. Unity API 只能在主线程调用

所有请求由后台线程收下 → 丢进 `ConcurrentQueue` → 主线程每帧取出执行（`RunOnMain<T>`）。

更多踩坑记录：**[../docs/IL2CPP-NOTES.md](../docs/IL2CPP-NOTES.md)**

---

## 六、已知限制

| 限制 | 说明 |
|---|---|
| `POST /call` 只支持 0 参数方法 | 见上文第 2 条（安全考虑）|
| `/fields` 不支持 Unity 内置类 | 见上文第 1 条（安全考虑）|
| 无热重载 | 换 dll 必须重启游戏 |
| interop 依赖 | 换游戏要重新生成 interop 并重编译 |

---

## 七、许可

同仓库根目录：MIT
