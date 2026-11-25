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
#include <windows.h>
#include <string>
#include <vector>

namespace ProcBridge::Inject
{
    constexpr ULONG InitBlockMagic = 0x42495250; // "PRIB"
    constexpr ULONG InitBlockVersion = 1;

    struct InitBlockHeader
    {
        ULONG Magic;
        ULONG Version;
        ULONG TotalBytes;
        ULONG EnvironmentOffset;
        ULONG EnvironmentChars;
        ULONG HandleOffset;
        ULONG HandleCount;
        ULONG Reserved;
    };

    /// A handle of the parent that refers to a virtual file. Handle values are the same in a child
    /// that inherits them; Mapping is an index into the PROCBRIDGE_MAP list, which the child parses in
    /// the same order.
    struct InheritedHandle
    {
        ULONGLONG Handle;
        LONG Mapping;
        ULONG Reserved;
    };

    /// Builds a block. environment is "NAME=VALUE\0NAME=VALUE\0" (each entry NUL-terminated).
    std::vector<BYTE> BuildInitBlock(const std::wstring& environment, const std::vector<InheritedHandle>& handles);

    /// Validates a block of blockBytes bytes. On success environment points at the first entry
    /// (environmentChars characters) and handles at handleCount entries.
    bool ParseInitBlock(const void* block, size_t blockBytes, const wchar_t*& environment, size_t& environmentChars,
        const InheritedHandle*& handles, size_t& handleCount) noexcept;
}
