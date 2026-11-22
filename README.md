# MiniNet

MiniNet 是面向 Unity 的轻量级、服务端权威网络框架，底层使用 KCP。客户端通过类型化消息提交操作，服务端通过 Ghost（可复制的游戏对象）把状态同步给指定玩家。例如，购买药水是一条消息，购买后的金币和库存是 Ghost 上的字段。

[快速开始与 API 指南](Assets/MiniNet/README.md) · [业务示例](Assets/MiniNet/Examples) · [复制测试](Assets/MiniNet/Tests/GhostBindingTests.cs)

## 只向队友同步战术数据

假设附近所有玩家都能看到角色，但只有队友能看到角色的战术目标。在服务端，把公开状态放在父 Ghost，把战术数据放在子 Ghost，并用队伍成员表控制子对象的可见性。

```csharp
var ghosts = server.ReplicateManager;
var player = ghosts.Spawn(playerTemplate);
var teammates = new HashSet<int>(); // 服务端连接 ID
var tactics = ghosts.SpawnChild(player, tacticsTemplate, visibleTo: teammates.Contains);
```

玩家入队时调用 `teammates.Add(connectionId)`。如果该玩家能看到父对象，下一次同步就会收到战术数据的完整当前值。退队时调用 `teammates.Remove(connectionId)`，该客户端会移除战术数据，仍可继续接收公开的角色状态。

## 在 Unity 中试用

1. 使用 Unity Hub 打开仓库根目录。项目记录的编辑器版本是 `2022.3.57f1c2`。
2. 将[快速开始里的脚本](Assets/MiniNet/README.md#快速开始本机收发一条消息)保存为 `MiniNetDemo.cs`，挂到场景中的 GameObject 上。如果脚本所在程序集使用 asmdef，添加对 `MiniNet` 的引用。
3. 进入 Play 模式。示例会在本机启动服务端和客户端，连接成功后可在 Unity Console 看到服务端收到的“你好”。

接入自己的游戏循环时，先调用 `TickIncoming()` 接收消息，再运行游戏逻辑，最后调用 `TickOutgoing()` 发送本帧的消息和状态变化。

## 从示例查 API

| 想做什么 | 从哪里看 |
| --- | --- |
| 定义消息、注册处理器、选择可靠或不可靠投递 | [连接与消息 API](Assets/MiniNet/README.md#连接与消息-api) |
| 定义玩家模板并同步血量 | [PlayerState 与 SpawnPlayer](Assets/MiniNet/Examples/Test.cs) |
| 发送购买请求、验证归属、更新背包 | [客户端请求](Assets/MiniNet/Examples/Inventory.Client.cs) · [服务端处理](Assets/MiniNet/Examples/Inventory.Server.cs) |
| 验证入队、退队及父子对象的生命周期 | [双客户端回环测试](Assets/MiniNet/Tests/GhostBindingTests.cs) |

传输层与编译期代码生成使用的第三方库及其许可见[依赖说明](Assets/MiniNet/ThirdParty/README.md)。
