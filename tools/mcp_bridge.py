#!/usr/bin/env python3
"""
MiniMCP -> MCP bridge

把 MiniMCP 的 HTTP 端点包成一个标准的 MCP 服务器（JSON-RPC 2.0 / stdio），
这样 Claude Desktop / Cursor / Operit 这类 MCP 客户端可以直接挂载。

用法：
    python3 mcp_bridge.py --url http://127.0.0.1:18081
    或环境变量：MINIMCP_URL=http://127.0.0.1:18081 python3 mcp_bridge.py

仅用 Python 标准库，无第三方依赖。
"""

import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

PROTOCOL_VERSION = "2024-11-05"
SERVER_NAME = "minimcp-bridge"
SERVER_VERSION = "1.0.0"


def log(msg):
    sys.stderr.write("[minimcp-bridge] %s\n" % msg)
    sys.stderr.flush()


class GameClient(object):
    def __init__(self, base_url):
        self.base = base_url.rstrip("/")
        self.edition = None          # 'mono' | 'il2cpp'
        self.info = {}

    def detect(self):
        try:
            self.info = self.request("GET", "/health")
            raw_ed = str(self.info.get("edition", "")).lower()
            if raw_ed in ("il2cpp", "monogame-net"):
                self.edition = raw_ed          # 对象名风格端点 + 统一 JSON
            elif "endpoints" in self.info or raw_ed == "":
                self.edition = "mono"          # Mono 版：路径风格端点
            else:
                self.edition = raw_ed or "mono"
            log("connected: edition=%s (object-style=%s)" % (self.edition, self.object_style))
        except Exception as e:
            log("detect failed: %s" % e)
            self.edition = "mono"
        return self.info

    @property
    def object_style(self):
        """对象名风格端点（/objects /get /set /call）：IL2CPP 与 MonoGame 版均是"""
        return self.edition in ("il2cpp", "monogame-net")

    @property
    def supports_args(self):
        """是否支持带参方法调用：只有 MonoGame/.NET 版可以（纯托管反射，安全）"""
        return self.edition == "monogame-net"

    def request(self, method, path, body=None, timeout=20):
        url = self.base + path
        data = None
        headers = {"Accept": "application/json"}
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                raw = r.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as e:
            raw = e.read().decode("utf-8", "replace")
        except Exception as e:
            return {"ok": False, "error": "transport: %s" % e}
        try:
            return json.loads(raw)
        except Exception:
            return {"ok": True, "raw": raw}


# --------------------------------------------------------------------------
# 工具定义（跨版本统一层）
# --------------------------------------------------------------------------

def q(s):
    return urllib.parse.quote(str(s), safe="")


