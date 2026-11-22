# MiniNet 用户指南

MiniNet 是 Unity 的服务端权威网络框架，底层使用 KCP。客户端用**消息**表达一次操作（如购买药水），服务端用 **Ghost** 同步持续状态（如金币和药水数量）。业务程序集使用 asmdef 时，请引用 `MiniNet`。连接与消息 API 位于 `MiniNet`，Ghost API 位于 `MiniNet.Replicate`。

## 快速开始：本机收发一条消息

把下面的脚本保存为 `MiniNetDemo.cs`，挂到场景中的 GameObject 后进入 Play 模式。它复用仓库中的 `ChatMessage`，在同一进程启动两端；连接成功后，Unity Console 会打印服务端收到的消息。

```csharp
using MiniNet;
using MiniNet.Examples;
using UnityEngine;

public class MiniNetDemo : MonoBehaviour {
    private readonly Server server = new Server(new TransportServer());
    private readonly Client client = new Client(new TransportClient());

    void Start() {
        server.RegisterHandler<ChatMessage>((sender, msg, mode) =>
            Debug.Log($"玩家 {sender}：{msg.Text}"));
        client.OnConnected += () => client.Send(new ChatMessage { Text = "你好" });
        server.Listen(7777);
        client.Connect("127.0.0.1", 7777);
    }

    void Update() {
        server.TickIncoming();
        client.TickIncoming();
        // 在收包和发包之间处理本帧的游戏逻辑。
        server.TickOutgoing();
        client.TickOutgoing();
    }

    void OnDestroy() {
        client.Disconnect();
        server.Shutdown();
    }
}
```

分进程运行时，每端只驱动自己的实例。请显式调用这两个 Tick；当前 `NetworkLoop` 是空实现。服务端在 `TickOutgoing()` 中采集 Ghost 状态并发送。

## 连接与消息 API

| 操作 | 服务端 `Server` | 客户端 `Client` |
|---|---|---|
| 生命周期 | `Listen(port)`、`DisconnectClient(id)`、`Shutdown()` | `Connect(address, port)`、`Disconnect()` |
| 状态与事件 | `State`、`RemoteClientConnected(id)`、`RemoteClientDisconnected(id)`、`Error(id, text)` | `State`、`OnConnected()`、`OnDisconnected()`、`Error(text)` |
| 发送类型化消息 | `Send(id, message, mode)` | `Send(message, mode)` |
| 注册类型化处理器 | `RegisterHandler<T>((sender, msg, mode) => …)` | `RegisterHandler<T>((msg, mode) => …)` |
| 原始字节 | `Send(id, bytes, mode)`、`DataReceived(id, bytes, mode)` | `Send(bytes, mode)`、`DataReceived(bytes, mode)` |

消息类型定义为实现 `INetworkMessage` 的非泛型结构体。Unity 编译时的 Weaver 会为支持的字段生成序列化代码，包括布尔值、整数、浮点数、枚举和字符串。两端保持消息类型全名和字段布局一致，接收端提前注册处理器。原始载荷使用非空的 `ArraySegment<byte>`。消息默认使用 `DeliveryMode.Reliable`，可显式选择 `DeliveryMode.Unreliable`；Ghost 复制使用可靠通道。

## 用 Ghost 同步状态

以仓库里的玩家血量为例，`PlayerState` 继承 `GhostComponent`，用 `[GhostField]` 标记 `Hp`。模板通过 `new GhostBuilder(1).Component<PlayerState>().Build()` 定义。两端必须使用相同的模板 ID、组件顺序和字段布局。客户端在收包前注册模板；服务端首次 `Spawn` 时会自动注册模板。[玩家示例](Examples/Test.cs)

```csharp
// 客户端初始化。Spawned 触发时可以读取完整的初始值。
client.ReplicateManager.RegisterTemplate(PlayerExample.Template);
client.ReplicateManager.Spawned += ghost => {
    if (ghost.TryGetComponent<PlayerState>(out var state))
        Debug.Log($"玩家 {ghost.NetId} 的初始血量为 {state.Hp}");
};

// 服务端创建玩家。直接修改同步字段，下一次 TickOutgoing 会采集变化。
var player = server.ReplicateManager.Spawn(PlayerExample.Template);
player.GetComponent<PlayerState>().Hp = 100;
```

| 需求 | API 与行为 |
|---|---|
| 定位实例或读取组件 | `NetId` 标识运行时实例，`TemplateId` 标识模板。客户端用 `TryGetGhost(netId, out ghost)` 查找；两端用 `GetComponent<T>()` 或 `TryGetComponent<T>(out value)` 读取组件。 |
| 响应客户端变化 | 在 `client.ReplicateManager` 上订阅 `Spawned`、`Updated`、`Despawned`、`OwnershipChanged`。断线时管理器会清空本地对象；场景表现可在 `OnDisconnected` 里统一清理。 |
| 设置归属与校验操作 | 服务端用 `Spawn(template, ownerConnectionId)` 或 `SetOwner(netId, id)` 指定所有者，传 `null` 可解除归属。处理客户端请求时，用 `TryGetOwnedGhost(sender, netId, out ghost)` 校验发送者。服务端可用 `TryGetOwner` 查询，客户端可用 `IsOwned` 查询本地归属。 |
| 分开实现两端行为 | 组件按需实现 `IRequiresServerImplementation` 或 `IRequiresClientImplementation`。在对应端注册模板前用 `server.Bind(...)` 或 `client.Bind(...)` 注册工厂，之后用 `component.GetImplementation<T>()` 取本地实现。见[服务端购买处理](Examples/Inventory.Server.cs)与[客户端请求](Examples/Inventory.Client.cs)。 |
| 自定义组件序列化 | 常规字段使用 `[GhostField]`，自动跟踪每组件最多 64 个字段（含继承字段）。需要自定义格式时可覆写 `Serialize` 和 `Deserialize`。自动字段同步方向为服务端到客户端，客户端修改请求通过消息发送。 |

## 子 Ghost：只把部分状态发给队友

把公开血量放在玩家模板里，把队伍战术数据放在独立的子模板里。客户端提前注册两个模板。下面的代码运行在服务端，`connectionId` 是从连接事件获得的玩家连接 ID。

```csharp
var team = new System.Collections.Generic.HashSet<int>();
var ghosts = server.ReplicateManager;
var player = ghosts.Spawn(playerTemplate);
var tactics = ghosts.SpawnChild(player, tacticsTemplate, visibleTo: team.Contains);

// 入队和退队分别发生在各自的业务事件中。
team.Add(connectionId);     // 下次同步时收到战术数据的完整当前值
// …玩家稍后离队…
team.Remove(connectionId);  // 下次同步时该客户端移除 tactics，保留 player
```

每次发送时，服务端按连接检查可见性。子对象要求所有祖先可见，并通过自身的 `visibleTo` 条件。省略条件时继承父级范围。归属与可见性独立：指定所有者后，如需仅本人可见，还要设置过滤条件。可用 `SetVisibility(netId, predicate)` 修改任意 Ghost 的条件，传 `null` 清除该对象自身的限制。

客户端通过 `ghost.Parent` 访问父对象，根对象的 `Parent` 为 `null`。首次可见时先创建父对象，再创建子对象；重新可见时重新接收完整状态。服务端调用 `Despawn(player.NetId)` 会级联销毁后代，客户端先收到子对象的移除通知。可运行的双客户端示例见 [GhostBindingTests.cs](Tests/GhostBindingTests.cs) 中的 `TeamOnlyChildFollowsMembershipAndParentVisibilityOverLoopback`。
