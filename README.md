# ProcBridge

Run a process (and all of its child processes) on machine A while it works on a folder that lives on
machine B, over LAN or the internet, as if that folder were local. Only the hooked process tree sees
the virtual folder; everything else on machine A is untouched.

Status: M1 local hook spike. `ProcBridge.Hook` redirects a virtual folder to another local folder
inside one process; there is no network yet. Code and build system live in `src/`. See
[docs/Plan-vi.md](docs/Plan-vi.md) and [docs/Backlog-vi.md](docs/Backlog-vi.md); packaging and releases: [docs/Release-vi.md](docs/Release-vi.md) (Vietnamese).

Try it (after building `src/ProcBridge.Native.sln`):

```text
src\native\out\x64\Release\ProcBridge.Spawn.x64.exe --map D:\Virtual D:\Real -- cmd /c dir D:\Virtual
```
