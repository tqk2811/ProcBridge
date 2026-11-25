#pragma once
// Initialization block passed to ProcBridgeHookInitialize (plan 5): the injector copies it into the
// target and passes its address as the initializer's parameter. It carries the configuration
// environment variables (so they reach a child even when its creator passed its own environment
// block) and the virtual handles the child inherits from its parent.
//
// Layout (every offset from the start of the block, all fields little endian, same on x86 and x64):
//   InitBlockHeader
//   wchar_t environment["NAME=VALUE\0NAME=VALUE\0"]  (EnvironmentChars characters)
//   InheritedHandle handles[HandleCount]             (8-byte aligned)
//   BYTE extra[ExtraBytes]                           (8-byte aligned; per-handle records of the hook)
#include <windows.h>
#include <string>
#include <vector>

namespace ProcBridge::Inject
{
    constexpr ULONG InitBlockMagic = 0x42495250; // "PRIB"
    constexpr ULONG InitBlockVersion = 2;
    constexpr ULONG NoExtra = 0xFFFFFFFF;

    struct InitBlockHeader
    {
        ULONG Magic;
        ULONG Version;
        ULONG TotalBytes;
        ULONG EnvironmentOffset;
        ULONG EnvironmentChars;
        ULONG HandleOffset;
        ULONG HandleCount;
        ULONG ExtraOffset;
        ULONG ExtraBytes;
        ULONG Reserved;
    };

    /// A handle of the parent that refers to a virtual file. Handle values are the same in a child
    /// that inherits them; Mapping is an index into the PROCBRIDGE_MAP list, which the child parses in
    /// the same order (with the Broker: the roots of the Hello reply, the same for every process of
    /// the session). ExtraOffset locates the handle's record in the extra area (NoExtra when none); its
    /// format belongs to the hook (machine B's metadata and virtual path of a remote handle).
    struct InheritedHandle
    {
        ULONGLONG Handle;
        LONG Mapping;
        ULONG ExtraOffset;
    };

    /// Builds a block. environment is "NAME=VALUE\0NAME=VALUE\0" (each entry NUL-terminated); extra is
    /// the area the ExtraOffset of the handles points into.
    std::vector<BYTE> BuildInitBlock(const std::wstring& environment, const std::vector<InheritedHandle>& handles,
        const std::vector<BYTE>& extra = {});

    /// Validates a block of blockBytes bytes. On success environment points at the first entry
    /// (environmentChars characters), handles at handleCount entries and extra at extraBytes bytes.
    /// The ExtraOffset of each handle is not checked: the reader bounds its record by extraBytes.
    bool ParseInitBlock(const void* block, size_t blockBytes, const wchar_t*& environment, size_t& environmentChars,
        const InheritedHandle*& handles, size_t& handleCount, const BYTE*& extra, size_t& extraBytes) noexcept;
}
