# 踩坑集

这些都是实际踩过、且**换台机器还会再踩一次**的坑。按主题分组。

---

## 一、兼容层与虚拟机环境（附加场景；原生 Windows 通常遇不到）

### 1. Mono 的 `HttpListener` 完全不可用 —— 这是本项目的起点

| 尝试 | 现象 |
|---|---|
| 绑 `*` / `+` | Mono 不支持通配 host 前缀 → 任何请求 `400 Bad Request (Invalid host)` |
| 绑具体 IP（如 `10.x.x.x`）| TCP 能建连（`/dev/tcp` 成功），但**永远 0 字节响应**；`netstat` 显示连接堆在 `CLOSE_WAIT`；日志无任何 handler 记录 |
| 改用同步 `GetContext()` | 同样无效 |

**结论**：Mono 的 `HttpListener` 整套（含同步 accept 路径）在 Wine + Box64 下不可用 → **自写 `TcpListener` + 手写 HTTP**。

**诊断手法**（三者结合可判定"连接建立了但应用层从未读取"）：
1. 客户端看到 `0 bytes received`
2. `netstat` 看到 `CLOSE_WAIT`
3. 应用日志里关键行从未出现

---

## 二、BepInEx

### 2. cfg 是裸值格式，不要加引号

```ini
# ✕ 错：引号会成为值的一部分（日志里显示 http://"0.0.0.0":18081/...）
BindAddress = "0.0.0.0"

# ✓ 对
BindAddress = 0.0.0.0
```

### 3. 换 dll 必须重启游戏进程

插件只在游戏启动时由 Chainloader 加载一次，热替换 `MiniMCP.dll` 不会生效。

### 4. 注入需要 DLL 覆盖

Wine / Proton 下要劫持成功，需要：

```
WINEDLLOVERRIDES=winhttp=n,b
```

判断是否注入成功：看 `BepInEx/LogOutput.log` 里有没有 `Chainloader ready` 之类的行。

---

## 三、反射与读写

### 5. `/scan` 窄类型溢出（已修）

早期实现把目标值强转成字段类型再比较：

```csharp
// ✕ (byte)999999999 → 255，导致满屏 255 的颜色值被误命中，真字段被挤掉
if (t == typeof(byte)) return (byte)v == target;
```

修法：整型统一先转 `double` 再比。

```csharp
if (t == typeof(byte)) return (double)(byte)v == target;
```

### 6. `/scan` 不递归普通 C# 对象

扫描器只深挖 **Unity 类型**组件的字段，不递归纯 C# 数据对象（如存档类）。因此像 `save.TotalCash` 扫不到 —— 那是正常行为，**用 `/getpath` 直读**。

### 7. 枚举必须写整个字段 + 字符串名

```json
// ✕ 写 .value__ ：返回 ok 但值不变
{"path":"...Rarity.value__","value":3}

// ✓ 写整个字段
{"path":"...Rarity","value":"SSR"}
```

### 8. 集合必须整体写

```json
// ✕ 报 "Error converting value 3 to type 'X[]'"
{"path":"...chara_raritys[0]","value":"SSR"}

// ✓
{"path":"...chara_raritys","value":["SSR","SSR"]}
```

### 9. 空对象元素只会建出默认值

```json
// 会创建 N 个元素，但字段不会被填充（枚举变 "None"、子列表为 null）
{"path":"...UnlockedHPoseLv.u","value":[{},{},{}]}
```

**两步写法**：先整体写占位定长度 → 再逐元素写单字段。

```
1. setpath  ...u          = [{},{},...]        ← 定长度
2. setpath  ...u._items[0].h  = "Def"          ← 逐项填
3. setpath  ...u._items[0].lv = [1,2,3,4]
```

### 10. 只读属性改不了

报 `writable member not found: X on Y` 说明是 C# `{ get; }` 只读属性。可尝试：
- 写它的 backing field：`.<X>k__BackingField`
- 改**数据源**（很多运行时对象是从 JSON 数据镜像构建的，改数据源 + 重载即生效）

### 11. JSON 转义容易翻车

多层 shell 引号嵌套会把 `\"` 变成字面量 `\.` 传给服务端：

```
Invalid property identifier character: \. Path 'value[0]'
```

**对策**：把 JSON 放进**单引号**变量再传给 `curl -d`，不要手工叠转义。

