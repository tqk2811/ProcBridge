# ProcBridge

Run a process (and all of its child processes) on machine A while it works on a folder that lives on
machine B, over LAN or the internet, as if that folder were local. Only the hooked process tree sees
the virtual folder; everything else on machine A is untouched.

Status: M0 skeleton (empty projects, build + CI). Code and build system live in `src/`. See [docs/Plan-vi.md](docs/Plan-vi.md) (Vietnamese).
