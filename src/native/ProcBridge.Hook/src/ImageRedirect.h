#pragma once
#include "ImageMaterializer.h"
#include <memory>

namespace ProcBridge::Hook
{
    /// Starts a process whose image lives in a virtual folder from the image's backing copy (one NtCreateUserProcess
    /// call). Prepare brings the image in (ImageMaterializer) and builds a copy of the caller's PS_ATTRIBUTE_LIST whose
    /// image name (PS_ATTRIBUTE_IMAGE_NAME) is the backing NT path, and points ImagePathName of the caller's process
    /// parameters at the backing DOS path, so the child's loader finds the DLLs brought next to the image. The child's
    /// hook gives the loader entries their virtual names back (ImageVariable, see LoaderNames.h). The destructor
    /// restores the caller's process parameters. Does nothing for an image outside the virtual folders.
    class ImageRedirect final
    {
    public:
        /// Variable of the child's init block: "<real directory>*<virtual directory>" of a redirected image, empty
        /// otherwise (the initializer then removes a value inherited from elsewhere).
        static constexpr const wchar_t* ImageVariable = L"PROCBRIDGE_IMAGE";

        ImageRedirect(const IPathResolver& resolver, PVOID attributeList, PVOID processParameters) noexcept
            : _resolver(resolver), _attributeList(attributeList), _processParameters(processParameters)
        {
        }

        ~ImageRedirect();
        ImageRedirect(const ImageRedirect&) = delete;
        ImageRedirect& operator=(const ImageRedirect&) = delete;

        /// STATUS_SUCCESS (also when the image is not virtual) or the status the process creation fails with.
        NTSTATUS Prepare() noexcept;

        /// The attribute list to give to NtCreateUserProcess.
        PVOID AttributeList() const noexcept { return _attributes != nullptr ? _attributes.get() : _attributeList; }

        /// Restores the caller's process parameters (also done by the destructor).
        void Restore() noexcept;

        /// "PROCBRIDGE_IMAGE=<value>\0" for the child's init block. Throws std::bad_alloc.
        std::wstring ChildEnvironment() const;

    private:
        NTSTATUS PrepareImage();

        const IPathResolver& _resolver;
        PVOID _attributeList;
        PVOID _processParameters;
        bool _redirected = false;
        MaterializedImage _image;
        std::unique_ptr<BYTE[]> _attributes;
        UNICODE_STRING _realImagePath{};      // points into _image.RealDosPath
        UNICODE_STRING _savedImagePath{};     // caller's ImagePathName while swapped
        bool _parametersSwapped = false;
    };
}
