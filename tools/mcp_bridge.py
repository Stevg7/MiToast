# -*- coding: utf-8 -*-
"""直连 MiToastMcp.exe 抓取通知历史（历史通知 MCP 工具不可用时的兜底方案）。

用法:
    python tools/mcp_bridge.py [输出目录]
    默认输出目录: %TEMP%\\mitoast-mcp-out

产出:
    stats.json / cat_<分类>.json / app_wechat.json   原始 JSON（UTF-8）
    digest_core.txt   支付/订单/物流/日程/验证码/系统/进度/通用/推广 逐条精简
    digest_chat.txt   聊天/微信/音乐 按会话分组统计

说明:
    Windows 控制台默认 GBK，中文直接 print 会乱码，因此控制台只输出 ASCII 状态；
    中文数据一律写入 UTF-8 文件，用阅读工具读取。窗口固定为最近 72 小时。
    query/statistics 工具会触发 Windows Hello 身份验证弹窗且无内置超时，
    本脚本用读线程 + 队列实现每调用 240s 超时（AUTH 未点击时快速失败而非挂死），
    超时或验证被取消均以非零退出码结束并给出 AUTH_* 提示。
"""
import json
import os
import queue
import subprocess
import sys
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
EXE = HERE.parent / "windows" / "dist" / "MiToastMcp.exe"
OUT_DIR = (Path(sys.argv[1]) if len(sys.argv) > 1
           else Path(os.environ.get("TEMP", ".")) / "mitoast-mcp-out")

PER_CALL_TIMEOUT = int(os.environ.get("MITOAST_BRIDGE_TIMEOUT", "240"))  # 秒；留足用户点击 Windows Hello 弹窗的时间

CORE_CATEGORIES = ["payment", "order", "delivery", "schedule", "verification",
                   "system", "progress", "general", "promo"]
AUTH_FAIL_MARKER = "身份验证未通过"


class BridgeError(Exception):
    def __init__(self, code, detail=""):
        super().__init__("%s %s" % (code, detail))
        self.code = code
        self.detail = detail


def main():
    if not EXE.exists():
        print("EXE_MISSING:", EXE)
        return 2
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    proc = subprocess.Popen(
        [str(EXE)],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        text=True, encoding="utf-8", bufsize=1,
    )

    lines = queue.Queue()

    def pump():
        for line in iter(proc.stdout.readline, ""):
            lines.put(line)
        lines.put(None)  # EOF

    threading.Thread(target=pump, daemon=True).start()

    def send(obj):
        proc.stdin.write(json.dumps(obj, ensure_ascii=False) + "\n")
        proc.stdin.flush()

    def wait_for(req_id, tag):
        deadline = time.time() + PER_CALL_TIMEOUT
        while time.time() < deadline:
            try:
                line = lines.get(timeout=5)
            except queue.Empty:
                continue
            if line is None:
                raise BridgeError("SERVER_EOF", tag)
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            if msg.get("id") == req_id:
                return msg
        raise BridgeError("CALL_TIMEOUT", tag)

    try:
        send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
              "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                         "clientInfo": {"name": "mitoast-bridge", "version": "1.0"}}})
        wait_for(1, "initialize")
        print("INIT_OK", flush=True)
        send({"jsonrpc": "2.0", "method": "notifications/initialized"})

        send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        tools = [t.get("name") for t in
                 wait_for(2, "tools/list").get("result", {}).get("tools", [])]
        print("TOOLS:", tools, flush=True)

        def pick(fragment):
            for name in tools:
                if fragment in (name or ""):
                    return name
            return None

        stats_name, query_name = pick("stats"), pick("query")
        if not (stats_name and query_name):
            print("REQUIRED_TOOLS_MISSING")
            return 3

        call_id = [100]

        def call(name, args, out_name):
            call_id[0] += 1
            send({"jsonrpc": "2.0", "id": call_id[0], "method": "tools/call",
                  "params": {"name": name, "arguments": args}})
            result = wait_for(call_id[0], "tools/call " + out_name).get("result", {})
            try:
                text = result["content"][0]["text"]
            except (KeyError, IndexError, TypeError):
                text = json.dumps(result, ensure_ascii=False)
            if AUTH_FAIL_MARKER in text:
                raise BridgeError("AUTH_NOT_PASSED", out_name)
            (OUT_DIR / out_name).write_text(text, encoding="utf-8")
            print("SAVED", out_name, len(text), "bytes", flush=True)
            try:
                return json.loads(text)
            except ValueError:
                return {}

        now_ms = int(time.time() * 1000)
        since = now_ms - 72 * 3600 * 1000

        call(stats_name, {"hours": 72}, "stats.json")
        for cat in CORE_CATEGORIES:
            call(query_name, {"since": since, "category": cat, "limit": 500},
                 "cat_%s.json" % cat)
        call(query_name, {"since": since, "category": "chat", "limit": 120},
             "cat_chat.json")
        call(query_name, {"since": since, "category": "music", "limit": 30},
             "cat_music.json")
        call(query_name, {"since": since, "app": "微信", "limit": 60},
             "app_wechat.json")
    except BridgeError as exc:
        print("FAILED", exc.code, exc.detail, flush=True)
        if exc.code == "CALL_TIMEOUT":
            print("HINT: probably waiting on the Windows Hello consent dialog; "
                  "approve it or run when the user is present.", flush=True)
        proc.kill()
        return 4

    def load(name):
        try:
            return json.loads((OUT_DIR / name).read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {}

    core = []
    for cat in CORE_CATEGORIES:
        data = load("cat_%s.json" % cat)
        core.append("=== %s (%d) ===" % (cat.upper(), data.get("count", 0)))
        for n in data.get("notifications", []):
            content = (n.get("content") or "").replace("\n", " ")[:90]
            core.append("%s | %s | %s | %s" % (
                n.get("time", ""), n.get("app", ""),
                (n.get("title") or "")[:30], content))
        core.append("")
    (OUT_DIR / "digest_core.txt").write_text("\n".join(core), encoding="utf-8")

    chat = []
    for name in ["cat_chat.json", "app_wechat.json", "cat_music.json"]:
        data = load(name)
        groups = {}
        for n in data.get("notifications", []):
            key = (n.get("app", ""), (n.get("title") or "")[:28])
            groups.setdefault(key, []).append(n)
        chat.append("=== %s count %d groups %d ===" % (
            name, data.get("count", 0), len(groups)))
        for key, items in sorted(groups.items(), key=lambda kv: -len(kv[1])):
            latest = max(items, key=lambda x: x.get("timestamp", 0))
            content = (latest.get("content") or "").replace("\n", " ")[:70]
            chat.append("  [%3d] %s | %s | latest %s | %s" % (
                len(items), key[0], key[1], latest.get("time", ""), content))
        chat.append("")
    (OUT_DIR / "digest_chat.txt").write_text("\n".join(chat), encoding="utf-8")

    print("DIGESTS_WRITTEN to", OUT_DIR, flush=True)
    proc.stdin.close()
    try:
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        proc.kill()
    return 0


if __name__ == "__main__":
    sys.exit(main())
