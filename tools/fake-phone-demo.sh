#!/usr/bin/env bash
# 外卖流程本机演示（无需手机在线）：起一个本机"假手机"WebSocket 服务，
# 把 PC 端 MiToast 的手动地址临时指向它并重启，推送"美团外卖"6 阶段通知，
# 演示结束后恢复原配置并重启回真实连接。
#
# 用法：
#   tools/fake-phone-demo.sh              # 默认 4 秒一阶段
#   tools/fake-phone-demo.sh 2500         # 2.5 秒一阶段
set -u

PORT=18080
INTERVAL="${1:-4000}"
CFG="$APPDATA/MiToast/settings.json"
DIST="F:/project/MiToast/windows/dist/MiToast.exe"
ADB="C:/Users/Administrator/AppData/Local/Android/Sdk/platform-tools/adb.exe"

stop_app() {
    powershell -NoProfile -Command 'Get-Process MiToast -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "F:\project\MiToast\windows\dist\MiToast.exe" } | Stop-Process -Force; Start-Sleep 1' >/dev/null 2>&1
}

start_app() {
    powershell -NoProfile -Command "Start-Process -FilePath '$DIST' -WorkingDirectory 'F:\project\MiToast\windows\dist'" >/dev/null 2>&1
}

echo "── 1/5 停止当前 MiToast 实例"
stop_app

echo "── 2/5 备份并修改 settings.json（手动地址 → 127.0.0.1:$PORT）"
cp "$CFG" "$CFG.bak-demo"
powershell -NoProfile -Command "
\$j = Get-Content '$CFG' -Raw | ConvertFrom-Json
\$j.ManualPhoneHost = '127.0.0.1'
\$j.ManualPhonePort = $PORT
\$j | ConvertTo-Json -Depth 6 | Set-Content '$CFG' -Encoding UTF8
" >/dev/null 2>&1

echo "── 3/5 启动 MiToast（它会拨号连到本机假手机）"
start_app

echo "── 4/5 假手机开始服务（PC 连入后自动推 6 阶段，间隔 ${INTERVAL}ms）"
dotnet run --project F:/project/MiToast/tools/FakePhone -- $PORT "$INTERVAL"

echo "── 5/5 演示结束，恢复原配置并重启 MiToast"
cp "$CFG.bak-demo" "$CFG" && rm -f "$CFG.bak-demo"
start_app
sleep 3
powershell -NoProfile -Command '$p = Get-Process MiToast -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "F:\project\MiToast\windows\dist\MiToast.exe" }; if ($p) { "MiToast 已重启 PID=" + $p.Id } else { "MiToast 未在运行" }' 2>&1 | tail -1
