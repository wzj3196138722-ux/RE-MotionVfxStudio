#pragma once
#include <windows.h>
#include <vector>
#include <cstring>

namespace mvl {
struct PointerPatch {
    void** slot;
    void* original;
    void* replacement;
};
inline bool replace_pointer(void** slot, void* expected, void* replacement) {
    DWORD protection = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &protection)) return false;
    void* prior = InterlockedCompareExchangePointer(slot, replacement, expected);
    DWORD ignored;
    VirtualProtect(slot, sizeof(void*), protection, &ignored);
    return prior == expected;
}
// Patch only the importing executable's slots. REFramework, our own sampler,
// other processes and Windows retain their original input functions.
inline size_t hook_import(HMODULE module, const char* name, void* replacement,
    void*& original, std::vector<PointerPatch>& patches) {
    auto base = reinterpret_cast<unsigned char*>(module);
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (!base || dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0) return 0;
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) return 0;
    const size_t size = nt->OptionalHeader.SizeOfImage;
    const auto directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!directory.VirtualAddress || directory.VirtualAddress >= size || directory.Size > size-directory.VirtualAddress) return 0;
    auto descriptors = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + directory.VirtualAddress);
    size_t installed = 0;
    for (size_t i=0; i<directory.Size/sizeof(*descriptors) && descriptors[i].Name; ++i) {
        const auto& descriptor = descriptors[i];
        if (!descriptor.OriginalFirstThunk || !descriptor.FirstThunk) continue;
        for (size_t index=0;; ++index) {
            size_t lookup = descriptor.OriginalFirstThunk + index*sizeof(IMAGE_THUNK_DATA64);
            size_t address = descriptor.FirstThunk + index*sizeof(IMAGE_THUNK_DATA64);
            if (lookup + sizeof(IMAGE_THUNK_DATA64) > size || address + sizeof(void*) > size) break;
            const auto thunk = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + lookup)->u1.AddressOfData;
            if (!thunk) break;
            if (IMAGE_SNAP_BY_ORDINAL64(thunk) || thunk+sizeof(WORD) >= size) continue;
            auto candidate = reinterpret_cast<const char*>(base+thunk+sizeof(WORD));
            size_t remaining = size-thunk-sizeof(WORD);
            if (!memchr(candidate, 0, remaining) || strcmp(candidate,name) != 0) continue;
            auto slot = reinterpret_cast<void**>(base+address);
            // Multiple imports may share a wrapper. Do not destroy another
            // mod's distinct hook chain by silently choosing the wrong target.
            if (original && original != *slot) continue;
            original = *slot; // Publish the original before installing callback.
            if (replace_pointer(slot,original,replacement)) {
                patches.push_back({slot,original,replacement}); ++installed;
            }
        }
    }
    return installed;
}
inline void restore_pointers(std::vector<PointerPatch>& patches) {
    for (auto it=patches.rbegin(); it!=patches.rend(); ++it)
        replace_pointer(it->slot,it->replacement,it->original);
    patches.clear();
}
}
