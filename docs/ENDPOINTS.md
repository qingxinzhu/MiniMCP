# 端点参考

所有端点都挂在同一个端口上（默认 **18081**），协议是**明文 HTTP，无鉴权**。

```bash
curl http://<IP>:18081/health
```

> ⚠️ 无鉴权意味着同网段任何人也能调用这些端点。请只在本机或完全可信的网络里使用（见 README「安全」）。
> 路径里含 `[` `]` 时，`curl` 需要加 `-g` 关闭通配，或对 URL 做百分号编码。

---

## GET /health

存活探测 + 环境信息。

```json
{"ok":true,"plugin":"MiniMCP","unity":"6000.3.4f1","scene":"<Scene>"}
```

---

## GET /tree

导出场景对象树。

| 参数 | 说明 |
|---|---|
| `depth` | 递归深度（默认较小，按需加大）|
| `max` | 每个层级最多返回多少个对象 |

> 大场景返回体可达数百 KB，慎用大 `max`。

---

## GET /find

搜索对象，返回其 **instanceId**（后续请求都要用它）。

| 参数 | 说明 |
|---|---|
| `name` | 按 GameObject / 组件名搜索（**注意：是 `name=`，不是 `q=` / `pattern=`**）|
| `component` | 按组件类型名搜索 |
| `max` | 返回条数上限（默认 50）|

```bash
curl -g "http://<IP>:18081/find?name=CSShopWindow"
```

返回：

```json
{"ok":true,"count":2,"results":[{"name":"CSShopWindow(Clone)","id":-3514510,"active":true,"path":"/GMCS.../UI[3]/CSShopWindow(Clone)[2]"}]}
```

> ⚠️ **instanceId 不稳定**：重新载入存档 / 窗口重建后 id 会变（负数 id 常见），每次改值前先 `/find`。

---

## GET /inspect

列出某个对象/组件的成员及其当前值。

| 参数 | 说明 |
|---|---|
| `id` | **必填**，对象 instanceId |
| `limit` | 成员数量上限 |

---

## GET /fields

列出成员清单（含 backing field 与属性），不带值 —— 用来看结构。

| 参数 | 说明 |
|---|---|
| `id` | **必填** |
| `path` | 可选，深入到指定字段再列成员 |

```bash
# 看某个组件的字段名
curl -g "http://<IP>:18081/fields?id=326440"

# 看某个字段下面的成员
curl -g "http://<IP>:18081/fields?id=326440&path=save._saveData"
```

输出是 JSON，可用 `grep -o '"name":"[^"]*"'` 快速提取成员名。

---

## GET /getpath

**按路径读值**（推荐的主要读取方式）。

| 参数 | 说明 |
|---|---|
| `id` | **必填**，根对象 instanceId |
| `path` | 路径，支持点号、数组下标、Dictionary 条目 |

路径语法：

```
save.TotalCash
save.Charas[1].Attr.NowLike
_mstManager._skillMst._entries[8].value.BuffID      ← Dictionary 的 _entries[i].key / .value
_charaManager._allChara[1].MaidData.Skills._items[0].SkillMstID
```

返回：

```json
{"ok":true,"path":"save.TotalCash","type":"Int64","value":996810299}
```

对象值会显示为 `<类名>`（如 `<List`1>`、`<CSSkill>`），此时用 `._size` / `._items[j]` 继续往下走。

---

## POST /setpath

**按路径写值** —— 最主要的写入方式。

```bash
curl -X POST "http://<IP>:18081/setpath" \
  -H 'Content-Type: application/json' \
  -d '{"id":326440,"path":"save.HPoint","value":99999}'
```

成功返回：

```json
{"ok":true,"path":"save.HPoint","now":99999}
```

### 各类型的值写法（实战总结）

| 目标类型 | 写法 | 说明 |
|---|---|---|
| Int / Long / Float / Bool | `"value": 100` | 直接写 |
| String | `"value": "floor_white_wood"` | 直接写 |
| **枚举** | `"value": "SSR"` | **必须写整个字段**；写 `.value__` 无效（返回 ok 但不生效）|
| **List / 数组** | `"value": [1,2,3,4]` | **必须整体写**；写单元素会报类型转换错误 |
| 对象列表 | `"value": [{"Field":"x"}, ...]` | 元素字段名需与被写类型一致；**空对象 `{}` 只会建出默认值/基类**，字段不会被填充 |
| 只读属性 | ✕ `writable member not found` | 需改其 backing field `.<Name>k__BackingField` 或改数据源 |

写入失败的常见错误：

| 报错 | 原因 |
|---|---|
| `writable member not found: X on Y` | 该属性/字段只读（如 C# `{ get; }`）|
| `Invalid property identifier character: \.` | JSON 转义错误 —— **用单引号包 JSON**，别多层转义 |
| `Exception has been thrown by the target of an invocation.` | 目标类型无法从 JSON 反序列化（常见于只读集合 / 抽象基类）|
| `null at segment: X` | 路径中间对象为 null |

---

## GET /scan

按**数值**反查字段 —— 在不知道字段名时用它"捞出"目标。

| 参数 | 说明 |
|---|---|
| `value` | 要匹配的数值 |
| `max` | 返回条数上限 |

**注意两点**：

1. 只匹配 **Unity 类型组件**的字段，**不会递归进普通 C# 数据对象**（所以像 `save.TotalCash` 这种扫不到，要用 `/getpath` 直读）。
2. 早期版本存在**窄类型溢出** bug（`(byte)999999999` → 255 导致满屏误报），现已修复：整型统一先转 `double` 再比较。

---

## POST /set

按 `id + component + member` 直接写值（不走路径）。

```json
{"id":326440,"component":"...","member":"...","value":123}
```

---

## POST /call

调用对象上的方法。

```json
{"id":326440,"component":"...","method":"...","args":[...]}
```

> 方法在**主线程**执行，返回前会等待完成（有超时）。

---

## 响应约定

每个响应体都是 JSON，用 `ok` 字段区分成功 / 失败：

```json
{"ok":true,  ...}
{"ok":false, "error":"..."}
```

> 本版（v0.1.0 明文基线）沿用最简实现：**HTTP 状态码一律 `200`**，成功与否看响应体里的 `ok` 字段。
> 想改成规范的 4xx/5xx，在 `MiniMcp.cs` 的 `WriteResponse` 里带上状态码即可（端点逻辑不用动）。