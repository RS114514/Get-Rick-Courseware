#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

mkdir -p publish-win

echo "========================================="
echo "1. 编译纯 C 语言原生 Win32 版本 (Rick-c.exe)"
echo "========================================="
cd "$SCRIPT_DIR/c"
./build.sh
cp Rick-c.exe "$SCRIPT_DIR/publish-win/Rick-c.exe"

echo ""
echo "========================================="
echo "2. 编译 Go 语言原生 Win32 版本 (Rick-go.exe)"
echo "========================================="
cd "$SCRIPT_DIR/go"
./build.sh
cp Rick-go.exe "$SCRIPT_DIR/publish-win/Rick-go.exe"

echo ""
echo "========================================="
echo "3. 编译 .NET 版本 (Rick.exe)"
echo "========================================="
cd "$SCRIPT_DIR"
csc -target:winexe -platform:anycpu -out:publish-win/Rick.exe \
    -win32icon:app.ico -win32manifest:app.manifest \
    -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.dll -r:System.Core.dll -r:System.Management.dll -r:System.Data.dll \
    -d:WINDOWS -optimize+ \
    rick.cs USBMonitor.cs Settings.cs
cp -f "$SCRIPT_DIR/publish-win/Rick.exe" "$SCRIPT_DIR/publish-win/获取Rick课件.exe"
rm -f "$SCRIPT_DIR/publish-win/Rick_极速兼容版.exe"

echo ""
echo "========================================="
echo "全部多语言版本编译完成！产物列表："
echo "========================================="
ls -lh "$SCRIPT_DIR/publish-win/"*.exe
