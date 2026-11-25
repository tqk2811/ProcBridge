#pragma once
// PS_ATTRIBUTE_LIST of NtCreateUserProcess (built by kernelbase from PROC_THREAD_ATTRIBUTE_* and the image name).
#include "NtApi.h"

namespace ProcBridge::Hook::Ps
{
    // The attribute number is the low 16 bits; the high bits are thread/input/additive flags.
    constexpr ULONG_PTR AttributeNumberMask = 0x0000FFFF;
    constexpr ULONG_PTR AttributeThreadFlag = 0x00010000;
    constexpr ULONG_PTR AttributeParentProcess = 0; // PS_ATTRIBUTE_PARENT_PROCESS (0x60000)
    constexpr ULONG_PTR AttributeImageName = 5;     // PS_ATTRIBUTE_IMAGE_NAME (0x20005): Value = PWSTR NT path, Size = bytes
    constexpr ULONG_PTR AttributeHandleList = 11;   // PS_ATTRIBUTE_HANDLE_LIST (0x2000B)

    struct Attribute
    {
        ULONG_PTR Attribute;
        SIZE_T Size;
        ULONG_PTR Value;
        PSIZE_T ReturnLength;
    };

    struct AttributeList
    {
        SIZE_T TotalLength;
        Attribute Attributes[1];
    };

    // RTL_USER_PROCESS_PARAMETERS.Flags: string buffers are pointers (not offsets from the block).
    constexpr ULONG ProcessParametersNormalized = 0x00000001;
}
