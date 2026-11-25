#include "PathMapper.h"
#include "PathString.h"

namespace ProcBridge::Hook
{
    namespace
    {
        // "\??\X:": index of the first character after the drive letter colon.
        constexpr USHORT DriveRelativeStart = 6;

        bool IsDriveLetterNtPath(const UNICODE_STRING& path) noexcept
        {
            return path.Length >= DriveRelativeStart * sizeof(wchar_t)
                && path.Buffer[0] == L'\\' && path.Buffer[1] == L'?' && path.Buffer[2] == L'?' && path.Buffer[3] == L'\\'
                && path.Buffer[5] == L':';
        }

        // View into path after "\??\X:" (no allocation).
        UNICODE_STRING VolumeRelativeView(const UNICODE_STRING& path) noexcept
        {
            UNICODE_STRING view{};
            if (IsDriveLetterNtPath(path))
            {
                view.Buffer = path.Buffer + DriveRelativeStart;
                view.Length = static_cast<USHORT>(path.Length - DriveRelativeStart * sizeof(wchar_t));
                view.MaximumLength = view.Length;
            }
            return view;
        }

        // \Device\HarddiskVolumeN + volume-relative part; empty when the drive letter is unknown.
        void BuildDevicePath(const UNICODE_STRING& ntPath, const UNICODE_STRING& volumeRelative, UNICODE_STRING& devicePath) noexcept
        {
            devicePath = {};
            if (!IsDriveLetterNtPath(ntPath))
            {
                return;
            }
            const wchar_t drive[3] = { ntPath.Buffer[4], L':', L'\0' };
            wchar_t device[MAX_PATH];
            const DWORD written = QueryDosDeviceW(drive, device, MAX_PATH);
            if (written == 0)
            {
                return;
            }
            UNICODE_STRING head{};
            head.Buffer = device;
            head.Length = static_cast<USHORT>(lstrlenW(device) * sizeof(wchar_t));
            head.MaximumLength = head.Length;
            if (!ConcatString(devicePath, head, volumeRelative.Buffer, volumeRelative.Length))
            {
                devicePath = {};
            }
        }

        DWORD ToNtPath(const wchar_t* dosPath, UNICODE_STRING& ntPath) noexcept
        {
            UNICODE_STRING converted{};
            if (!RtlDosPathNameToNtPathName_U(dosPath, &converted, nullptr, nullptr))
            {
                return ERROR_BAD_PATHNAME;
            }
            USHORT length = converted.Length;
            while (length > DriveRelativeStart * sizeof(wchar_t) && converted.Buffer[length / sizeof(wchar_t) - 1] == L'\\')
            {
                length -= sizeof(wchar_t);
            }
            converted.Length = length;
            const bool copied = ConcatString(ntPath, converted, nullptr, 0);
            RtlFreeUnicodeString(&converted);
            return copied ? ERROR_SUCCESS : ERROR_NOT_ENOUGH_MEMORY;
        }
    }

    DWORD PathMapper::LoadFromEnvironment() noexcept
    {
        constexpr DWORD capacity = 8192;
        HeapBuffer buffer(capacity * sizeof(wchar_t));
        if (!buffer)
        {
            return ERROR_NOT_ENOUGH_MEMORY;
        }
        auto* text = reinterpret_cast<wchar_t*>(buffer.Get());
        const DWORD length = GetEnvironmentVariableW(EnvironmentVariable, text, capacity);
        if (length == 0)
        {
            return GetLastError() == ERROR_ENVVAR_NOT_FOUND ? ERROR_SUCCESS : GetLastError();
        }
        if (length >= capacity)
        {
            return ERROR_INSUFFICIENT_BUFFER;
        }

        wchar_t* entry = text;
        while (entry != nullptr && *entry != L'\0')
        {
            wchar_t* next = entry;
            while (*next != L'\0' && *next != L'|')
            {
                ++next;
            }
            wchar_t* following = nullptr;
            if (*next == L'|')
            {
                *next = L'\0';
                following = next + 1;
            }

            wchar_t* separator = entry;
            while (*separator != L'\0' && *separator != L'*')
            {
                ++separator;
            }
            if (*separator != L'*')
            {
                return ERROR_INVALID_PARAMETER;
            }
            *separator = L'\0';
            const DWORD result = AddMapping(entry, separator + 1);
            if (result != ERROR_SUCCESS)
            {
                return result;
            }
            entry = following;
        }
        return ERROR_SUCCESS;
    }