TOOLS = [
    {
        "name": "game_health",
        "description": "游戏/插件存活状态。返回插件版本、Unity 版本、当前场景。任何操作前先调这个确认连接正常。",
        "inputSchema": {"type": "object", "properties": {}},
    },
    {
        "name": "game_endpoints",
        "description": "列出当前版本（Mono / IL2CPP）实际支持的所有端点。不确定能用什么时先调这个。",
        "inputSchema": {"type": "object", "properties": {}},
    },
    {
        "name": "list_objects",
        "description": "列出游戏中的对象（可按名称关键词过滤）。返回每个对象的名称、类型、坐标、子对象数、激活状态。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": {"type": "string", "description": "名称关键词（模糊匹配，可留空列全部）"},
                "tree": {"type": "boolean", "description": "true 时改为返回对象树（需配合 name 指定根对象）"},
                "depth": {"type": "integer", "description": "树深度，默认 3"},
            },
        },
    },
    {
        "name": "object_tree",
        "description": "打印某个对象的层级树（缩进文本）。`*` 表示激活，`.` 表示未激活。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": {"type": "string", "description": "根对象名（必填）"},
                "depth": {"type": "integer", "description": "展开深度，默认 3"},
            },
            "required": ["name"],
        },
    },
    {
        "name": "inspect_object",
        "description": "查看对象的内部结构：字段清单（含当前值）与可调用的方法。AI 探索游戏时的主力工具。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": {"type": "string", "description": "对象名（必填）"},
                "methods": {"type": "boolean", "description": "true 时改列方法清单"},
            },
            "required": ["name"],
        },
    },
    {
        "name": "get_field",
        "description": "读取一个字段的值。两个版本寻址方式不同，本工具已自动适配（Mono 走 path，IL2CPP 走对象名+字段名）。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "【Mono 版】字段路径，如 save.Charas[1].Attr.NowLike"},
                "object_id": {"type": "string", "description": "【Mono 版】对象 instanceId（可选，与 path 二选一）"},
                "name": {"type": "string", "description": "【IL2CPP 版】对象名（必填）"},
                "field": {"type": "string", "description": "【IL2CPP 版】字段名（必填）"},
            },
        },
    },
    {
        "name": "set_field",
        "description": "修改一个字段的值。这是最常用的写操作（改血量、金币、坐标、状态等）。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "【Mono 版】字段路径"},
                "object_id": {"type": "string", "description": "【Mono 版】对象 instanceId"},
                "name": {"type": "string", "description": "【IL2CPP 版】对象名"},
                "field": {"type": "string", "description": "【IL2CPP 版】字段名"},
                "value": {"description": "新值（字符串/数字/布尔均可）"},
                "component": {"type": "string", "description": "【Mono 版】组件名（可选）"},
            },
            "required": ["value"],
        },
    },
    {
        "name": "call_method",
        "description": "调用对象上的方法。IL2CPP 版出于安全只允许 0 参方法；Mono 版可带参。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": {"type": "string", "description": "对象名"},
                "method": {"type": "string", "description": "方法名"},
                "object_id": {"type": "string", "description": "【Mono 版】对象 instanceId"},
                "component": {"type": "string", "description": "【Mono 版】组件名"},
                "args": {"type": "array", "description": "参数列表（Mono 版用；IL2CPP 请留空）", "items": {}},
            },
            "required": ["method"],
        },
    },
    {
        "name": "move_object",
        "description": "【IL2CPP 版】修改对象坐标（可直接传 x/y/z，也可传 dx/dy 做相对位移）。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": {"type": "string"},
                "x": {"type": "number"}, "y": {"type": "number"}, "z": {"type": "number"},
                "dx": {"type": "number"}, "dy": {"type": "number"},
            },
            "required": ["name"],
        },
    },
    {
        "name": "set_active",
        "description": "【IL2CPP 版】开关一个对象（等价于 Unity 的 SetActive）。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": {"type": "string"},
                "active": {"type": "boolean"},
            },
            "required": ["name", "active"],
        },
    },
    {
        "name": "set_timescale",
        "description": "【IL2CPP 版】修改游戏速度倍率（1=正常，0.1=慢动作，3=加速）。",
        "inputSchema": {
            "type": "object",
            "properties": {"value": {"type": "number"}},
            "required": ["value"],
        },
    },
    {
        "name": "scan_value",
        "description": "【Mono 版】按数值反查字段（不知道字段叫什么时用这个搜）。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "value": {"description": "要搜索的数值"},
                "root": {"type": "string", "description": "搜索根路径（可选）"},
            },
            "required": ["value"],
        },
    },
    {
        "name": "raw_request",
        "description": "兑底：直发一个原始请求到插件。两个版本端点有差异时、或需要本工具未封装的功能时用它。先用 game_endpoints 看支持哪些路径。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "method": {"type": "string", "description": "GET 或 POST，默认 GET"},
                "path": {"type": "string", "description": "如 /tree?name=Player 或 /stats"},
                "body": {"type": "object", "description": "POST 时的 JSON 体（可选）"},
            },
            "required": ["path"],
        },
    },
]


# --------------------------------------------------------------------------
# 工具实现
# --------------------------------------------------------------------------
def tool_health(c, a):
    return c.info if c.info else c.detect()


def tool_endpoints(c, a):
    return c.request("GET", "/")


def tool_list_objects(c, a):
    name = a.get("name", "")
    if a.get("tree"):
        return c.request("GET", "/tree?name=%s&depth=%d" % (q(name), int(a.get("depth", 3))))
    if c.object_style:
        return c.request("GET", "/objects?name=%s" % q(name))
    # Mono 版：用 /find 搜对象，没有批量列表就用树扫
    if name:
        return c.request("GET", "/find?name=%s" % q(name))
    return c.request("GET", "/tree?depth=%d" % int(a.get("depth", 2)))


def tool_tree(c, a):
    return c.request("GET", "/tree?name=%s&depth=%d" % (q(a.get("name", "")), int(a.get("depth", 3))))


def tool_inspect(c, a):
    nm = a.get("name", "")
    if c.object_style:
        if a.get("methods"):
            return c.request("POST", "/listfields", {"name": nm})
        return c.request("GET", "/fields?name=%s" % q(nm))
    if a.get("methods"):
        return c.request("GET", "/fields?name=%s" % q(nm))
    return c.request("GET", "/inspect?name=%s" % q(nm))


def tool_get(c, a):
    if c.object_style:
        return c.request("POST", "/get", {"name": a.get("name"), "field": a.get("field")})
    body = {}
    if a.get("path"):
        body["path"] = a["path"]
    if a.get("object_id"):
        body["id"] = a["object_id"]
    if a.get("field"):
        body["path"] = a["field"]
    return c.request("POST", "/getpath", body)


def tool_set(c, a):
    if c.object_style:
        return c.request("POST", "/set", {"name": a.get("name"), "field": a.get("field"), "value": a.get("value")})
    body = {"value": a.get("value")}
    if a.get("path"):
        body["path"] = a["path"]
    if a.get("object_id"):
        body["id"] = a["object_id"]
    if a.get("component"):
        body["component"] = a["component"]
    return c.request("POST", "/setpath", body)


