#!/usr/bin/env bash
# 用本机 Chrome 无头模式按真实逻辑尺寸给原型截图（先 flutter build web，再在 8790 端口起静态服务）。
# 用法：bash tool/shots.sh <输出目录>
set -euo pipefail
out=${1:?输出目录}
mkdir -p "$out"
chrome="/c/Program Files/Google/Chrome/Application/chrome.exe"
base=http://localhost:8790/
shot() { # 名字 宽 高 查询参数
  "$chrome" --headless=new --hide-scrollbars --force-dark-mode --window-size="$2,$3" \
    --virtual-time-budget=15000 --screenshot="$(cygpath -w "$out")\\$1.png" "$base?clean=1&$4" >/dev/null 2>&1
  echo "$1"
}
# iPad Air 4：1180×820 横 / 820×1180 竖（2360×1640 @2x）
shot 01-connect-landscape 1180 820 "stage=connect"
shot 02-local-network 1180 820 "stage=localNetwork"
shot 03-waiting 1180 820 "stage=preparing"
shot 04-pick-role 1180 820 "stage=pickRole"
shot 05-reader-spread-chrome 1180 820 "stage=inGame&page=3"
shot 06-reader-spread-immersive 1180 820 "stage=inGame&page=3&immersive=1"
shot 07-reader-portrait 820 1180 "stage=inGame&page=3"
shot 08-reader-portrait-invert 820 1180 "stage=inGame&page=3&night=invert&immersive=1"
shot 09-text-mode 820 1180 "stage=inGame&text=1"
shot 10-reading-sheet 820 1180 "stage=inGame&page=3&sheet=reading"
shot 11-cards-two-pane 1180 820 "stage=inGame&section=cards"
shot 12-cards-portrait 820 1180 "stage=inGame&section=cards"
shot 13-talk-speaking 1180 820 "stage=inGame&section=talk&speaking=1"
shot 14-phase-sheet 1180 820 "stage=inGame&section=talk&sheet=phase"
shot 15-offline 820 1180 "stage=inGame&page=9&offline=1&immersive=0"
shot 16-light-theme 1180 820 "stage=inGame&section=cards&theme=light&night=off"
shot 17-role-sheet 1180 820 "stage=inGame&sheet=role&unread=2"
# 安卓平板常见 1280×800
shot 18-android-landscape 1280 800 "stage=inGame&page=5"
shot 19-android-portrait 800 1280 "stage=inGame&section=cards"
