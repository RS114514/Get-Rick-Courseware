@echo off
chcp 65001 > null
echo 🚀 开始编译 Windows 桌面版本 (win-x64)...

:: 执行 dotnet 发布指令，生成自包含、内嵌原生依赖的单文件 exe
dotnet publish RickCourseware.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish-win

if %ERRORLEVEL% NEQ 0 (
    echo ❌ 编译失败，终止操作。
    pause
    exit /b %ERRORLEVEL%
)

:: 拷贝并重命名主执行文件
echo 💾 正在重命名程序为 Rick.exe...
if exist "publish-win\获取Rick课件.exe" (
    move /y "publish-win\获取Rick课件.exe" "publish-win\Rick.exe" > null
)

echo.
echo ✅ Windows 单文件客户端编译成功！
echo 成果物位于: %~dp0publish-win\Rick.exe
echo.
pause
