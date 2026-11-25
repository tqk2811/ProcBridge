#include "ImageRedirect.h"
#include "PsAttributes.h"
#include <cstddef>
#include <cstring>

namespace ProcBridge::Hook
{
    namespace
    {
        /// Size of the caller's attribute list and index of its image name attribute (-1 when none). False when the
        /// list cannot be read (caller memory).
        bool FindImageName(const void* attributeList, SIZE_T& totalLength, SSIZE_T& index, const wchar_t*& name,
            SIZE_T& nameBytes) noexcept
        {
            index = -1;
            __try
            {
                const auto* list = static_cast<const Ps::AttributeList*>(attributeList);
                totalLength = list->TotalLength;
                if (totalLength < offsetof(Ps::AttributeList, Attributes))
                {
                    return false;
                }
                const SIZE_T count = (totalLength - offsetof(Ps::AttributeList, Attributes)) / sizeof(Ps::Attribute);
                for (SIZE_T i = 0; i < count; ++i)
                {
                    const Ps::Attribute& attribute = list->Attributes[i];
                    if ((attribute.Attribute & Ps::AttributeThreadFlag) == 0
                        && (attribute.Attribute & Ps::AttributeNumberMask) == Ps::AttributeImageName)
                    {
                        index = static_cast<SSIZE_T>(i);
                        name = reinterpret_cast<const wchar_t*>(attribute.Value);
                        nameBytes = attribute.Size;
                        return true;
                    }
                }
                return true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return false;
            }
        }

        bool SafeCopy(void* target, const void* source, SIZE_T bytes) noexcept
        {
            __try
            {
                memcpy(target, source, bytes);
                return true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return false;
            }
        }

        /// Swaps ImagePathName of normalized process parameters with value (saved receives the old one).
        bool SwapImagePath(PVOID processParameters, const UNICODE_STRING& value, UNICODE_STRING& saved) noexcept
        {
            __try
            {
                auto* parameters = static_cast<RTL_USER_PROCESS_PARAMETERS*>(processParameters);
                ULONG flags = 0;
                memcpy(&flags, parameters->Reserved1 + 8, sizeof(flags));
                if ((flags & Ps::ProcessParametersNormalized) == 0)
                {
                    return false; // buffers are offsets from the block: a pointer elsewhere cannot be expressed
                }
                saved = parameters->ImagePathName;
                parameters->ImagePathName = value;
                return true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return false;
            }
        }

        void RestoreImagePath(PVOID processParameters, const UNICODE_STRING& saved) noexcept
        {
            __try
            {
                static_cast<RTL_USER_PROCESS_PARAMETERS*>(processParameters)->ImagePathName = saved;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
            }
        }
    }

    ImageRedirect::~ImageRedirect()
    {
        Restore();
    }

    NTSTATUS ImageRedirect::Prepare() noexcept
    {
        try
        {
            return PrepareImage();
        }
        catch (...)
        {
            return STATUS_NO_MEMORY;
        }
    }

    NTSTATUS ImageRedirect::PrepareImage()
    {
        if (_attributeList == nullptr)
        {
            return STATUS_SUCCESS;
        }
        SIZE_T totalLength = 0;
        SSIZE_T index = -1;
        const wchar_t* name = nullptr;
        SIZE_T nameBytes = 0;
        if (!FindImageName(_attributeList, totalLength, index, name, nameBytes) || index < 0 || name == nullptr
            || nameBytes == 0 || nameBytes > 0xFFFC || (nameBytes % sizeof(wchar_t)) != 0)
        {
            return STATUS_SUCCESS; // the original call reports what is wrong with its arguments
        }
        std::wstring imagePath(nameBytes / sizeof(wchar_t), L'\0');
        if (!SafeCopy(imagePath.data(), name, nameBytes))
        {
            return STATUS_SUCCESS;
        }
        UNICODE_STRING imageNt{};
        imageNt.Buffer = imagePath.data();
        imageNt.Length = static_cast<USHORT>(nameBytes);
        imageNt.MaximumLength = imageNt.Length;
        const int mapping = _resolver.FindMapping(imageNt);
        if (mapping < 0)
        {
            return STATUS_SUCCESS;
        }
        const NTSTATUS status = ImageMaterializer(_resolver).Materialize(mapping, imageNt, _image);
        if (!NT_SUCCESS(status))
        {
            return status;
        }
        _attributes = std::make_unique<BYTE[]>(totalLength);
        if (!SafeCopy(_attributes.get(), _attributeList, totalLength))
        {
            return STATUS_ACCESS_VIOLATION;
        }
        Ps::Attribute& attribute = reinterpret_cast<Ps::AttributeList*>(_attributes.get())->Attributes[index];
        attribute.Value = reinterpret_cast<ULONG_PTR>(_image.RealNtPath.c_str());
        attribute.Size = _image.RealNtPath.size() * sizeof(wchar_t);
        _redirected = true;

        // Without the backing path in ImagePathName the child would still start, but its loader would look for the
        // DLLs next to the virtual name before the hook is there.
        _realImagePath.Buffer = _image.RealDosPath.data();
        _realImagePath.Length = static_cast<USHORT>(_image.RealDosPath.size() * sizeof(wchar_t));
        _realImagePath.MaximumLength = static_cast<USHORT>(_realImagePath.Length + sizeof(wchar_t));
        _parametersSwapped = _processParameters != nullptr && SwapImagePath(_processParameters, _realImagePath, _savedImagePath);
        return STATUS_SUCCESS;
    }

    void ImageRedirect::Restore() noexcept
    {
        if (_parametersSwapped)
        {
            RestoreImagePath(_processParameters, _savedImagePath);
            _parametersSwapped = false;
        }
    }

    std::wstring ImageRedirect::ChildEnvironment() const
    {
        std::wstring entry = std::wstring(ImageVariable) + L'=';
        if (_redirected)
        {
            entry += _image.RealDirectory + L'*' + _image.VirtualDirectory;
        }
        entry += L'\0';
        return entry;
    }
}