### 12. 双份数据：UI 读的可能不是你以为的那份

很多游戏有"存档副本"与"运行时实例"两套数据。改了内存里回读正确、但界面不变，通常是**改错了那一份**。

排查法：找到 UI 组件（Text/TMP）的 `.text`，反查它读的数据源，再对照两份数据。

### 13. `Dictionary` 走 `_entries[i].key / .value`

C# `Dictionary<TKey,TValue>` 反射时没有 `Keys[i]`，要用内部数组：

```
_mstManager._skillMst._entries[8].key            → "sk_h_rate..."
_mstManager._skillMst._entries[8].value.BuffID   → 真正的字段值
```

---

## 四、运行时 / 环境行为

### 14. 游戏切到后台 → 连接全部挂死

部分平台（含安卓上的 Wine）在游戏切后台时会 `SIGSTOP` 整个 Wine 进程组（`ps` 状态显示 `T`）。此时：
- 端口仍显示 `LISTEN`
- 但**完全不响应**，连接堆在 `CLOSE_WAIT`

**不要误判成插件挂了** —— 先看 `ps` 的状态，把游戏切回前台即自动恢复。

### 15. 内存改动会丢，需要游戏自己保存

通过运行时桥改的是**进程内存**：
- 游戏进程被关 → 全部回退
- 有些游戏在特定操作（如进入"建造模式"）时会**用它自己的数据重写**那部分结构，把改动冲掉

**固化的唯一路径**：改完 → 在游戏内**保存** → 退出到主菜单 → 重新载入存档。
另外注意：如果某个结构在游戏内操作时会被重写，那就**改完别再碰相关界面**，直接去保存。

### 16. 主数据表在每次重载存档时会被重建

从游戏资源里加载的只读数据表（如各种 `Mst`），**每次读档都会重新构建**，运行时改的值不持久，也不在任何存档文件里。

对策：要么每次启动后重刷一遍（脚本化），要么改真正会被存档的字段。

---

## 五、跨设备 / 协作

### 17. 远端 proot 容器对多行命令处理不稳

给远端容器发多行命令 / heredoc 时，可能出现"只执行第一行""回显污染""输出丢失"。
**对策**：一律用**单行**命令，用 `;` 分隔，并显式 `echo` 关键结果做校验。

### 18. 容器里 `rm -rf` 可用，Android shell 里不行

Android 的 `sh`/`toybox` 会拦截含 `rm -rf` 字符串的命令（**纯字符串匹配**，连 echo 里的文本也会被拦）。
容器（proot Ubuntu）内不受此限制；在 Android 侧删除请用：

```bash
rm -f <file>
rmdir <emptydir>
find <dir> -delete
```

### 19. 别把临时/敏感文件放在共享存储

- 共享存储（如 `/sdcard`）上任何 App 都能读，key / token / 私密配置不要放那里
- 需要让容器脚本读凭据时，放到**容器自己的 `/root`**（rootfs 物理路径在宿主上，脚本从宿主侧写入即可）

---

## 六、安全（针对本类服务）

### 20. 默认监听 `0.0.0.0` 等于把游戏交出去

本插件**无鉴权**，同网段任何设备执行一条 `curl` 就能读写游戏内存、调用任意方法。

按需选择：
1. `BindAddress = 127.0.0.1`，需要远程时走 SSH / 隧道转发
2. 用完即关（关游戏 = 端口消失，端口只随游戏进程存在）
3. 路由器侧做网段隔离

### 21. 如果加了"请求体"处理，务必设上限

`new byte[contentLength]` 无上限时，一个 `Content-Length: 999999999` 就能把游戏进程的内存打爆。
本仓库当前版本保持最简实现、未加上限；如需扩展，记得在分配前先与一个常量上限比对。

---

## 元经验

1. **先看日志原文再改代码**。有些"看起来像算法问题"的故障，真根因都是日志里明写的一行路径（`E:\sdcard\...`）。
2. **失败路径要打日志**：加载失败时把"实际尝试过的路径"打出来，比任何猜测都值钱。
3. **备份先行**：每次替换 dll / 改配置前留 `.bak-<日期>-<原因>`，事后回滚只需一条 `cp`。
4. **两端对账**：跨设备协作时，文件传完要**双方各算一次 md5** 再确认。