def tool_call(c, a):
    if c.object_style:
        body = {"name": a.get("name"), "method": a.get("method")}
        if c.supports_args:
            for i, v in enumerate(a.get("args") or []):
                body["arg%d" % i] = v
        return c.request("POST", "/call", body)
    body = {"method": a.get("method")}
    if a.get("object_id"):
        body["id"] = a["object_id"]
    if a.get("component"):
        body["component"] = a["component"]
    args = a.get("args") or []
    if args:
        body["args"] = args
    return c.request("POST", "/call", body)


def tool_move(c, a):
    body = {"name": a.get("name")}
    for k in ("x", "y", "z", "dx", "dy"):
        if a.get(k) is not None:
            body[k] = a[k]
    return c.request("POST", "/move", body)


def tool_setactive(c, a):
    return c.request("POST", "/setactive", {"name": a.get("name"), "active": bool(a.get("active"))})


def tool_timescale(c, a):
    return c.request("POST", "/timescale", {"value": a.get("value", 1)})


def tool_scan(c, a):
    if c.object_style:
        return {"ok": False, "error": "%s 版不支持 /scan，请用 inspect_object 或 raw_request" % c.edition}
    return c.request("GET", "/scan?value=%s" % q(a.get("value")))


def tool_raw(c, a):
    m = str(a.get("method", "GET")).upper()
    return c.request(m, a.get("path", "/"), a.get("body"))


HANDLERS = {
    "game_health": tool_health,
    "game_endpoints": tool_endpoints,
    "list_objects": tool_list_objects,
    "object_tree": tool_tree,
    "inspect_object": tool_inspect,
    "get_field": tool_get,
    "set_field": tool_set,
    "call_method": tool_call,
    "move_object": tool_move,
    "set_active": tool_setactive,
    "set_timescale": tool_timescale,
    "scan_value": tool_scan,
    "raw_request": tool_raw,
}


# --------------------------------------------------------------------------
# JSON-RPC / MCP
# --------------------------------------------------------------------------
class Bridge(object):
    def __init__(self, url):
        self.client = GameClient(url)
        self.client.detect()

    def handle(self, msg):
        method = msg.get("method")
        mid = msg.get("id")
        params = msg.get("params") or {}

        if method == "initialize":
            return self.reply(mid, {
                "protocolVersion": params.get("protocolVersion", PROTOCOL_VERSION),
                "capabilities": {"tools": {"listChanged": False}},
                "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
                "instructions": "MiniMCP bridge (edition=%s). "
                                "先调 game_health 确认连接，再调 game_endpoints 看该版本支持什么，"
                                "然后用 list_objects / inspect_object 探索，用 get_field / set_field / call_method 读写。"
                                % self.client.edition,
            })
        if method in ("notifications/initialized", "initialized"):
            return None
        if method == "ping":
            return self.reply(mid, {})
        if method == "tools/list":
            return self.reply(mid, {"tools": TOOLS})
        if method == "tools/call":
            name = params.get("name")
            args = params.get("arguments") or {}
            fn = HANDLERS.get(name)
            if fn is None:
                return self.reply(mid, self.content({"ok": False, "error": "unknown tool: %s" % name}, True))
            try:
                out = fn(self.client, args)
            except Exception as e:
                out = {"ok": False, "error": "handler error: %s" % e}
            return self.reply(mid, self.content(out, not (isinstance(out, dict) and out.get("ok") is False)))
        if mid is None:
            return None
        return self.error(mid, -32601, "method not found: %s" % method)

    @staticmethod
    def content(obj, ok):
        text = json.dumps(obj, ensure_ascii=False, indent=2)
        return {"content": [{"type": "text", "text": text}], "isError": not ok}

    @staticmethod
    def reply(mid, result):
        return {"jsonrpc": "2.0", "id": mid, "result": result}

    @staticmethod
    def error(mid, code, msg):
        return {"jsonrpc": "2.0", "id": mid, "error": {"code": code, "message": msg}}


def main():
    url = None
    argv = sys.argv[1:]
    for i, a in enumerate(argv):
        if a == "--url" and i + 1 < len(argv):
            url = argv[i + 1]
        elif a.startswith("--url="):
            url = a.split("=", 1)[1]
    url = url or os.environ.get("MINIMCP_URL") or "http://127.0.0.1:18081"
    log("starting, target=%s" % url)
    bridge = Bridge(url)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except Exception as e:
            log("bad json: %s" % e)
            continue
        try:
            out = bridge.handle(msg)
        except Exception as e:
            log("handle error: %s" % e)
            out = bridge.error(msg.get("id"), -32603, str(e))
        if out is not None:
            sys.stdout.write(json.dumps(out, ensure_ascii=False) + "\n")
            sys.stdout.flush()


if __name__ == "__main__":
    main()
