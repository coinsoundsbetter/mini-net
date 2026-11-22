<h1 align="center">MiniNet</h1>

<p align="center"><strong>简体中文</strong> · <a href="README.en.md">English</a></p>

<p align="center"><strong>轻量的 C# 多人游戏网络框架</strong><br>
基于 KCP，支持类型化消息与服务端权威的对象复制。</p>

<p align="center">
  <a href="#功能">功能速览</a> ·
  <a href="#快速开始">快速开始</a> ·
  <a href="Assets/MiniNet/README.md">用户指南与 API</a>
</p>

---

- **用 C# 定义网络对象。** 用 `GhostBuilder` 组合需要同步的组件，无需创建或配置 Unity Prefab。
- **自己驱动网络循环。** 用 `TickIncoming()` 收包，在游戏逻辑运行后调用 `TickOutgoing()` 采集并发送状态。
- **在普通 .NET 进程中测试。** 运行时独立于 Unity，开发者或 Coding Agent 可以在代码中接入两端，驱动网络循环并断言结果。

## 功能

| 功能 | 支持的操作 |
| --- | --- |
| 连接管理 | 服务端监听和断开指定连接，客户端连接和断开，双方接收连接状态与错误事件。 |
| 消息收发 | 定义结构体消息并按类型注册回调，也可以直接发送字节。支持可靠和不可靠两种投递方式。 |
| 对象同步 | 用模板定义网络对象（Ghost），在服务端创建、更新和销毁。客户端首次收到完整状态，后续接收变化。 |
| 字段同步 | 用 `[GhostField]` 标记字段，Weaver 在编译时生成序列化和变化检测代码。客户端可监听创建、更新和移除事件。 |
| 可见性与子对象 | 按连接筛选接收方。子 Ghost 继承父级可见范围，还可以进一步限制自己的接收方。 |
| 所有权 | 指定或转移对象的所有者，处理客户端请求时可按发送者校验对象归属。 |

## 快速开始

四个示例共用一对服务端和客户端，依次演示连接、消息往返、字段复制和按队伍同步。示例引用 `MiniNet` 和 `MiniNet.Replicate`。

> [!NOTE]
> 示例中的类型化消息和 `[GhostField]` 使用 Unity 编译期 **Weaver** 生成的序列化代码。在普通 .NET 项目直接编译源码时，请注册消息读写函数并实现组件序列化。[编译与序列化](Assets/MiniNet/README.md#编译与序列化)

### 1. 启动服务端和客户端，打印连接日志

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

### 2. 客户端发消息，服务端回复

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

### 3. 修改服务端血量，客户端自动同步

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

### 4. 让所有人看到玩家，只让队友看到战术目标

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

## 与其他网络框架的不同之处

以 Mirror 为例，两者都能同步玩家血量，也都能把战术目标只发给队友。接入时的差异主要在于对象如何定义、谁驱动网络循环，以及可见性规则写在哪里。

| 接入时的问题 | MiniNet | Mirror |
| --- | --- | --- |
| 如何组织两端业务？ | MiniNet 没有统一的 `NetworkManager`。应用分别配置 `Client` 和 `Server`，鼓励从项目初期就分开编写两端业务，共享消息和状态定义。 | `NetworkManager` 提供客户端、服务端和主机模式的启动入口与生命周期回调。项目也可以分别组织两端业务。 |
| 在哪里运行和测试？ | 在普通 .NET 进程中即可创建服务端和客户端并测试交互。直接编译源码时，需为类型化消息和同步组件提供序列化实现。 | 网络对象和行为基于 Unity 的 `MonoBehaviour`，端到端测试通常在 Unity 环境中驱动这些组件。 |
| 如何定义一个有血量的玩家？ | 把带 `[GhostField]` 的组件加入 `GhostBuilder` 模板，用代码生成玩家，无需配置 Prefab。 | 在 GameObject 上使用 `NetworkIdentity`，在 `NetworkBehaviour` 中用 `[SyncVar]` 声明血量。动态生成对象通常使用已注册的 Prefab，也支持自定义生成函数。 |
| 谁决定收发时机？ | 宿主显式调用 `TickIncoming()` 和 `TickOutgoing()`，可以把战斗结算放在两次调用之间。 | 框架将网络更新接入 Unity 的 `PlayerLoop`，默认随 Unity 更新循环收发。 |
| 如何只向队友同步战术目标？ | 将目标放进子 Ghost，用 `visibleTo` 筛选连接。子对象继承父对象的可见范围，并可进一步缩小范围。 | 通过 Interest Management 决定每个 `NetworkIdentity` 的观察者，可选用内置队伍、距离策略或实现自定义策略。 |

以购买药水为例，客户端发送请求，服务端校验背包归属和余额，再扣款并增加药水。修改购买按钮时，可以把 Coding Agent 的上下文限定为客户端实现和共享消息定义，服务端校验与结算逻辑留在独立文件中。[客户端购买请求](Assets/MiniNet/Examples/Inventory.Client.cs) · [服务端购买处理](Assets/MiniNet/Examples/Inventory.Server.cs)

如果你希望开发者或 Coding Agent 在 C# 中完成接入，并在独立 .NET 进程里验证一轮收发，MiniNet 的代码模板和显式循环更贴合这条工作流。如果项目已经围绕 Unity 场景、Prefab 和组件组织，Mirror 可以沿用现有的对象结构。[Mirror 自定义生成函数](https://mirror-networking.gitbook.io/docs/manual/guides/gameobjects/custom-spawnfunctions) · [Mirror 兴趣管理](https://mirror-networking.gitbook.io/docs/manual/interest-management)
