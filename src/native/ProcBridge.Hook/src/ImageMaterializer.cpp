#include "ImageMaterializer.h"
#include "PathString.h"
#include <algorithm>
#include <cwctype>

namespace ProcBridge::Hook
{
    namespace
    {
        constexpr wchar_t NtPrefix[] = L"\\??\\";
        constexpr size_t NtPrefixChars = 4;
        constexpr size_t MaxImportDescriptors = 1024;
        constexpr size_t MaxImportNameChars = 260;

        /// "\??\C:\x" -> "\\?\C:\x" (a Win32 path that skips the DOS name parsing).
        std::wstring Win32Path(const std::wstring& ntPath)
        {
            return L"\\\\?\\" + ntPath.substr(NtPrefixChars);
        }

        /// "\??\C:\x" -> "C:\x", "\??\UNC\server\share\x" -> "\\server\share\x".
        std::wstring DosPath(const std::wstring& ntPath)
        {
            std::wstring rest = ntPath.substr(NtPrefixChars);
            if (rest.size() > 4 && _wcsnicmp(rest.c_str(), L"UNC\\", 4) == 0)
            {
                return L"\\" + rest.substr(3);
            }
            return rest;
        }

        std::wstring Directory(const std::wstring& path)
        {
            const size_t slash = path.find_last_of(L'\\');
            return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash);
        }

        /// API set names (api-ms-*, ext-ms-*) are resolved by the loader's schema, never as files.
        bool IsApiSetName(const std::wstring& name)
        {
            return name.size() > 4 && (_wcsnicmp(name.c_str(), L"api-", 4) == 0 || _wcsnicmp(name.c_str(), L"ext-", 4) == 0);
        }

        bool ContainsName(const std::vector<std::wstring>& names, const std::wstring& name)
        {
            return std::any_of(names.begin(), names.end(), [&name](const std::wstring& x) { return _wcsicmp(x.c_str(), name.c_str()) == 0; });
        }

        /// Reads the import table of a PE file (PE32 or PE32+, any machine) with positional reads.
        class PeImportReader final
        {
        public:
            explicit PeImportReader(const std::wstring& ntPath)
                : _file(CreateFileW(Win32Path(ntPath).c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr))
            {
            }

            ~PeImportReader()
            {
                if (_file != INVALID_HANDLE_VALUE)
                {
                    CloseHandle(_file);
                }
            }

            PeImportReader(const PeImportReader&) = delete;
            PeImportReader& operator=(const PeImportReader&) = delete;

            /// Names of the DLLs in the import directory (no delay-load imports). Empty when the file is not a PE image.
            std::vector<std::wstring> ReadImportNames()
            {
                std::vector<std::wstring> names;
                if (_file == INVALID_HANDLE_VALUE || !ReadHeaders())
                {
                    return names;
                }
                ULONGLONG offset = 0;
                if (_importRva == 0 || !RvaToOffset(_importRva, offset))
                {
                    return names;
                }
                for (size_t i = 0; i < MaxImportDescriptors; ++i)
                {
                    IMAGE_IMPORT_DESCRIPTOR descriptor{};
                    if (!Read(offset + i * sizeof(descriptor), &descriptor, sizeof(descriptor)) || descriptor.Name == 0)
                    {
                        break;
                    }
                    ULONGLONG nameOffset = 0;
                    std::wstring name;
                    if (RvaToOffset(descriptor.Name, nameOffset) && ReadName(nameOffset, name) && !name.empty())
                    {
                        names.push_back(std::move(name));
                    }
                }
                return names;
            }

        private:
            bool Read(ULONGLONG offset, void* buffer, DWORD bytes) const noexcept
            {
                OVERLAPPED position{};
                position.Offset = static_cast<DWORD>(offset);
                position.OffsetHigh = static_cast<DWORD>(offset >> 32);
                DWORD read = 0;
                return ReadFile(_file, buffer, bytes, &read, &position) && read == bytes;
            }

