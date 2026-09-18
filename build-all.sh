#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

mkdir -p publish-win

# 清理历史废弃版本的残留产物
rm -f "$SCRIPT_DIR/publish-win/Rick-c.exe" \
      "$SCRIPT_DIR/publish-win/Rick-go.exe" \
      "$SCRIPT_DIR/publish-win/Rick-asm.exe" \
      "$SCRIPT_DIR/publish-win/Rick_极速兼容版.exe"

# 自动检测 C# 编译器命令（macOS/Windows使用csc，Linux Ubuntu下可能为csc、mono-csc或mcs）
if command -v csc >/dev/null 2>&1; then
    CSC_CMD="csc"
elif command -v mono-csc >/dev/null 2>&1; then
    CSC_CMD="mono-csc"
elif command -v mcs >/dev/null 2>&1; then
    CSC_CMD="mcs"
else
    echo "❌ 错误：未找到 C# 编译器 (csc / mono-csc / mcs)！"
    exit 1
fi

echo "========================================="
echo "编译官方主版本 (Rick.exe) [编译器: $CSC_CMD]"
echo "========================================="
$CSC_CMD -target:winexe -platform:anycpu -out:publish-win/Rick.exe \
    -win32icon:app.ico -win32manifest:app.manifest \
    -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.dll -r:System.Core.dll -r:System.Management.dll -r:System.Data.dll \
    -d:WINDOWS -optimize+ \
    rick.cs USBMonitor.cs Settings.cs

cp -f "$SCRIPT_DIR/publish-win/Rick.exe" "$SCRIPT_DIR/publish-win/获取Rick课件.exe"

echo ""
echo "========================================="
echo "编译完成！产物列表："
echo "========================================="
ls -lh "$SCRIPT_DIR/publish-win/"*.exe
