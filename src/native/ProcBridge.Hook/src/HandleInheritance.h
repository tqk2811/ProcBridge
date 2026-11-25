#pragma once
// Virtual handles a child process inherits from its parent (plan 5.5).
#include "NtApi.h"
#include <ProcBridge/InitBlock.h>
#include <vector>

namespace ProcBridge::Hook
{
    /// Moves the "handle -> virtual mapping" entries of inheritable handles from a parent to a child.
    /// The parent copies them into the child's init block and the child adds them to its handle table at
    /// initialization. With the Broker (M2) a remote handle's record also carries its virtual path and
    /// machine B's metadata (RemoteHandleTable), so no Broker-side table of the session is needed.
    class IHandleInheritance
    {
    public:
        virtual ~IHandleInheritance() = default;

        /// Parent side: appends the virtual handles a child created with inherited handles receives.
        /// extra receives the records the ExtraOffset of the handles point into. Throws std::bad_alloc.
        virtual void ExportForChild(std::vector<Inject::InheritedHandle>& handles, std::vector<BYTE>& extra) const = 0;

        /// Parent side, once the child's list is final (after ExportForChild and the handle list filter): the
        /// Broker's write tokens of those handles stay open until the child closes its copies as well.
        virtual void RetainForChild(const std::vector<Inject::InheritedHandle>& handles, const std::vector<BYTE>& extra) const noexcept = 0;

        /// Child side: adopts the entries received from the parent.
        virtual void ImportFromParent(const Inject::InheritedHandle* handles, size_t count, const BYTE* extra,
            size_t extraBytes) noexcept = 0;
    };

    /// The implementation backed by the file hooks' handle tables (local and remote).
    IHandleInheritance& GetLocalHandleInheritance() noexcept;
}
