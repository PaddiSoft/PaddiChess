#!/bin/bash
set -euo pipefail

project_root="$(cd "$(dirname "$0")" && pwd)"
dotnet_path="$(command -v dotnet || true)"
if [[ -z "$dotnet_path" && -x /usr/local/share/dotnet/dotnet ]]; then
  dotnet_path=/usr/local/share/dotnet/dotnet
fi
if [[ -z "$dotnet_path" ]]; then
  echo "未找到 .NET SDK，请安装 .NET 10 后重试。"
  read -r -p "按回车关闭…"
  exit 1
fi

echo "正在编译并启动 Paddi象棋源码版。现有 .app 安装包保持不变。"
exec "$dotnet_path" run --project "$project_root/Paddi象棋/Paddi象棋.csproj" -c Release --no-launch-profile
