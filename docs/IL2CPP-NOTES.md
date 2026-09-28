# IL2CPP 版开发笔记

这份文档记录把 MiniMCP 移植到 **Unity IL2CPP** 后端时踩到的坑与结论。
适用于想自己写 IL2CPP 插件的读者。

---

## 一、环境判定：这游戏到底是不是 IL2CPP？

看游戏根目录有没有这两个东西：

| 标志 | Mono | **IL2CPP** |
|---|---|---|
| `GameAssembly.dll` | ✗ 没有 | ✓ **有**（几 MB ~ 几十 MB）|
| `<游戏名>_Data/Managed/` | ✓ 有（一堆 .dll）| ✗ **没有** |
| `<游戏名>_Data/il2cpp_data/` | ✗ | ✓ 有（含 `global-metadata.dat`）|

装了 BepInEx 6 并启动一次后，`BepInEx/interop/` 会生成几十个 interop 程序集 —— 有它们才能编译 IL2CPP 插件。

---

## 二、插件骨架：三条铁律，缺一不可

```csharp
[BepInPlugin("com.example.plugin", "MyPlugin", "1.0.0")]
public class MyPlugin : BasePlugin
{
    public override void Load()
    {
        // 【1】必须先注册类型，否则 AddComponent 直接崩
        ClassInjector.RegisterTypeInIl2Cpp<MyBehaviour>();

        // 【2】再挂载到常驻对象上
        var go = new GameObject("MyPluginHost");
        GameObject.DontDestroyOnLoad(go);
        go.AddComponent<MyBehaviour>();
    }
}

// 【3】构造函数必须是这个签名
public class MyBehaviour : MonoBehaviour
{
    public MyBehaviour(IntPtr ptr) : base(ptr) { }
}
```

- 缺 **①**：`AddComponent` 抛异常或直接崩游戏
- 缺 **③**：编译期就报错，或者运行时无法实例化
- `BasePlugin` 与 `MonoBehaviour` **不能是同一个类**（IL2CPP 下必须分开）

---

## 三、反射：能用 native API，但边界很窄

### 可用的关键符号（均在 `Il2CppInterop.Runtime.IL2CPP` 上）

```
il2cpp_object_get_class          il2cpp_class_get_fields
il2cpp_class_get_name            il2cpp_class_get_namespace
il2cpp_class_get_methods         il2cpp_class_get_method_from_name
il2cpp_class_get_parent          il2cpp_class_get_field_from_name
il2cpp_field_get_name            il2cpp_field_get_offset
il2cpp_field_get_type            il2cpp_field_get_value
il2cpp_field_set_value           il2cpp_type_get_name
il2cpp_method_get_param_count    il2cpp_runtime_invoke
il2cpp_string_chars              il2cpp_gc_disable / il2cpp_gc_enable
```

### ⛔ 危险边界（血泪教训）

**`il2cpp_class_get_fields` 对 `Transform` 等 native-heavy 的 Unity 内置类调用 → 游戏进程瞬间消失。**

- try/catch **拦不住**（挂在 native 层，不是托管异常）
- 表现：进程直接退出，无日志、无堆栈
- 对策（本插件实际采用）：

```
跳过 UnityEngine.* / System.* / Il2Cpp* / MagicaCloth*
只读游戏自定义脚本类
只读值类型字段（string/对象/数组一律不 deref）
字段数上限 40，继承深度上限 8，字段去重
```

**`il2cpp_runtime_invoke(method, obj, args, exc)` 传错参数 → 同样直接崩。**

需要参数的方法传空数组 ⇒ 崩溃。所以本插件的 `/call` **只放行 0 参数方法**：

```csharp
int pcount = (int)IL2CPP.il2cpp_method_get_param_count(method);
if (pcount != 0) return ErrJson("method needs " + pcount + " args; only 0-arg calls are supported for safety");
```

