#!/bin/bash
# 并发测试运行脚本

echo "======================================"
echo "JT808-2019 并发性能测试"
echo "======================================"
echo ""

# 设置PATH
export PATH="$HOME/.dotnet:$PATH"

# 进入项目目录
cd /home/shenzheng/JT808Server2019

# 停止可能运行的服务器
echo "停止现有服务器进程..."
pkill -f "JT808.Server" || true
sleep 2

# 编译测试工具
echo "编译并发测试工具..."
dotnet build JT808.ConcurrencyTest/JT808.ConcurrencyTest.csproj --configuration Release -v q

# 启动服务器(后台)
echo "启动测试服务器..."
cd JT808.Server
nohup dotnet run --configuration Release -- --test-mode > /tmp/jt808_server.log 2>&1 &
SERVER_PID=$!
echo "服务器PID: $SERVER_PID"

# 等待服务器启动
echo "等待服务器启动(5秒)..."
sleep 5

# 检查服务器是否启动
if ! netstat -tuln | grep -q 8809 && ! ss -tuln | grep -q 8809; then
    echo "错误: 服务器未能启动,请检查日志: /tmp/jt808_server.log"
    tail -30 /tmp/jt808_server.log
    exit 1
fi

echo "服务器已启动!"
echo ""

# 运行并发测试
echo "运行并发测试..."
cd ../JT808.ConcurrencyTest

# 小规模测试: 50个客户端, 每个发5条消息
echo "50" | echo "8809" | echo "127.0.0.1" | dotnet run --configuration Release <<EOF
127.0.0.1
8809
50
5
Y
EOF

echo ""
echo "测试完成!"

# 等待一下让服务器处理完
sleep 3

# 停止服务器
echo "停止服务器..."
kill $SERVER_PID 2>/dev/null || true

# 显示服务器日志末尾
echo ""
echo "======================================"
echo "服务器日志 (最后20行):"
echo "======================================"
tail -20 /tmp/jt808_server.log

echo ""
echo "完整日志: /tmp/jt808_server.log"
