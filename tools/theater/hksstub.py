"""hksstub.py - a tiny Havok Script chunk of our own, in the game's bytecode format, that runs plain-text Lua (2026-10-17).

Why: probe 2 hung Black Ops II at start with our ffotdmp.lua as loose text - its Lua loader reads a rawfile as bytecode.
The stub is bytecode, written by hand from the format hksdis.py reads (docs\\theater-only.md), and does no more than:

    Engine.SetDvar("reluduali_lua_stub", WORD)                 -- proof that the stub itself ran
    local f = loadstring(SOURCE)                                -- the game's own compiler, on our text
    Engine.SetDvar("reluduali_lua_compiled", tostring(f))       -- "function: ..." or "nil"
    pcall(f)                                                    -- our text runs; a mistake in it stays inside

Nothing from the game goes into it: the format's header and type names, our two strings and our text.

    python -I hksstub.py <source.lua> <word> <out.lua>
"""
import struct
import sys

# the format's header (version 0x51, format 0x0D, little-endian, 4-byte int/size_t/instruction/number) and its 13 type names
HEADER = b"\x1bLuaQ\x0d\x01\x04\x04\x04\x04\x00\x00\x00"
TYPES = ["TNIL", "TBOOLEAN", "TLIGHTUSERDATA", "TNUMBER", "TSTRING", "TTABLE", "TFUNCTION", "TUSERDATA", "TTHREAD",
         "TIFUNCTION", "TCFUNCTION", "TUI64", "TSTRUCT"]
OP = {"GETGLOBAL": 6, "MOVE": 7, "RETURN": 9, "LOADK": 25, "CALL_I": 2, "GETFIELD_R1": 73, "DATA": 76}


def abc(op, a, b=0, c=0):
    return (OP[op] << 25) | ((b & 0xFF) << 17) | ((c & 0x1FF) << 8) | (a & 0xFF)


def abx(op, a, bx):
    return (OP[op] << 25) | ((bx & 0x1FFFF) << 8) | (a & 0xFF)


def stub(source, word):
    k = ["Engine", "SetDvar", "reluduali_lua_stub", word, "loadstring", source, "reluduali_lua_compiled", "tostring", "pcall"]
    K = {s: i for i, s in enumerate(k)}
    data = abc("DATA", 0)                                   # GETFIELD_R1 carries two empty words after it, as the game's code does
    code = [
        abx("GETGLOBAL", 0, K["Engine"]),
        abc("GETFIELD_R1", 0, 0, K["SetDvar"]), data, data,
        abx("LOADK", 1, K["reluduali_lua_stub"]),
        abx("LOADK", 2, K[word]),
        abc("CALL_I", 0, 3, 1),                             # Engine.SetDvar(r1, r2): 2 arguments, no result
        abx("GETGLOBAL", 0, K["loadstring"]),
        abx("LOADK", 1, K[source]),
        abc("CALL_I", 0, 2, 2),                             # r0 = loadstring(r1)
        abx("GETGLOBAL", 1, K["Engine"]),
        abc("GETFIELD_R1", 1, 1, K["SetDvar"]), data, data,
        abx("LOADK", 2, K["reluduali_lua_compiled"]),
        abx("GETGLOBAL", 3, K["tostring"]),
        abc("MOVE", 4, 0),
        abc("CALL_I", 3, 2, 2),                             # r3 = tostring(r0)
        abc("CALL_I", 1, 3, 1),                             # Engine.SetDvar(r2, r3)
        abx("GETGLOBAL", 1, K["pcall"]),
        abc("MOVE", 2, 0),
        abc("CALL_I", 1, 2, 1),                             # pcall(r0)
        abc("RETURN", 0, 1),
    ]
    out = bytearray(HEADER)
    out += struct.pack("<i", len(TYPES))
    for i, t in enumerate(TYPES):
        name = t.encode() + b"\0"
        out += struct.pack("<ii", i, len(name)) + name
    out += struct.pack("<ii", 0, 0) + bytes([2]) + struct.pack("<ii", 5, len(code))   # upvalues, params, vararg, registers, count
    while len(out) % 4:
        out += b"_"                                         # the code starts on a 4-byte boundary, padded with '_'
    for ins in code:
        out += struct.pack("<I", ins)
    out += struct.pack("<i", len(k))
    for s in k:
        b = s.encode("utf-8") + b"\0"
        out += bytes([4]) + struct.pack("<i", len(b)) + b   # TSTRING, its length with the 0, the bytes
    out += struct.pack("<iIi", 1, 0x52454C55, 0)            # the debug word, a hash of our own ("RELU"), no nested functions
    return bytes(out)


if __name__ == "__main__":
    src = open(sys.argv[1], encoding="utf-8").read()
    open(sys.argv[3], "wb").write(stub(src, sys.argv[2]))
