"""hksdis.py - reads Black Ops II's compiled LUI scripts (Havok Script bytecode, "\\x1bLuaQ" format 0x0D) as a listing.

A study tool for the theater-only launch (docs\\theater-only.md, 2026-10-17): it reads the game's own files, as OpenAssetTools'
Unlinker dumps them from the zones (rawfile .lua); it never writes one, and nothing it reads is shipped. Clean-room: the format
was read from the files themselves (the header, the type table, the "_" padding before the code, the constants), and the
opcode names follow the published Havok Script list; every listing says where a guess is.

    python -I hksdis.py <file.lua> [--strings]

The listing: one block per function (its number, parameters, registers), then each instruction with what it names.
"""
import struct
import sys

# Havok Script 5.1 opcodes, by number (the CoD family's list); "DATA" is the extra word some instructions carry
OPS = ["GETFIELD", "TEST", "CALL_I", "CALL_C", "EQ", "EQ_BK", "GETGLOBAL", "MOVE", "SELF", "RETURN", "GETTABLE_S",
       "GETTABLE_N", "GETTABLE", "LOADBOOL", "TFORLOOP", "SETFIELD", "SETTABLE_S", "SETTABLE_S_BK", "SETTABLE_N",
       "SETTABLE_N_BK", "SETTABLE", "SETTABLE_BK", "TAILCALL_I", "TAILCALL_C", "TAILCALL_M", "LOADK", "LOADNIL",
       "SETGLOBAL", "JMP", "CALL_M", "CALL", "INTRINSIC_INDEX", "INTRINSIC_NEWINDEX", "INTRINSIC_SELF",
       "INTRINSIC_INDEX_LITERAL", "INTRINSIC_NEWINDEX_LITERAL", "INTRINSIC_SELF_LITERAL", "TAILCALL", "GETUPVAL",
       "SETUPVAL", "ADD", "ADD_BK", "SUB", "SUB_BK", "MUL", "MUL_BK", "DIV", "DIV_BK", "MOD", "MOD_BK", "POW", "POW_BK",
       "NEWTABLE", "UNM", "NOT", "LEN", "LT", "LT_BK", "LE", "LE_BK", "CONCAT", "TESTSET", "FORPREP", "FORLOOP",
       "SETLIST", "CLOSE", "CLOSURE", "VARARG", "TAILCALL_I_R1", "CALL_I_R1", "SETUPVAL_R1", "TEST_R1", "NOT_R1",
       "GETFIELD_R1", "SETFIELD_R1", "NEWSTRUCT", "DATA", "SETSLOTN", "SETSLOTI", "SETSLOT", "SETSLOTS", "SETSLOTMT",
       "CHECKTYPE", "CHECKTYPES", "GETSLOT", "GETSLOTMT", "SELFSLOT", "SELFSLOTMT", "GETFIELD_MM", "CHECKTYPE_D",
       "GETSLOT_D", "GETGLOBAL_MEM"]


class Reader:
    def __init__(self, b):
        self.b, self.p = b, 0

    def u8(self):
        v = self.b[self.p]
        self.p += 1
        return v

    def i32(self):
        v = struct.unpack_from("<i", self.b, self.p)[0]
        self.p += 4
        return v

    def u32(self):
        v = struct.unpack_from("<I", self.b, self.p)[0]
        self.p += 4
        return v

    def f32(self):
        v = struct.unpack_from("<f", self.b, self.p)[0]
        self.p += 4
        return v

    def raw(self, n):
        v = self.b[self.p:self.p + n]
        self.p += n
        return v


class Func:
    pass


def read_func(r, types):
    f = Func()
    f.at = r.p
    f.nups, f.nparams, f.flags, f.maxstack, n = r.i32(), r.i32(), r.u8(), r.i32(), r.i32()
    while r.p % 4:                                  # the code is aligned to 4 with '_' bytes
        if r.u8() != 0x5F:
            raise ValueError("no '_' padding before the code at %#x" % (r.p - 1))
    f.code = [r.u32() for _ in range(n)]
    f.k = []
    for _ in range(r.i32()):
        t = r.u8()
        name = types.get(t, "?%d" % t)
        if name == "TNIL":
            f.k.append(None)
        elif name == "TBOOLEAN":
            f.k.append(bool(r.u8()))
        elif name == "TNUMBER":
            v = r.f32()
            f.k.append(int(v) if v == int(v) and abs(v) < 1e9 else v)
        elif name == "TSTRING":
            ln = r.i32()
            f.k.append(r.raw(ln).rstrip(b"\0").decode("latin1"))
        elif name == "TLIGHTUSERDATA":
            f.k.append(("lightuserdata", r.u32()))
        elif name == "TUI64":
            f.k.append(("ui64", struct.unpack_from("<Q", r.raw(8))[0]))
        else:
            raise ValueError("constant of type %s at %#x: unknown layout" % (name, r.p - 1))
    f.debug = r.i32()                               # 1 in every file seen: then a 32-bit hash
    if f.debug != 1:
        raise ValueError("debug word %d at %#x (only 1 is known)" % (f.debug, r.p - 4))
    f.hash = r.u32()
    f.protos = [read_func(r, types) for _ in range(r.i32())]
    return f


