#pragma once
// Virtual handles a child process inherits from its parent (plan 5.5).
#include "NtApi.h"
#include <ProcBridge/InitBlock.h>
#include <vector>

namespace ProcBridge::Hook
{
    /// Moves the "handle -> virtual mapping" entries of inheritable handles from a parent to a child.
    /// M4 (local): the parent copies them into the child's init block and the child adds them to its
    /// handle table at initialization. M2 replaces it with the Broker, which keeps the table of the
    /// whole session and answers "(parent pid, handle value)" when a child meets an unknown handle.
    class IHandleInheritance
    {
    public:
        virtual ~IHandleInheritance() = default;

        /// Parent side: appends the virtual handles a child created with inherited handles receives.
        virtual void ExportForChild(std::vector<Inject::InheritedHandle>& handles) const = 0;

        /// Child side: adopts the entries received from the parent.
        virtual void ImportFromParent(const Inject::InheritedHandle* handles, size_t count) noexcept = 0;
    };

    /// The local implementation backed by the file hooks' handle table.
    IHandleInheritance& GetLocalHandleInheritance() noexcept;
}
