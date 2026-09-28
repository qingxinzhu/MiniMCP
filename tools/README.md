# tools/

## mcp_bridge.py — HTTP 端点 → 标准 MCP 服务器

MiniMCP 本体是裸 HTTP（「类 MCP」）。这个桥把它包成**标准 MCP 服务器**（JSON-RPC 2.0 over stdio），
让 Claude Desktop / Cursor / Operit 等 MCP 客户端可以直接挂载。

**零依赖** —— 只用 Python 标准库。

### 用法

```bash
# 直接跑（stdin/stdout 走 JSON-RPC）
python3 mcp_bridge.py --url http://127.0.0.1:18081

# 或环境变量
MINIMCP_URL=http://127.0.0.1:18081 python3 mcp_bridge.py
```

### 挂到 Claude Desktop

`claude_desktop_config.json`：

```json
{
  "mcpServers": {
    "minimcp": {
      "command": "python3",
      "args": ["/abs/path/to/tools/mcp_bridge.py", "--url", "http://127.0.0.1:18081"]
    }
  }
}
```

### 它做了什么

- 实现 MCP 握手（`initialize`）、工具列表（`tools/list`）、工具调用（`tools/call`）
- **自动识别后端**：
  - `il2cpp` / `monogame-net` → 对象名风格端点（`/objects` `/get` `/set` `/call`）
  - 其他 → Mono 版路径风格端点（`/find` `/inspect` `/getpath` `/setpath`）
- 把 20+ 个参差不同的端点归并成 **13 个稳定工具**，AI 不用背版本差异
- 保留 `raw_request` 兑底，端点没封装也不会丢功能

### 暴露的工具

`game_health` `game_endpoints` `list_objects` `object_tree` `inspect_object`
`get_field` `set_field` `call_method` `move_object` `set_active` `set_timescale`
`scan_value` `raw_request`

### 自测

```bash
printf '%s\n' \
 '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
 '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
 | python3 mcp_bridge.py --url http://127.0.0.1:18081
```
