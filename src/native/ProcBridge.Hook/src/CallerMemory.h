#pragma once
// Caller memory: hooks read and write pointers that the kernel would probe and answer with STATUS_ACCESS_VIOLATION.
// Every access to caller memory goes through these SEH-guarded copies (no C++ objects in them, so __try is allowed).
#include "NtApi.h"
#include <cstring>

namespace ProcBridge::Hook
{
    inline int AccessFilter(DWORD code) noexcept
    {
        return code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_DATATYPE_MISALIGNMENT || code == EXCEPTION_IN_PAGE_ERROR
            ? EXCEPTION_EXECUTE_HANDLER
            : EXCEPTION_CONTINUE_SEARCH;
    }

    inline bool SafeCopy(void* destination, const void* source, size_t bytes) noexcept
    {
        __try
        {
            memcpy(destination, source, bytes);
            return true;
        }
        __except (AccessFilter(GetExceptionCode()))
        {
            return false;
        }
    }

    /// Writes the IO_STATUS_BLOCK of a hook that answered by itself.
    inline bool SafeSetIoStatus(PIO_STATUS_BLOCK ioStatusBlock, NTSTATUS status, ULONG_PTR information) noexcept
    {
        IO_STATUS_BLOCK value{};
        value.Status = status;
        value.Information = information;
        return SafeCopy(ioStatusBlock, &value, sizeof(value));
    }
}
