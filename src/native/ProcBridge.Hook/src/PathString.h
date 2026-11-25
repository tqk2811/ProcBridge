#pragma once
// Heap-backed UNICODE_STRING helpers. No CRT allocation: everything goes to the process heap so the
// helpers stay usable inside hooks (plan 4.6).
#include "NtApi.h"

namespace ProcBridge::Hook
{
    /// Allocates an empty string with room for byteCapacity bytes plus a terminating NUL.
    bool AllocateString(UNICODE_STRING& value, size_t byteCapacity) noexcept;

    /// Frees a string allocated by this module and clears it.
    void FreeString(UNICODE_STRING& value) noexcept;

    /// value = head + tail. tail may be empty.
    bool ConcatString(UNICODE_STRING& value, const UNICODE_STRING& head, const wchar_t* tail, size_t tailBytes) noexcept;

    /// True when text starts with prefix (case-insensitive) on a path boundary: the next character
    /// after prefix is '\' or the end of text.
    bool HasPathPrefix(const wchar_t* text, size_t textBytes, const UNICODE_STRING& prefix) noexcept;

    /// Writes "to" + text[from.Length..] into destination when text has the path prefix "from".
    /// Returns false (destination untouched) when the prefix does not match or the result does not fit.
    bool ReplacePathPrefix(const wchar_t* text, size_t textBytes, const UNICODE_STRING& from, const UNICODE_STRING& to,
        wchar_t* destination, size_t destinationCapacityBytes, size_t& resultBytes) noexcept;

    /// RAII process-heap buffer.
    class HeapBuffer final
    {
    public:
        explicit HeapBuffer(size_t bytes) noexcept;
        ~HeapBuffer();
        HeapBuffer(const HeapBuffer&) = delete;
        HeapBuffer& operator=(const HeapBuffer&) = delete;

        BYTE* Get() const noexcept { return _data; }
        explicit operator bool() const noexcept { return _data != nullptr; }

    private:
        BYTE* _data;
    };
}
