# Third-party dependencies

- **kcp2k 1.41** comes from the project's local Mirror distribution. MiniNet builds it as `MiniNet.Kcp2k` and uses the same name for its C# namespace. The original license and version history are in `Kcp2k`.
- **Mono.CecilX 0.11.4** comes from the project's local Mirror distribution. The copy in `Cecil` has the assembly name `MiniNet.Cecil` and the original strong-name signature was removed so it has its own identity. Unity imports it as an Editor-only dependency explicitly referenced by the MiniNet Weaver. The original license is included in `Cecil`.
