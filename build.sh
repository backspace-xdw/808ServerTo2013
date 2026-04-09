#!/bin/bash
# JT808Server2019 编译脚本

echo "=================================="
echo "JT808-2019 服务器编译脚本"
echo "=================================="
echo ""

# 检查 dotnet 是否安装
if ! command -v dotnet &> /dev/null; then
    echo "错误: 未找到 .NET SDK"
    echo "请访问 https://dotnet.microsoft.com/download 下载安装"
    exit 1
fi

echo "检测到 .NET 版本:"
dotnet --version
echo ""

# 清理旧的编译文件
echo "[1/4] 清理旧的编译文件..."
dotnet clean
echo ""

# 恢复依赖
echo "[2/4] 恢复 NuGet 包..."
dotnet restore
echo ""

# 编译项目
echo "[3/4] 编译项目..."
dotnet build --configuration Release
echo ""

# 显示结果
echo "[4/4] 编译完成!"
echo ""
echo "运行服务器 (JT/T 808-2019):"
echo "  cd JT808.Server && dotnet run"
echo ""
echo "运行测试客户端:"
echo "  cd JT808.TestClient && dotnet run"
echo ""
echo "默认端口: 8809 (区别于2013版本的8808)"
echo ""