            bool ReadHeaders()
            {
                IMAGE_DOS_HEADER dos{};
                if (!Read(0, &dos, sizeof(dos)) || dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew <= 0)
                {
                    return false;
                }
                const ULONGLONG ntOffset = static_cast<ULONGLONG>(dos.e_lfanew);
                DWORD signature = 0;
                IMAGE_FILE_HEADER fileHeader{};
                WORD magic = 0;
                const ULONGLONG optionalOffset = ntOffset + sizeof(signature) + sizeof(fileHeader);
                if (!Read(ntOffset, &signature, sizeof(signature)) || signature != IMAGE_NT_SIGNATURE
                    || !Read(ntOffset + sizeof(signature), &fileHeader, sizeof(fileHeader)) || !Read(optionalOffset, &magic, sizeof(magic)))
                {
                    return false;
                }
                if (magic == IMAGE_NT_OPTIONAL_HDR32_MAGIC)
                {
                    IMAGE_OPTIONAL_HEADER32 optional{};
                    if (fileHeader.SizeOfOptionalHeader < sizeof(optional) || !Read(optionalOffset, &optional, sizeof(optional)))
                    {
                        return false;
                    }
                    _sizeOfHeaders = optional.SizeOfHeaders;
                    _importRva = optional.NumberOfRvaAndSizes > IMAGE_DIRECTORY_ENTRY_IMPORT
                        ? optional.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress : 0;
                }
                else if (magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC)
                {
                    IMAGE_OPTIONAL_HEADER64 optional{};
                    if (fileHeader.SizeOfOptionalHeader < sizeof(optional) || !Read(optionalOffset, &optional, sizeof(optional)))
                    {
                        return false;
                    }
                    _sizeOfHeaders = optional.SizeOfHeaders;
                    _importRva = optional.NumberOfRvaAndSizes > IMAGE_DIRECTORY_ENTRY_IMPORT
                        ? optional.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress : 0;
                }
                else
                {
                    return false;
                }
                _sections.resize(fileHeader.NumberOfSections);
                const DWORD sectionBytes = static_cast<DWORD>(_sections.size() * sizeof(IMAGE_SECTION_HEADER));
                return sectionBytes == 0 || Read(optionalOffset + fileHeader.SizeOfOptionalHeader, _sections.data(), sectionBytes);
            }

            bool RvaToOffset(DWORD rva, ULONGLONG& offset) const noexcept
            {
                if (rva < _sizeOfHeaders)
                {
                    offset = rva;
                    return true;
                }
                for (const IMAGE_SECTION_HEADER& section : _sections)
                {
                    const DWORD size = std::max(section.Misc.VirtualSize, section.SizeOfRawData);
                    if (rva >= section.VirtualAddress && rva - section.VirtualAddress < size)
                    {
                        offset = static_cast<ULONGLONG>(section.PointerToRawData) + (rva - section.VirtualAddress);
                        return true;
                    }
                }
                return false;
            }

            /// ASCII name up to its NUL (or MaxImportNameChars) at offset.
            bool ReadName(ULONGLONG offset, std::wstring& name) const
            {
                char text[MaxImportNameChars]{};
                DWORD read = 0;
                OVERLAPPED position{};
                position.Offset = static_cast<DWORD>(offset);
                position.OffsetHigh = static_cast<DWORD>(offset >> 32);
                if (!ReadFile(_file, text, sizeof(text) - 1, &read, &position) || read == 0)
                {
                    return false;
                }
                const size_t length = strnlen(text, read);
                if (length == read && read == sizeof(text) - 1)
                {
                    return false; // longer than any file name
                }
                name.assign(text, text + length);
                // A name with a path is resolved by the loader as such, not next to the image.
                return name.find_first_of(L"\\/:") == std::wstring::npos;
            }

            HANDLE _file;
            DWORD _sizeOfHeaders = 0;
            DWORD _importRva = 0;
            std::vector<IMAGE_SECTION_HEADER> _sections;
        };

