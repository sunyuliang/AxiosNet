#!/usr/bin/env python3
"""从 .nupkg（或裸 .dll）里提取 PDB debug id —— 即 PE 调试目录中的 CodeView/RSDS 项 GUID。

为什么需要它：nuget.org 的符号服务器按 debug id 定位 PDB，

    https://symbols.nuget.org/download/symbols/{PdbName}/{debugId}FFFFFFFF/{PdbName}

所以「消费者能不能 F11 进库源码」的唯一权威判据，就是拿**已发布二进制**里的这个 id
去符号服务器取 PDB。裸字节搜 b'RSDS' 也能凑出 GUID，但会误命中数据段里的巧合序列；
按 PE 数据结构解析（COFF → optional header → 数据目录第 6 项 Debug → 各条目）才可靠。

用法：
    python3 .github/scripts/debug-id.py --nupkg artifacts/AxiosNet.1.0.2.nupkg
    python3 .github/scripts/debug-id.py --nupkg published.nupkg
    python3 .github/scripts/debug-id.py --dll src/AxiosNet/bin/Release/net6.0/AxiosNet.dll
    python3 .github/scripts/debug-id.py --nupkg x.nupkg --entry lib/net6.0/AxiosNet.dll

stdout 只输出 32 位大写十六进制 id（便于 `$(...)` 直接取用），诊断信息走 stderr。
"""

from __future__ import annotations

import argparse
import struct
import sys
import uuid
import zipfile

IMAGE_DEBUG_TYPE_CODEVIEW = 2
PE32_MAGIC = 0x10B

# Debug 是可选头数据目录的第 6 项（0=Export … 6=Debug）
DEBUG_DIRECTORY_INDEX = 6
# 数据目录相对 optional header 起点的偏移：PE32 = 96，PE32+ = 112
DATA_DIR_OFFSET_PE32 = 96
DATA_DIR_OFFSET_PE32PLUS = 112
DEBUG_ENTRY_SIZE = 28


def load_dll_from_nupkg(path: str, entry: str | None) -> tuple[str, bytes]:
    with zipfile.ZipFile(path) as package:
        candidates = [n for n in package.namelist() if n.startswith("lib/") and n.endswith(".dll")]
        if not candidates:
            sys.exit(f"{path} 内没有 lib/**/*.dll，不是类库包？")
        if entry is not None:
            if entry not in candidates:
                sys.exit(f"{path} 内没有条目 {entry}")
            chosen = entry
        else:
            # 优先取 net6.0 资产（双目标里覆盖面最广的那份：.NET 6/7 消费者拿到的就是它）
            preferred = [n for n in candidates if n.startswith("lib/net6.0/")]
            chosen = sorted(preferred or candidates)[0]
        return chosen, package.read(chosen)


def debug_id_of(dll: bytes) -> tuple[str, str]:
    """返回 (debug id, pdb 路径记录)。两者都来自 CodeView(RSDS) 项。"""
    if dll[:2] != b"MZ":
        sys.exit("不是 PE 文件（缺少 MZ 头）")

    pe_offset = struct.unpack_from("<I", dll, 0x3C)[0]
    coff = pe_offset + 4
    num_sections = struct.unpack_from("<H", dll, coff + 2)[0]
    optional_size = struct.unpack_from("<H", dll, coff + 16)[0]
    optional = coff + 20
    magic = struct.unpack_from("<H", dll, optional)[0]
    if magic not in (PE32_MAGIC, 0x20B):
        sys.exit(f"未知的 optional header magic: 0x{magic:X}")

    data_dir = optional + (DATA_DIR_OFFSET_PE32 if magic == PE32_MAGIC else DATA_DIR_OFFSET_PE32PLUS)
    debug_rva, debug_size = struct.unpack_from("<II", dll, data_dir + DEBUG_DIRECTORY_INDEX * 8)
    if debug_size == 0:
        sys.exit("PE 调试目录为空：没有生成 PDB（检查 DebugType=portable / DebugSymbols）")

    # RVA → 文件偏移：遍历节表找覆盖该 RVA 的节
    section_table = optional + optional_size
    debug_offset = None
    for index in range(num_sections):
        base = section_table + index * 40
        virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from("<IIII", dll, base + 8)
        span = max(virtual_size, raw_size)
        if virtual_address <= debug_rva < virtual_address + span:
            debug_offset = raw_pointer + (debug_rva - virtual_address)
            break
    if debug_offset is None:
        sys.exit("调试目录的 RVA 不在任何节内，PE 结构异常")

    for index in range(debug_size // DEBUG_ENTRY_SIZE):
        base = debug_offset + index * DEBUG_ENTRY_SIZE
        _, _, _, _, kind, entry_size, _, entry_pointer = struct.unpack_from("<IIHHIIII", dll, base)
        if kind != IMAGE_DEBUG_TYPE_CODEVIEW:
            continue
        entry = dll[entry_pointer : entry_pointer + entry_size]
        if entry[:4] != b"RSDS":
            sys.exit("CodeView 项不是 RSDS（不是 portable PDB）")
        # RSDS 布局：签名(4) + GUID(16, 存的是 GUID 的前三个字段的字节序) + Age(4) + 路径
        guid = uuid.UUID(bytes_le=entry[4:20]).hex.upper()
        pdb_path = entry[24:].split(b"\x00", 1)[0].decode("utf-8", "replace")
        return guid, pdb_path

    sys.exit("调试目录里没有 CodeView 项（可能是 embedded PDB 或未生成符号）")


def main() -> None:
    parser = argparse.ArgumentParser(description="提取 .NET 程序集的 PDB debug id")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--nupkg", help="从 .nupkg 内的 lib/**/*.dll 提取")
    source.add_argument("--dll", help="从裸 .dll 提取")
    parser.add_argument("--entry", help="包内 DLL 条目路径（默认优先 lib/net6.0/）")
    args = parser.parse_args()

    if args.nupkg:
        entry, dll = load_dll_from_nupkg(args.nupkg, args.entry)
        print(f"{args.nupkg} -> {entry}", file=sys.stderr)
    else:
        with open(args.dll, "rb") as handle:
            dll = handle.read()
        entry = args.dll

    guid, pdb_path = debug_id_of(dll)
    print(f"pdb: {pdb_path}", file=sys.stderr)
    print(guid)


if __name__ == "__main__":
    main()