> 注意 `il2cpp_method_get_param_count` 返回 **`uint`**，要显式 `(int)` 转换。

---

## 四、线程：Unity API 只能在主线程

HTTP 请求在后台线程到达 ⇒ 所有 Unity 调用必须回主线程。

本插件的做法：

```
后台线程收到请求
   ↓ 封装成委托，塞进 ConcurrentQueue
主线程 Update()/每帧 取出执行
   ↓ ManualResetEventSlim 回传结果（带超时）
```

---

## 五、写安全型字段读取器

按类型分支读，**不要**对对象类型做指针解引用：

```csharp
switch (typeEnum)
{
    case 0x02: /* bool   */ return readByte(ptr) != 0 ? "true" : "false";
    case 0x03: /* char   */ return ((char)readU16(ptr)).ToString();
    case 0x04: case 0x05: case 0x06: case 0x07:   // i1 i2 i4 i8
    case 0x08: case 0x09: case 0x0A: case 0x0B:   // u1 u2 u4 u8
    case 0x0C: /* r4 */ case 0x0D: /* r8 */       // 数值
    case 0x0E: /* string */ return ptrToStr(...);
    default:   return "null";   // ← 关键：对象/数组/类 一律不 deref
}
```

---

## 六、兼容层（Wine / Box64）为什么不行

实测结论：

| 环节 | 结果 |
|---|---|
| BepInEx 6 本体注入 | 有时能起来 |
| .NET 6 运行时 JIT | ✗ `Fatal error. Internal CLR error 0x80131506` |
| Cpp2IL 处理 `global-metadata.dat` | ✗ 生成不出 interop |

**结论：IL2CPP + BepInEx 6 需要原生 Windows（或 x86_64 原生环境）。**
测试机上是盒子架构（Box64 模拟 x86_64）时，.NET 6 的 JIT 直接挂。

---

## 七、编译链路的现实选择

IL2CPP 插件**必须**引用目标游戏生成的 interop 程序集 ⇒ 编译**必须在有游戏的那台机器上做**（或把 interop 拷过来）。

典型流程：

```
1. 游戏机装 BepInEx 6，启动一次 → 生成 BepInEx/interop/
2. 游戏机装 .NET SDK 6
3. 把源码 + csproj 拷到游戏机，dotnet build
4. 产物拷进 BepInEx/plugins/，重启游戏
```

（本次开发就是把编译完全丢给目标机器跑的：写码在一边，编译部署在另一边。）

---

## 八、代码穿越多层转义时的坑

如果代码要经过 `JSON → shell → heredoc → C#` 多层传递，**反斜杠会被层层吃掉**：

| 写的 | 实际到达 C# 的 | 后果 |
|---|---|---|
| `\\"` | `"` | 字符串提前结束，语法错 |
| `\\n` | 真换行 | 同上 |
| `'\\\\'` | `'\'` | 少一个反斜杠 |

**对策**：

1. C# 里用常量拼 JSON，不用转义引号
   ```csharp
   private const char Qc = (char)34;
   return "{" + Qc + "ok" + Qc + ":true}";
   ```
2. 二进制走 base64
3. 写文件用 heredoc 原样落盘，不经过字符串解析层

---

## 九、可用工具链速查

| 目的 | 命令 |
|---|---|
| 编译 | `dotnet build MiniMcpIl2Cpp.csproj -c Release` |
| 指定游戏目录 | `-p:GameDir="D:\Games\X"` |
| 部署 | `copy bin\Release\net6.0\MiniMCP.Il2Cpp.dll <游戏>\BepInEx\plugins\` |
| 验证 | `curl http://127.0.0.1:18081/health` |

---

## 十、一句话总结

**IL2CPP 的反射不是 .NET 反射** —— 它是 native 内存操作，**边界外一次调用就能把游戏送走**。
所以这一版的每一条安全限制（跳过内置类、只放行 0 参方法）都不是保守，而是踩出来的。
