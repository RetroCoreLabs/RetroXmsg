# RetroXmsg

Talk to a Norsk Data machine. Two applications built on the XMSG packages from
[RetroIO](https://retrocorelabs.github.io/retroio/): a hub that links XMSG nodes over TCP, and a
runner that drives the C# XMSG stack against a real SINTRAN III machine - file transfer, folder
synchronisation, chat and terminal sessions.

The source, the build instructions and the releases are on
[GitHub](https://github.com/RetroCoreLabs/RetroXmsg).

## Get started

```powershell
# from the repository root
.\src\build.ps1
src\publish\win-x64\xmsghub.exe --port 5010
src\publish\win-x64\xmsgrunner.exe --self 103 --for 60
```

## Where to look

 - **`xmsghub`** (`RetroXmsg.Hub`): links XMSG nodes over TCP; hubs can chain.
 - **`xmsgrunner`** (`RetroXmsg.Runner`): drives the stack against a real machine - push, pull,
   sync, chat and relay.

These are applications, so this reference covers the types they expose to each other and to
their tests. The protocol itself - the codec, the node and the servers - is documented with the
`RetroIO.Protocols.Xmsg.*` packages in the
[RetroIO API reference](https://retrocorelabs.github.io/retroio/).

See the [API reference](api/index.md) for every namespace, type and member.
