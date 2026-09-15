#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "==> 编译 Windows 资源 (main.syso)..."
i686-w64-mingw32-windres ../c/resource.rc -O coff -o main.syso

echo "==> 编译 Go 语言原生无依赖可执行文件 (Rick-go.exe)..."
GOOS=windows GOARCH=386 go build -ldflags "-H windowsgui -s -w" -o Rick-go.exe main.go

echo "==> 编译完成: $(ls -lh Rick-go.exe)"