    DWORD PathMapper::AddMapping(const wchar_t* virtualDos, const wchar_t* realDos) noexcept
    {
        if (_count >= MaxMappings)
        {
            return ERROR_TOO_MANY_NAMES;
        }
        PathMapping& mapping = _mappings[_count];
        DWORD result = ToNtPath(virtualDos, mapping.VirtualNt);
        if (result == ERROR_SUCCESS)
        {
            result = ToNtPath(realDos, mapping.RealNt);
        }
        if (result != ERROR_SUCCESS)
        {
            FreeString(mapping.VirtualNt);
            FreeString(mapping.RealNt);
            return result;
        }
        mapping.VirtualVolumeRelative = VolumeRelativeView(mapping.VirtualNt);
        mapping.RealVolumeRelative = VolumeRelativeView(mapping.RealNt);
        BuildDevicePath(mapping.RealNt, mapping.RealVolumeRelative, mapping.RealDevicePath);
        BuildDevicePath(mapping.VirtualNt, mapping.VirtualVolumeRelative, mapping.VirtualDevicePath);
        if (mapping.VirtualDevicePath.Length == 0 && mapping.RealDevicePath.Length != 0)
        {
            // Virtual drive letter does not exist (e.g. V:\): keep the real volume device so that
            // GetFinalPathNameByHandle still resolves. Known gap, see docs/Backlog-vi.md.
            BuildDevicePath(mapping.RealNt, mapping.VirtualVolumeRelative, mapping.VirtualDevicePath);
        }
        ++_count;
        return ERROR_SUCCESS;
    }

    int PathMapper::FindMapping(const UNICODE_STRING& ntPath) const noexcept
    {
        for (int i = 0; i < _count; ++i)
        {
            if (HasPathPrefix(ntPath.Buffer, ntPath.Length, _mappings[i].VirtualNt))
            {
                return i;
            }
        }
        return -1;
    }

    bool PathMapper::TranslateToReal(int mapping, const UNICODE_STRING& ntPath, UNICODE_STRING& realPath) const noexcept
    {
        const PathMapping& entry = _mappings[mapping];
        const size_t tailBytes = static_cast<size_t>(ntPath.Length) - entry.VirtualNt.Length;
        return ConcatString(realPath, entry.RealNt,
            reinterpret_cast<const wchar_t*>(reinterpret_cast<const BYTE*>(ntPath.Buffer) + entry.VirtualNt.Length), tailBytes);
    }

    bool PathMapper::IsNearVirtualRoot(const UNICODE_STRING& directoryDevicePath) const noexcept
    {
        for (int i = 0; i < _count; ++i)
        {
            const UNICODE_STRING& root = _mappings[i].VirtualDevicePath;
            if (HasPathPrefix(root.Buffer, root.Length, directoryDevicePath)
                || HasPathPrefix(directoryDevicePath.Buffer, directoryDevicePath.Length, root))
            {
                return true;
            }
        }
        return false;
    }

    int PathMapper::FindMappingByDevicePath(const UNICODE_STRING& devicePath) const noexcept
    {
        int best = -1;
        for (int i = 0; i < _count; ++i)
        {
            const UNICODE_STRING& root = _mappings[i].VirtualDevicePath;
            if (HasPathPrefix(devicePath.Buffer, devicePath.Length, root)
                && (best < 0 || root.Length > _mappings[best].VirtualDevicePath.Length))
            {
                best = i;
            }
        }
        return best;
    }

    bool PathMapper::TranslateDeviceToReal(int mapping, const UNICODE_STRING& devicePath, UNICODE_STRING& realPath) const noexcept
    {
        const PathMapping& entry = _mappings[mapping];
        const size_t tailBytes = static_cast<size_t>(devicePath.Length) - entry.VirtualDevicePath.Length;
        return ConcatString(realPath, entry.RealNt,
            reinterpret_cast<const wchar_t*>(reinterpret_cast<const BYTE*>(devicePath.Buffer) + entry.VirtualDevicePath.Length), tailBytes);
    }
}