        bool FileExists(const std::wstring& ntPath)
        {
            const DWORD attributes = GetFileAttributesW(Win32Path(ntPath).c_str());
            return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
        }
    }

    NTSTATUS ImageMaterializer::Bring(int mapping, const std::wstring& virtualNt, std::wstring& realNt) const
    {
        UNICODE_STRING path{};
        path.Buffer = const_cast<wchar_t*>(virtualNt.c_str());
        path.Length = static_cast<USHORT>(virtualNt.size() * sizeof(wchar_t));
        path.MaximumLength = path.Length;
        // The access of an image section; a read-only open never gets a write token.
        const OpenIntent intent{ FILE_READ_DATA | FILE_EXECUTE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_OPEN,
            FILE_NON_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT, 0, FILE_SHARE_READ | FILE_SHARE_DELETE };
        UNICODE_STRING real{};
        HookIpc::FileInfo info{};
        NTSTATUS status = _resolver.Resolve(mapping, path, PathKind::Nt, intent, real, &info, nullptr);
        if (!NT_SUCCESS(status))
        {
            return status;
        }
        try
        {
            realNt.assign(real.Buffer, real.Length / sizeof(wchar_t));
        }
        catch (...)
        {
            FreeString(real);
            throw;
        }
        FreeString(real);
        if (info.RangeId != 0)
        {
            // The kernel maps the file without any hook: a ranged copy must be complete first.
            bool complete = false;
            status = _resolver.FetchRange(info.RangeId, 0, 0, true, complete);
            if (!NT_SUCCESS(status))
            {
                return status;
            }
            if (!complete)
            {
                return STATUS_UNEXPECTED_NETWORK_ERROR;
            }
        }
        // A local folder resolver only translates: the file may not be there.
        return FileExists(realNt) ? STATUS_SUCCESS : STATUS_OBJECT_NAME_NOT_FOUND;
    }

    NTSTATUS ImageMaterializer::Materialize(int mapping, const UNICODE_STRING& imageNtPath, MaterializedImage& image) const
    {
        const std::wstring virtualImage(imageNtPath.Buffer, imageNtPath.Length / sizeof(wchar_t));
        if (virtualImage.size() <= NtPrefixChars || virtualImage.compare(0, NtPrefixChars, NtPrefix) != 0)
        {
            return STATUS_OBJECT_PATH_SYNTAX_BAD;
        }
        const NTSTATUS status = Bring(mapping, virtualImage, image.RealNtPath);
        if (!NT_SUCCESS(status))
        {
            return status;
        }
        image.RealDosPath = DosPath(image.RealNtPath);
        image.RealDirectory = Directory(image.RealDosPath);
        image.VirtualDirectory = Directory(DosPath(virtualImage));

        const std::wstring virtualDirectoryNt = Directory(virtualImage);
        std::wstring ignored;
        Bring(mapping, virtualImage + L".manifest", ignored); // side-by-side manifest next to the image, when there is one

        // Breadth-first over the static imports found next to the image. seen holds every name asked once (found or not).
        std::vector<std::wstring> seen;
        std::vector<std::wstring> pending{ image.RealNtPath };
        size_t brought = 1;
        while (!pending.empty())
        {
            const std::wstring module = std::move(pending.back());
            pending.pop_back();
            for (const std::wstring& name : PeImportReader(module).ReadImportNames())
            {
                if (IsApiSetName(name) || ContainsName(seen, name))
                {
                    continue;
                }
                seen.push_back(name);
                if (brought >= MaxModules)
                {
                    continue;
                }
                const std::wstring virtualDll = virtualDirectoryNt + L'\\' + name;
                UNICODE_STRING virtualPath{};
                virtualPath.Buffer = const_cast<wchar_t*>(virtualDll.c_str());
                virtualPath.Length = static_cast<USHORT>(virtualDll.size() * sizeof(wchar_t));
                virtualPath.MaximumLength = virtualPath.Length;
                const int dllMapping = _resolver.FindMapping(virtualPath);
                std::wstring realDll;
                if (dllMapping >= 0 && NT_SUCCESS(Bring(dllMapping, virtualDll, realDll)))
                {
                    ++brought;
                    pending.push_back(std::move(realDll));
                }
            }
        }
        return STATUS_SUCCESS;
    }
}
