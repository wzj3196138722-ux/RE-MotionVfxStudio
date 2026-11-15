"""Shared helpers: load a RE Engine exe, map RVAs, disassemble with rip-relative targets annotated.

    python pe_image.py <exe> <rva hex> [count]      disassemble from an RVA

Needs pefile, capstone, numpy (pip install pefile capstone numpy).
"""
import struct, sys
import pefile, capstone

IMAGE_SCN_MEM_EXECUTE = 0x20000000


class Image:
    def __init__(self, exe):
        self.pe = pefile.PE(exe, fast_load=True)
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.data = open(exe, 'rb').read()
        self.secs = [(s.Name.rstrip(b'\0').decode(errors='replace'), s.VirtualAddress, s.PointerToRawData,
                      s.SizeOfRawData, s.Misc_VirtualSize, s.Characteristics) for s in self.pe.sections]
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self._imports = None

    # Section names are obfuscated in some builds (Wilds: code is ".data", tables ".sdata"),
    # so pick sections by flags and size, never by name.
    def code_section(self):
        return max((s for s in self.secs if s[5] & IMAGE_SCN_MEM_EXECUTE), key=lambda s: s[3])

    def rva2off(self, r):
        for n, va, po, ps, vs, ch in self.secs:
            if va <= r < va + ps:
                return po + r - va

    def off2rva(self, o):
        for n, va, po, ps, vs, ch in self.secs:
            if po <= o < po + ps:
                return va + o - po

    def u32(self, rva): return struct.unpack_from('<I', self.data, self.rva2off(rva))[0]
    def u64(self, rva): return struct.unpack_from('<Q', self.data, self.rva2off(rva))[0]
    def f32(self, rva): return struct.unpack_from('<f', self.data, self.rva2off(rva))[0]

    def cstr(self, rva, limit=120):
        o = self.rva2off(rva)
        if o is None:
            return None
        e = self.data.find(b'\0', o, o + limit)
        s = self.data[o:e]
        return s.decode() if e > o and len(s) >= 2 and all(32 <= c < 127 for c in s) else None

    def imports(self):
        """IAT slot RVA -> imported name. Entries are unresolved on disk: a slot holds the RVA of its hint/name."""
        if self._imports is None:
            self.pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_IMPORT']])
            self._imports = {imp.address - self.base: imp.name.decode()
                             for e in getattr(self.pe, 'DIRECTORY_ENTRY_IMPORT', []) for imp in e.imports if imp.name}
        return self._imports

    def jump_table(self, table_rva, n):
        """Targets of an MSVC `movsxd rax,[rcx+rax*4]; add rax,rcx; jmp rax` table."""
        o = self.rva2off(table_rva)
        return [table_rva + struct.unpack_from('<i', self.data, o + 4 * k)[0] for k in range(n)]

    def disasm(self, rva, count=200, stop_at_int3=True):
        """Lines of `rva mnemonic operands ; note`; rip-relative operands get the target RVA plus
        the import name, a C string, or the float/int stored there."""
        o = self.rva2off(rva)
        if o is None:
            return ['  (outside the image)']
        out = []
        for x in self.md.disasm(self.data[o:o + count * 15], rva):
            if stop_at_int3 and x.mnemonic == 'int3':
                break
            note = ''
            if '[rip' in x.op_str:
                try:
                    r = x.address + x.size + int(x.op_str.split('rip')[1].split(']')[0].replace(' ', ''), 16)
                    imp = self.imports().get(r)
                    s = self.cstr(r)
                    fo = self.rva2off(r)
                    if imp:
                        note = '  ; %x %s' % (r, imp)
                    elif s:
                        note = '  ; %x %r' % (r, s)
                    elif fo is not None:
                        note = '  ; [%x] f=%g i=%d' % (r, struct.unpack_from('<f', self.data, fo)[0],
                                                       struct.unpack_from('<i', self.data, fo)[0])
                    else:
                        note = '  ; [%x] (bss)' % r
                except Exception:
                    pass
            out.append('  %x %-9s %s%s' % (x.address, x.mnemonic, x.op_str, note))
            if len(out) >= count:
                break
        return out


if __name__ == '__main__':
    img = Image(sys.argv[1])
    print('\n'.join(img.disasm(int(sys.argv[2], 16), int(sys.argv[3]) if len(sys.argv) > 3 else 80, False)))
