# MiniNet

MiniNet 是轻量的 C# 多人游戏网络框架，基于 KCP，支持类型化消息和服务端权威的对象复制。网络对象用 C# 模板定义，无需配置 Unity Prefab。网络循环由你驱动，收包、游戏逻辑和发送的先后顺序直接体现在代码里。

运行时可以独立于 Unity。开发者或 Coding Agent 可以直接在代码中接入两端，在普通 .NET 测试进程里驱动它们并断言结果。下面四个例子共用同一对服务端和客户端，引用 `MiniNet` 和 `MiniNet.Replicate`。消息和字段示例使用 Unity 编译期 **Weaver** 生成的序列化代码。普通 .NET 项目直接编译源码时，需要手动注册消息读写函数并实现组件序列化。[编译与序列化](Assets/MiniNet/README.md#编译与序列化)

## 1. 启动服务端和客户端，打印连接日志

以本机双端为例。先订阅事件，再监听和连接。后续示例中的处理器、模板和事件订阅也放在开始收包之前。

```csharp
var server = new Server(new TransportServer());
var client = new Client(new TransportClient());

server.RemoteClientConnected += id => System.Console.WriteLine($"服务端：玩家 {id} 已连接");
client.OnConnected += () => System.Console.WriteLine("客户端：已连上服务端");

server.Listen(7777);
client.Connect("127.0.0.1", 7777);
```

在宿主的更新循环里反复执行下面四行，连接建立后，两端各打印一条日志。分进程部署时，每端只驱动自己的实例。

```csharp
server.TickIncoming();
client.TickIncoming();
// 在这里处理本轮游戏逻辑。
server.TickOutgoing();
client.TickOutgoing();
```

退出时调用 `client.Disconnect()` 和 `server.Shutdown()`。

## 2. 客户端发消息，服务端回复

两端共享消息定义。服务端处理器收到的 `sender` 就是发送者的连接 ID，可以直接用它回复该客户端。

```csharp
public struct ChatMessage : INetworkMessage {
    public string Text;
}
```

```csharp
server.RegisterHandler<ChatMessage>((sender, msg, mode) => {
    System.Console.WriteLine($"玩家 {sender} 说：{msg.Text}");
    server.Send(sender, new ChatMessage { Text = "收到：" + msg.Text });
});

client.RegisterHandler<ChatMessage>((msg, mode) => System.Console.WriteLine(msg.Text));
client.OnConnected += () => client.Send(new ChatMessage { Text = "你好" });
```

保持示例 1 的循环运行，客户端会打印「收到：你好」。

## 3. 修改服务端血量，客户端自动同步

把血量放进 `GhostComponent`，用 `[GhostField]` 标出同步字段。两端用相同的模板 ID 和组件顺序构建模板。

```csharp
public sealed class PlayerState : GhostComponent {
    [GhostField] public int Hp;
}
```

```csharp
var playerTemplate = new GhostBuilder(1).Component<PlayerState>().Build();
client.ReplicateManager.RegisterTemplate(playerTemplate);

void LogHp(Ghost ghost) {
    if (ghost.TryGetComponent<PlayerState>(out var state))
        System.Console.WriteLine($"玩家 {ghost.NetId} 的血量：{state.Hp}");
}
client.ReplicateManager.Spawned += LogHp;
client.ReplicateManager.Updated += LogHp;

var player = server.ReplicateManager.Spawn(playerTemplate);
player.GetComponent<PlayerState>().Hp = 100;
```

客户端首次收到玩家时打印血量 100。稍后在服务端处理一次受伤：

```csharp
player.GetComponent<PlayerState>().Hp -= 10;
```

下一次服务端 `TickOutgoing()` 会采集变化，客户端收包后打印 90。业务代码只改字段，无需再定义或发送扣血消息。

## 4. 让所有人看到玩家，只让队友看到战术目标

玩家继续使用示例 3 的模板。把需要限制接收范围的字段放到独立的子 Ghost 上，就可以单独控制谁收到这部分数据。

```csharp
public sealed class TeamTactics : GhostComponent {
    [GhostField] public int TargetId;
}
```

```csharp
var tacticsTemplate = new GhostBuilder(2).Component<TeamTactics>().Build();
client.ReplicateManager.RegisterTemplate(tacticsTemplate); // 每个客户端在收包前注册

var teammates = new System.Collections.Generic.HashSet<int>();
var tactics = server.ReplicateManager.SpawnChild(player, tacticsTemplate,
    visibleTo: teammates.Contains);
tactics.GetComponent<TeamTactics>().TargetId = 42;
```

服务端在入队和退队事件中更新集合，`connectionId` 来自连接事件。

```csharp
teammates.Add(connectionId);    // 入队：下次同步时收到子对象及其当前目标
// ... 玩家稍后退队 ...
teammates.Remove(connectionId); // 退队：下次同步时移除该客户端上的子对象
```

子对象的接收方必须能看到父对象，同时通过 `visibleTo` 条件。队外玩家仍能看到父对象的血量，战术目标的创建和字段更新只发给可见的队友，从而减少下行流量。[双客户端范围同步测试](Assets/MiniNet/Tests/GhostBindingTests.cs)

## 文档与示例

- [用户指南与 API 速查](Assets/MiniNet/README.md)
- [客户端购买请求](Assets/MiniNet/Examples/Inventory.Client.cs)与[服务端归属校验](Assets/MiniNet/Examples/Inventory.Server.cs)