def read(b):
    r = Reader(b)
    if b[:4] != b"\x1bLua" or b[4] != 0x51:
        raise ValueError("not Havok Script 5.1 bytecode")
    r.p = 14                                        # version, format, endianness, 4 sizes, 3 flag bytes
    types = {}
    for _ in range(r.i32()):
        tid, ln = r.i32(), r.i32()
        types[tid] = r.raw(ln).rstrip(b"\0").decode("latin1")
    main = read_func(r, types)
    if r.p != len(b):
        raise ValueError("%d bytes left after the main function" % (len(b) - r.p))
    return main


def kstr(f, i):
    if 0 <= i < len(f.k):
        v = f.k[i]
        return repr(v) if isinstance(v, str) else str(v)
    return "K?%d" % i


def rk(f, c):
    return kstr(f, c & 0xFF) if c & 0x100 else "r%d" % c


def listing(f, path="main", out=None):
    out = out if out is not None else []
    out.append("")
    out.append("function %s  (params %d, upvalues %d, %s, registers %d, %d instructions, %d constants)" % (
        path, f.nparams, f.nups, "vararg" if f.flags & 2 else "fixed", f.maxstack, len(f.code), len(f.k)))
    for pc, ins in enumerate(f.code):
        op = ins >> 25
        a, c, b, bx = ins & 0xFF, (ins >> 8) & 0x1FF, (ins >> 17) & 0xFF, (ins >> 8) & 0x1FFFF
        sbx = bx - 0xFFFF                           # the jump offset, biased by 0xFFFF as in Lua 5.1
        name = OPS[op] if op < len(OPS) else "OP%d" % op
        if name == "DATA":
            continue
        s = {
            "GETGLOBAL": "r%d = %s" % (a, kstr(f, bx)),
            "GETGLOBAL_MEM": "r%d = %s" % (a, kstr(f, bx)),
            "SETGLOBAL": "%s = r%d" % (kstr(f, bx), a),
            "LOADK": "r%d = %s" % (a, kstr(f, bx)),
            "GETFIELD": "r%d = r%d.%s" % (a, b, kstr(f, c)),
            "GETFIELD_R1": "r%d = r%d.%s" % (a, b, kstr(f, c)),
            "GETFIELD_MM": "r%d = r%d.%s" % (a, b, kstr(f, c)),
            "SETFIELD": "r%d.%s = %s" % (a, kstr(f, b), rk(f, c)),
            "SETFIELD_R1": "r%d.%s = %s" % (a, kstr(f, b), rk(f, c)),
            "SELF": "r%d = r%d; r%d = r%d:%s" % (a + 1, b, a, b, rk(f, c)),
            "MOVE": "r%d = r%d" % (a, b),
            "LOADBOOL": "r%d = %s%s" % (a, "true" if b else "false", "; skip" if c else ""),
            "LOADNIL": "r%d..r%d = nil" % (a, b),
            "GETUPVAL": "r%d = upvalue %d" % (a, b),
            "SETUPVAL": "upvalue %d = r%d" % (b, a),
            "SETUPVAL_R1": "upvalue %d = r%d" % (b, a),
            "CLOSURE": "r%d = function %s.%d" % (a, path, bx),
            "JMP": "jump %+d -> %d" % (sbx, pc + 1 + sbx),
            "NEWTABLE": "r%d = {}" % a,
            "RETURN": "return r%d..(%d values)" % (a, b - 1) if b else "return r%d.." % a,
        }.get(name)
        if s is None and name.startswith(("CALL", "TAILCALL")):
            s = "r%d(%s)%s" % (a, "%d args" % (b - 1) if b else "args to top", "" if c == 1 else " -> %d results" % (c - 1) if c else " -> results to top")
        if s is None and name.endswith("_BK"):
            s = "A=r%d B=%s C=%s" % (a, kstr(f, b), rk(f, c))
        if s is None:
            s = "A=%d B=%d C=%s Bx=%d" % (a, b, rk(f, c), bx)
        out.append("  %4d  %-14s %s" % (pc, name, s))
    for i, p in enumerate(f.protos):
        listing(p, "%s.%d" % (path, i), out)
    return out


def strings(f, out=None):
    out = out if out is not None else []
    out.extend(k for k in f.k if isinstance(k, str))
    for p in f.protos:
        strings(p, out)
    return out


if __name__ == "__main__":
    data = open(sys.argv[1], "rb").read()
    main = read(data)
    if "--strings" in sys.argv:
        print("\n".join(strings(main)))
    else:
        print("\n".join(listing(main)))
