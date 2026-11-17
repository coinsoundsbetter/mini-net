# MiniNet

轻量 C# 网络库。当前完成项目骨架，尚未实现网络协议或公开 API。

## 环境

- .NET SDK 10.0.302 或更高的 10.0 SDK（见 global.json）。
- 类库同时面向 net8.0 和 net10.0。
- 启用可空引用类型、隐式 using、.NET 分析器，并将编译警告视为错误。
- 当前没有第三方包依赖。

## 目录

- MiniNet.sln：解决方案。
- src/MiniNet/：核心类库。
- Directory.Build.props：公共编译配置。
- global.json：SDK 选择规则。

## 构建与打包

~~~sh
dotnet restore MiniNet.sln
dotnet build MiniNet.sln -c Release --no-restore
dotnet pack src/MiniNet/MiniNet.csproj -c Release -o artifacts
~~~

后续根据使用场景确定传输协议、连接生命周期、消息分帧和序列化接口，再添加对应实现、测试与示例。
