#include "PathString.h"
#include <cstring>

namespace ProcBridge::Hook
{
    bool AllocateString(UNICODE_STRING& value, size_t byteCapacity) noexcept
    {
        value = {};
        if (byteCapacity > 0xFFFC)
        {
            return false;
        }
        auto* buffer = static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), 0, byteCapacity + sizeof(wchar_t)));
        if (buffer == nullptr)
        {
            return false;
        }
        buffer[0] = L'\0';
        value.Buffer = buffer;
        value.Length = 0;
        value.MaximumLength = static_cast<USHORT>(byteCapacity + sizeof(wchar_t));
        return true;
    }

    void FreeString(UNICODE_STRING& value) noexcept
    {
        if (value.Buffer != nullptr)
        {
            HeapFree(GetProcessHeap(), 0, value.Buffer);
        }
        value = {};
    }

    bool ConcatString(UNICODE_STRING& value, const UNICODE_STRING& head, const wchar_t* tail, size_t tailBytes) noexcept
    {
        if (!AllocateString(value, static_cast<size_t>(head.Length) + tailBytes))
        {
            return false;
        }
        memcpy(value.Buffer, head.Buffer, head.Length);
        if (tailBytes > 0)
        {
            memcpy(reinterpret_cast<BYTE*>(value.Buffer) + head.Length, tail, tailBytes);
        }
        value.Length = static_cast<USHORT>(head.Length + tailBytes);
        value.Buffer[value.Length / sizeof(wchar_t)] = L'\0';
        return true;
    }

    bool HasPathPrefix(const wchar_t* text, size_t textBytes, const UNICODE_STRING& prefix) noexcept
    {
        if (prefix.Length == 0 || textBytes < prefix.Length || text == nullptr)
        {
            return false;
        }
        const size_t count = prefix.Length / sizeof(wchar_t);
        for (size_t i = 0; i < count; ++i)
        {
            if (text[i] != prefix.Buffer[i] && RtlUpcaseUnicodeChar(text[i]) != RtlUpcaseUnicodeChar(prefix.Buffer[i]))
            {
                return false;
            }
        }
        // ':' starts a stream name of the prefix itself ("<root>:stream", "<root>::$INDEX_ALLOCATION").
        return textBytes == prefix.Length || text[count] == L'\\' || text[count] == L':';
    }

    bool ReplacePathPrefix(const wchar_t* text, size_t textBytes, const UNICODE_STRING& from, const UNICODE_STRING& to,
        wchar_t* destination, size_t destinationCapacityBytes, size_t& resultBytes) noexcept
    {
        if (!HasPathPrefix(text, textBytes, from))
        {
            return false;
        }
        const size_t tailBytes = textBytes - from.Length;
        const size_t total = static_cast<size_t>(to.Length) + tailBytes;
        if (total > destinationCapacityBytes)
        {
            return false;
        }
        // text and destination may not overlap in callers; copy the tail first anyway for safety.
        memmove(reinterpret_cast<BYTE*>(destination) + to.Length, reinterpret_cast<const BYTE*>(text) + from.Length, tailBytes);
        memcpy(destination, to.Buffer, to.Length);
        resultBytes = total;
        return true;
    }

    HeapBuffer::HeapBuffer(size_t bytes) noexcept
        : _data(static_cast<BYTE*>(HeapAlloc(GetProcessHeap(), 0, bytes)))
    {
    }

    HeapBuffer::~HeapBuffer()
    {
        if (_data != nullptr)
        {
            HeapFree(GetProcessHeap(), 0, _data);
        }
    }
}
