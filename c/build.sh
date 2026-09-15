#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "==> 编译 Windows 资源 (图标与清单)..."
i686-w64-mingw32-windres resource.rc -O coff -o resource.o

echo "==> 编译纯 C 语言原生无依赖可执行文件 (Rick-c.exe)..."
i686-w64-mingw32-gcc -O2 -mwindows -nostdlib -e _mainEntry -s -o Rick-c.exe main.c resource.o -lkernel32 -luser32 -lshell32 -lole32

echo "==> 编译成功: $(ls -lh Rick-c.exe)"
