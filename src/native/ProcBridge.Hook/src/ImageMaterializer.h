#pragma once
#include "PathMapper.h"
#include <string>
#include <vector>

namespace ProcBridge::Hook
{
    /// Backing copy of an image started from a virtual folder (see ImageMaterializer).
    struct MaterializedImage
    {
        std::wstring RealNtPath;       // \??\C:\cache\...\bin\app.exe: the file the kernel maps
        std::wstring RealDosPath;      // C:\cache\...\bin\app.exe: ImagePathName of the child while it starts
        std::wstring RealDirectory;    // C:\cache\...\bin (DOS, no trailing '\')
        std::wstring VirtualDirectory; // D:\Work\virtual\bin (DOS, no trailing '\')
    };

    /// Brings an image of a virtual folder into its backing folder before a process is created from it.
    /// NtCreateUserProcess opens the image in the kernel, which no hook sees, and the child's loader resolves the
    /// static imports before the hook is injected into it. So the image, its "<image>.manifest" and the DLLs it
    /// imports (recursively) that live next to it in the virtual folder are made complete files in the backing
    /// folder (a Broker download, never a ranged copy), and the process is started from that copy.
    /// Runs on the creating thread under the hooks' re-entry guard: the file calls it makes reach the real files.
    class ImageMaterializer final
    {
    public:
        /// Most modules brought for one image (the image included).
        static constexpr size_t MaxModules = 128;

        explicit ImageMaterializer(const IPathResolver& resolver) noexcept : _resolver(resolver) {}

        /// imageNtPath is a virtual NT path matched by FindMapping (mapping). Returns the NTSTATUS the process creation
        /// fails with when the image itself cannot be brought; missing or failing dependencies are skipped (the loader
        /// reports them as it would locally). Throws std::bad_alloc.
        NTSTATUS Materialize(int mapping, const UNICODE_STRING& imageNtPath, MaterializedImage& image) const;

    private:
        /// Makes the backing copy of virtualNt complete and returns its NT path.
        NTSTATUS Bring(int mapping, const std::wstring& virtualNt, std::wstring& realNt) const;

        const IPathResolver& _resolver;
    };
}
