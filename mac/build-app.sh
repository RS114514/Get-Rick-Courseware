#!/bin/bash
# build-app.sh

echo "🚀 开始进行 Release 发布与打包..."

# 1. 发布为单文件自包含的可执行包
dotnet publish RickCourseware.Mac.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o ./publish-mac

if [ $? -ne 0 ]; then
    echo "❌ 编译失败，终止打包。"
    exit 1
fi

APP_NAME="获取Rick课件.app"

# 2. 清理并重建 .app 目录结构
echo "📂 创建 $APP_NAME 目录结构..."
rm -rf "$APP_NAME"
mkdir -p "$APP_NAME"/Contents/MacOS
mkdir -p "$APP_NAME"/Contents/Resources

# 3. 拷贝发布生成的全部文件到 MacOS 文件夹下
echo "💾 拷贝二进制程序和动态库依赖..."
cp -r ./publish-mac/* "$APP_NAME"/Contents/MacOS/

# 拷贝 app.ico 到 MacOS 运行目录，使状态托盘图标能正常加载
if [ -f "../app.ico" ]; then
    cp "../app.ico" "$APP_NAME"/Contents/MacOS/app.ico
fi

# 4. 生成 macOS 专有的 .icns 格式图标
echo "🎨 正在生成 macOS 应用图标..."
if [ -f "../app.ico" ]; then
    # 提取 1024x1024 尺寸为 PNG 母图
    sips -s format png ../app.ico --out master_icon.png > /dev/null 2>&1
    
    if [ -f "master_icon.png" ]; then
        # 创建 iconset 目录结构
        mkdir -p app.iconset
        
        # 使用 sips 缩放并输出各分辨率尺寸的图标
        sips -z 16 16     master_icon.png --out app.iconset/icon_16x16.png > /dev/null 2>&1
        sips -z 32 32     master_icon.png --out app.iconset/icon_16x16@2x.png > /dev/null 2>&1
        sips -z 32 32     master_icon.png --out app.iconset/icon_32x32.png > /dev/null 2>&1
        sips -z 64 64     master_icon.png --out app.iconset/icon_32x32@2x.png > /dev/null 2>&1
        sips -z 128 128   master_icon.png --out app.iconset/icon_128x128.png > /dev/null 2>&1
        sips -z 256 256   master_icon.png --out app.iconset/icon_128x128@2x.png > /dev/null 2>&1
        sips -z 256 256   master_icon.png --out app.iconset/icon_256x256.png > /dev/null 2>&1
        sips -z 512 512   master_icon.png --out app.iconset/icon_256x256@2x.png > /dev/null 2>&1
        sips -z 512 512   master_icon.png --out app.iconset/icon_512x512.png > /dev/null 2>&1
        sips -z 1024 1024 master_icon.png --out app.iconset/icon_512x512@2x.png > /dev/null 2>&1
        
        # 使用 iconutil 将 iconset 打包为 icns
        iconutil -c icns app.iconset --o "$APP_NAME"/Contents/Resources/app.icns
        
        # 清理临时文件
        rm -rf app.iconset master_icon.png
        echo "✓ 图标打包成功: $APP_NAME/Contents/Resources/app.icns"
    else
        echo "⚠ 图标生成失败，使用系统默认图标。"
    fi
else
    echo "⚠ 找不到 app.ico，使用系统默认图标。"
fi

# 5. 生成 Info.plist 元数据文件
echo "📝 写入 Info.plist 配置文件..."
cat <<EOF > "$APP_NAME"/Contents/Info.plist
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>获取Rick课件Mac</string>
    <key>CFBundleIconFile</key>
    <string>app.icns</string>
    <key>CFBundleIdentifier</key>
    <string>com.rs.rickcourseware</string>
    <key>CFBundleName</key>
    <string>获取Rick课件</string>
    <key>CFBundleDisplayName</key>
    <string>获取Rick课件</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>3.0.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.15</string>
</dict>
</plist>
EOF

# 6. 设置执行权限
chmod +x "$APP_NAME"/Contents/MacOS/获取Rick课件Mac

echo "✅ macOS App 打包成功！"
echo "应用位于: \$(pwd)/\$APP_NAME"
