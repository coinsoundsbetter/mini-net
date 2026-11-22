# MiniNet

MiniNet 是一个基于 KCP 的 C# 多人游戏网络框架，提供客户端与服务端连接、类型化消息和服务端权威的对象状态同步。运行时可由普通 .NET 程序或 Unity 驱动。

Unity 依赖集中在编译期的 **Weaver**：它接入 Unity 编译管线，为消息和同步字段自动生成序列化代码。在普通 .NET 项目中直接编译源码时，也可以手动注册消息序列化器、实现组件读写。详见[编译与序列化](Assets/MiniNet/README.md#编译与序列化)。

## 功能

| 功能 | 支持的操作 |
| --- | --- |
| 连接管理 | 服务端监听和断开指定连接，客户端连接和断开，双方接收连接状态与错误事件。 |
| 消息收发 | 定义结构体消息并按类型注册回调，也可以直接发送字节。支持可靠和不可靠两种投递方式。 |
| 对象同步 | 用模板定义网络对象（Ghost），在服务端创建、更新和销毁。客户端首次收到完整状态，后续接收变化。 |
| 字段同步 | 用 `[GhostField]` 标记字段，Weaver 在编译时生成序列化和变化检测代码。客户端可监听创建、更新和移除事件。 |
| 可见性与子对象 | 按连接筛选接收方。子 Ghost 继承父级可见范围，还可以进一步限制自己的接收方。 |
| 所有权 | 指定或转移对象的所有者，处理客户端请求时可按发送者校验对象归属。 |

## 基本用法

引用命名空间 `MiniNet` 和 `MiniNet.Replicate`。如果业务脚本使用 asmdef，添加对 `MiniNet` 程序集的引用。

### 1. 建立连接

服务端和客户端分别初始化，在各自的更新循环中驱动网络。下面用本机地址演示。

```csharp
var server = new Server(new TransportServer());
server.Listen(7777);

var client = new Client(new TransportClient());
client.Connect("127.0.0.1", 7777);
```

每帧先调用本端的 `TickIncoming()`，处理游戏逻辑后调用 `TickOutgoing()`。退出时，客户端调用 `Disconnect()`，服务端调用 `Shutdown()`。

### 2. 发送和接收消息

```csharp
public struct ChatMessage : INetworkMessage {
    public string Text;
}
```

```csharp
// 服务端：在开始收包前注册处理器。sender 是发送者的连接 ID。
server.RegisterHandler<ChatMessage>((sender, msg, mode) =>
    System.Console.WriteLine($"[{sender}] {msg.Text}"));

// 客户端：连接成功后发送，默认可靠投递。
client.Send(new ChatMessage { Text = "你好" });
```

### 3. 同步对象状态

```csharp
public sealed class PlayerState : GhostComponent {
    [GhostField] public int Hp;
}
```

```csharp
// 两端使用相同的模板 ID 和组件布局。
var playerTemplate = new GhostBuilder(1).Component<PlayerState>().Build();

// 客户端：在收包前注册模板。
client.ReplicateManager.RegisterTemplate(playerTemplate);

// 服务端：创建对象，之后直接修改同步字段。
var player = server.ReplicateManager.Spawn(playerTemplate);
player.GetComponent<PlayerState>().Hp = 100;
```

客户端可订阅 `Spawned`、`Updated` 和 `Despawned` 以更新游戏表现。服务端调用 `Despawn(player.NetId)` 后，框架会移除客户端对应的副本。

## 文档与示例

- [用户指南与 API 速查](Assets/MiniNet/README.md)包含可直接挂到 GameObject 上运行的本机通信示例。
- [客户端发送请求](Assets/MiniNet/Examples/Inventory.Client.cs)和[服务端校验归属](Assets/MiniNet/Examples/Inventory.Server.cs)展示一次完整的业务交互。
- [子对象与可见性测试](Assets/MiniNet/Tests/GhostBindingTests.cs)展示如何按连接同步部分状态。
