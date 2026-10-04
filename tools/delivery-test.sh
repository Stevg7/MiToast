#!/usr/bin/env bash
# 外卖通知测试脚本：触发手机端 MiToast 内置的模拟接收器，生成一条"美团外卖"
# 从下单到送达的完整流程。与真实通知走完全相同的转发链路（通知监听 → WebSocket → PC 卡片），
# PC 端会按 key 原地更新同一张卡片（阶段进度推进），不会弹新卡。
#
# 用法：
#   tools/delivery-test.sh              # 全流程：下单→已接单→制作→取餐→配送→送达（默认 8 秒一阶段）
#   tools/delivery-test.sh -i 3000      # 全流程，改成 3 秒一阶段
#   tools/delivery-test.sh -s 5         # 只发单个阶段（1=已下单 2=已接单 3=商家制作 4=骑手取餐 5=配送中 6=已送达）
#   tools/delivery-test.sh --clear      # 移除 PC 端的模拟卡片
#
# 前提：手机已装 MiToast 且与 PC 在同一网络；无线调试或 USB 连接任一可用。

set -u

ADB="C:/Users/Administrator/AppData/Local/Android/Sdk/platform-tools/adb.exe"
[ -x "$ADB" ] || ADB=$(command -v adb) || { echo "找不到 adb"; exit 1; }

# ── 选设备：无线调试的端口会轮换、同一设备可能出现多条重名条目，
#    优先取状态为 device 的第一台；一台都没有时尝试 mDNS 自动连接 ──
pick_device() {
    local serial
    serial=$("$ADB" devices 2>/dev/null | grep -m1 "device$" | awk '{print $1}')
    if [ -n "${serial:-}" ]; then echo "$serial"; return 0; fi

    echo "无已连接设备，尝试 mDNS 自动连接…" >&2
    "$ADB" mdns services 2>/dev/null | grep -o "192\.168\.[0-9.]*:[0-9]*" | sort -u | while read -r addr; do
        "$ADB" connect "$addr" >/dev/null 2>&1
    done
    serial=$("$ADB" devices 2>/dev/null | grep -m1 "device$" | awk '{print $1}')
    if [ -n "${serial:-}" ]; then echo "$serial"; return 0; fi
    return 1
}

SERIAL=$(pick_device) || { echo "连接不上手机：请确认无线调试已开（开发者选项 → 无线调试），或插 USB 线"; exit 1; }
echo "设备: $SERIAL"

EXTRA_ARGS=()
case "${1:-}" in
    -i) EXTRA_ARGS=(--el interval "${2:-3000}");;
    -s) if [ "${2:-0}" -ge 1 ] && [ "${2:-0}" -le 6 ]; then
            EXTRA_ARGS=(--ei stage "$2")
        else
            echo "阶段号必须是 1-6"; exit 1
        fi;;
    --clear) EXTRA_ARGS=(--ez clear true);;
esac

echo "触发外卖模拟流程…"
"$ADB" -s "$SERIAL" shell am broadcast -a com.mitoast.action.SIM_DELIVERY \
    -n com.mitoast/.simulate.SimulateReceiver "${EXTRA_ARGS[@]}" \
    | grep -E "Broadcast completed|Error|error" || true

if [ "${1:-}" != "--clear" ]; then
    echo "已触发。盯着 PC 端的卡片看阶段推进（取餐码/进度条/文案随阶段更新）。"
    echo "提前结束: tools/delivery-test.sh --clear"
fi
