using System.Buffers.Binary;
using System.Text;

namespace Zeitlind.Hook.Common;

/// <summary>
/// 明文包解析器在 GameAssembly.dll 中的位置，以及可以安全覆盖的入口字节数。
/// </summary>
public readonly record struct ParserLocation(nint Address, uint Rva, int PatchSize);

/// <summary>
/// 定位明文包解析器。
///
/// 这里不锚定入口机器码。函数序言里的寄存器搬运指令由编译器的寄存器分配决定，
/// 游戏重新编译一次就可能改变，固定字节特征会随版本失效。改为锚定两个更稳定的事实：
///
/// 1. 调用方传入的包头与包尾魔数是网络协议常量，由服务端和客户端共同约定，
///    不随客户端重新编译而改变；
/// 2. x64 PE 的异常目录（<c>.pdata</c>）由编译器为每个函数生成，可以把任意函数内
///    地址精确还原成函数入口，无需猜测函数边界。
///
/// 于是流程为：在可执行节里找出两个魔数的全部出现位置，用异常目录把它们归属到各自
/// 的函数，取同时包含两个魔数的函数；若不止一个，再要求魔数出现在比较指令的立即数
/// 上（解析器校验魔数，构造器只写入魔数）。最后用 <c>UNWIND_INFO</c> 证明入口序言
/// 只由 push 和栈分配构成，因而可以整段搬进 trampoline。
/// </summary>
public static unsafe class ParserLocator
{
    private const int JumpSize = 14;
    private const uint ImageScnMemExecute = 0x20000000;
    private const int MaxPatchSize = 32;
    private const int MaxMagicHits = 256;

    private const byte UwopPushNonvol = 0;
    private const byte UwopAllocLarge = 1;
    private const byte UwopAllocSmall = 2;
    private const byte UwopSetFpreg = 3;
    private const byte UwopSaveNonvol = 4;
    private const byte UwopSaveNonvolFar = 5;
    private const byte UwopSaveXmm128 = 8;
    private const byte UwopSaveXmm128Far = 9;
    private const byte UwopPushMachframe = 10;

    private const byte UnwFlagChainInfo = 0x4;

    private sealed class Candidate
    {
        public uint Begin;
        public uint End;
        public uint UnwindInfo;
        public bool HasHead;
        public bool HasTail;
        public bool ComparesMagic;
    }

    public static ParserLocation Locate(nint moduleBase, uint headMagic, uint tailMagic)
    {
        var pe = PeImage.Open(moduleBase, "GameAssembly.dll");
        var image = pe.Image;
        if (!pe.TryGetDataDirectory(3, out var exceptionRva, out var exceptionSize))
        {
            throw new InvalidDataException("GameAssembly.dll 没有异常目录，无法还原函数边界");
        }

        if (
            exceptionRva == 0
            || exceptionSize < 12
            || exceptionSize % 12 != 0
            || !pe.ContainsRange(exceptionRva, exceptionSize)
        )
        {
            throw new InvalidDataException("GameAssembly.dll 的异常目录范围无效，无法还原函数边界");
        }

        var functionTable = pe.GetPointer(exceptionRva, exceptionSize, "异常目录");
        var functionCount = checked((int)(exceptionSize / 12));

        var headPattern = ToLittleEndianBytes(headMagic);
        var tailPattern = ToLittleEndianBytes(tailMagic);

        var headHits = new List<uint>();
        var tailHits = new List<uint>();
        ScanExecutableSections(pe, headPattern, tailPattern, headHits, tailHits);

        if (headHits.Count == 0 || tailHits.Count == 0)
        {
            throw new InvalidDataException(
                $"可执行节中找不到成对的包头/包尾魔数（包头 {headHits.Count} 处，包尾 {tailHits.Count} 处），网络协议可能已经改变"
            );
        }

        var candidates = new List<Candidate>();
        CollectCandidates(pe, functionTable, functionCount, headHits, isHead: true, candidates);
        CollectCandidates(pe, functionTable, functionCount, tailHits, isHead: false, candidates);

        var matched = new List<Candidate>();
        foreach (var candidate in candidates)
        {
            if (candidate.HasHead && candidate.HasTail)
            {
                matched.Add(candidate);
            }
        }

        if (matched.Count == 0)
        {
            throw new InvalidDataException("没有任何函数同时引用包头和包尾魔数，无法确定明文包解析器");
        }

        var target = SelectParser(matched);
        var patchSize = ComputeRelocatablePatchSize(pe, target.Begin, target.End, target.UnwindInfo);

        return new ParserLocation((nint)(image + target.Begin), target.Begin, patchSize);
    }

    private static Candidate SelectParser(List<Candidate> matched)
    {
        if (matched.Count == 1)
        {
            return matched[0];
        }

        // 解析器校验魔数，编解码器构造函数只把魔数写进对象字段。只有前者会把魔数
        // 当成比较指令的立即数。
        Candidate? comparing = null;
        var comparingCount = 0;
        foreach (var candidate in matched)
        {
            if (candidate.ComparesMagic)
            {
                comparing = candidate;
                comparingCount++;
            }
        }

        if (comparingCount == 1 && comparing is not null)
        {
            return comparing;
        }

        var described = new StringBuilder();
        foreach (var candidate in matched)
        {
            if (described.Length != 0)
            {
                described.Append('、');
            }

            described.Append($"RVA 0x{candidate.Begin:X}（大小 0x{candidate.End - candidate.Begin:X}");
            described.Append(candidate.ComparesMagic ? "，含魔数比较）" : "）");
        }

        throw new InvalidDataException(
            $"有 {matched.Count} 个函数同时引用包头和包尾魔数，无法区分明文包解析器：{described}，需要重新分析当前游戏版本"
        );
    }

    private static void CollectCandidates(
        PeImage pe,
        byte* functionTable,
        int functionCount,
        List<uint> hits,
        bool isHead,
        List<Candidate> candidates
    )
    {
        foreach (var hit in hits)
        {
            if (
                !TryResolveFunction(
                    pe,
                    functionTable,
                    functionCount,
                    hit,
                    out var begin,
                    out var end,
                    out var unwind
                )
            )
            {
                // 落在任何函数之外的命中只是恰好相同的数据，忽略
                continue;
            }

            Candidate? candidate = null;
            foreach (var item in candidates)
            {
                if (item.Begin == begin)
                {
                    candidate = item;
                    break;
                }
            }

            if (candidate is null)
            {
                candidate = new Candidate
                {
                    Begin = begin,
                    End = end,
                    UnwindInfo = unwind,
                };
                candidates.Add(candidate);
            }

            if (isHead)
            {
                candidate.HasHead = true;
            }
            else
            {
                candidate.HasTail = true;
            }

            candidate.ComparesMagic |= IsCompareImmediate(pe, hit);
        }
    }

    private static void ScanExecutableSections(
        PeImage pe,
        ReadOnlySpan<byte> headPattern,
        ReadOnlySpan<byte> tailPattern,
        List<uint> headHits,
        List<uint> tailHits
    )
    {
        for (var sectionIndex = 0; sectionIndex < pe.SectionCount; sectionIndex++)
        {
            var section = pe.GetSection(sectionIndex);

            if (
                (section.Characteristics & ImageScnMemExecute) == 0
                || section.VirtualSize < sizeof(uint)
                || section.VirtualAddress >= pe.ImageSize
            )
            {
                continue;
            }

            var safeSize = pe.GetMappedSize(section);
            var span = new ReadOnlySpan<byte>(pe.Image + section.VirtualAddress, (int)Math.Min(safeSize, int.MaxValue));

            CollectOccurrences(span, section.VirtualAddress, headPattern, headHits);
            CollectOccurrences(span, section.VirtualAddress, tailPattern, tailHits);
        }
    }

    private static void CollectOccurrences(
        ReadOnlySpan<byte> haystack,
        uint baseRva,
        ReadOnlySpan<byte> needle,
        List<uint> hits
    )
    {
        var offset = 0;
        while (offset <= haystack.Length - needle.Length)
        {
            var found = haystack[offset..].IndexOf(needle);
            if (found < 0)
            {
                return;
            }

            offset += found;
            hits.Add(baseRva + (uint)offset);
            if (hits.Count > MaxMagicHits)
            {
                throw new InvalidDataException("魔数在可执行节中出现次数异常，拒绝继续定位解析器");
            }

            offset++;
        }
    }

    private static bool TryResolveFunction(
        PeImage pe,
        byte* functionTable,
        int functionCount,
        uint rva,
        out uint begin,
        out uint end,
        out uint unwind
    )
    {
        begin = 0;
        end = 0;
        unwind = 0;

        var low = 0;
        var high = functionCount - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var entry = functionTable + (middle * 12);
            var entryBegin = *(uint*)entry;
            var entryEnd = *(uint*)(entry + 4);

            if (rva < entryBegin)
            {
                high = middle - 1;
                continue;
            }

            if (rva >= entryEnd)
            {
                low = middle + 1;
                continue;
            }

            begin = entryBegin;
            end = entryEnd;
            unwind = *(uint*)(entry + 8);
            ValidateRuntimeFunction(pe, begin, end, unwind);
            FollowChainedUnwindInfo(pe, ref begin, ref end, ref unwind);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 大函数可能被拆成多个 RUNTIME_FUNCTION 片段，片段的 UNWIND_INFO 用
    /// UNW_FLAG_CHAININFO 指回主体。沿链回到真正的函数入口。
    /// </summary>
    private static void FollowChainedUnwindInfo(PeImage pe, ref uint begin, ref uint end, ref uint unwind)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            var info = pe.GetPointer(unwind, 4, "UNWIND_INFO 头");
            if (((info[0] >> 3) & UnwFlagChainInfo) == 0)
            {
                return;
            }

            var codeCount = info[2];
            var chainOffset = checked(4U + checked((uint)((codeCount + 1) & ~1) * 2U));
            var chainRva = checked(unwind + chainOffset);
            var chain = pe.GetPointer(chainRva, 12, "链式 RUNTIME_FUNCTION");
            begin = *(uint*)chain;
            end = *(uint*)(chain + 4);
            unwind = *(uint*)(chain + 8);
            ValidateRuntimeFunction(pe, begin, end, unwind);
        }

        throw new InvalidDataException("UNWIND_INFO 链超过 8 层，拒绝继续解析");
    }

    private static void ValidateRuntimeFunction(PeImage pe, uint begin, uint end, uint unwind)
    {
        if (begin >= end || !pe.ContainsRange(begin, end - begin) || !pe.ContainsRange(unwind, 4))
        {
            throw new InvalidDataException("异常目录包含越界的 RUNTIME_FUNCTION");
        }
    }

    /// <summary>
    /// 判断 <paramref name="magicRva"/> 处的 4 字节是否是某条比较指令的 imm32。
    /// 覆盖 <c>3D id</c>（cmp eax, imm32）和 <c>[REX] 81 /7 ... id</c>
    /// （cmp r/m32, imm32，含寄存器与内存两种寻址）。
    /// </summary>
    private static bool IsCompareImmediate(PeImage pe, uint magicRva)
    {
        if (
            magicRva >= 1
            && pe.ContainsRange(magicRva - 1, 1)
            && *pe.GetPointer(magicRva - 1, 1, "魔数比较指令") == 0x3D
        )
        {
            return true;
        }

        for (uint back = 2; back <= 11 && back <= magicRva; back++)
        {
            var instructionRva = magicRva - back;
            if (!pe.ContainsRange(instructionRva, back))
            {
                continue;
            }

            var instruction = new ReadOnlySpan<byte>(
                pe.GetPointer(instructionRva, back, "魔数比较指令"),
                checked((int)back)
            );
            var index = 0;

            if ((instruction[index] & 0xF0) == 0x40)
            {
                index++;
            }

            if ((uint)index >= (uint)instruction.Length || instruction[index] != 0x81)
            {
                continue;
            }

            index++;
            if ((uint)index >= (uint)instruction.Length)
            {
                continue;
            }

            var modrm = instruction[index];
            if ((modrm & 0x38) != 0x38)
            {
                continue;
            }

            index++;
            var mod = modrm >> 6;
            var rm = modrm & 0x07;

            if (mod != 3 && rm == 4)
            {
                index++;
            }

            if (mod == 1)
            {
                index += 1;
            }
            else if (mod == 2 || (mod == 0 && rm == 5))
            {
                index += 4;
            }

            if (index == instruction.Length)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 用 UNWIND_INFO 求出可以安全覆盖的入口字节数。
    ///
    /// UNWIND_CODE 的 CodeOffset 是"该操作对应指令之后那条指令的偏移"，也就是一个
    /// 确定的指令边界。逐条核对每个操作的实际机器码长度是否与它的 CodeOffset 连续
    /// 衔接，可以证明从函数入口到该边界之间只有 push 和栈分配指令——它们都不含
    /// RIP 相对寻址和相对跳转，可以原样搬进 trampoline。取第一个不小于 14 字节
    /// 的边界作为补丁长度。
    /// </summary>
    private static int ComputeRelocatablePatchSize(
        PeImage pe,
        uint functionRva,
        uint functionEndRva,
        uint unwindRva
    )
    {
        ValidateRuntimeFunction(pe, functionRva, functionEndRva, unwindRva);
        var info = pe.GetPointer(unwindRva, 4, "UNWIND_INFO 头");
        var version = (byte)(info[0] & 0x07);
        if (version != 1)
        {
            throw new InvalidDataException($"解析器的 UNWIND_INFO 版本为 {version}，当前只支持版本 1");
        }

        if ((info[3] & 0x0F) != 0)
        {
            throw new InvalidDataException("解析器使用帧指针序言，无法确认入口字节可以安全搬迁");
        }

        var codeCount = info[2];
        var codesRva = checked(unwindRva + 4U);
        var codeBytes = checked((uint)codeCount * 2U);
        var codes = pe.GetPointer(codesRva, codeBytes, "UNWIND_CODE");

        Span<int> operationSlots = stackalloc int[byte.MaxValue + 1];
        var operationCount = 0;
        var slot = 0;
        while (slot < codeCount)
        {
            var operation = (byte)(codes[(slot * 2) + 1] & 0x0F);
            var operationInfo = (byte)(codes[(slot * 2) + 1] >> 4);
            var slots = SlotsFor(operation, operationInfo);
            if (slots == 0 || slot > codeCount - slots)
            {
                throw new InvalidDataException($"解析器的 UNWIND_INFO 含有无效操作 {operation}");
            }

            operationSlots[operationCount++] = slot;
            slot += slots;
        }

        var consumed = 0;
        var functionLength = checked((int)(functionEndRva - functionRva));

        // UNWIND_CODE 按执行顺序倒序存放，倒着遍历即为序言的执行顺序。
        for (var index = operationCount - 1; index >= 0; index--)
        {
            var current = operationSlots[index];
            var endOffset = codes[current * 2];
            var operation = (byte)(codes[(current * 2) + 1] & 0x0F);
            var operationInfo = (byte)(codes[(current * 2) + 1] >> 4);

            if (consumed >= functionLength)
            {
                break;
            }

            var available = Math.Min(7, functionLength - consumed);
            var codeRva = checked(functionRva + checked((uint)consumed));
            var code = new ReadOnlySpan<byte>(
                pe.GetPointer(codeRva, checked((uint)available), "解析器函数序言"),
                available
            );
            if (!TryMeasureRelocatable(code, operation, operationInfo, out var length))
            {
                break;
            }

            if (consumed + length != endOffset)
            {
                // 两条 unwind 指令之间夹着未被 unwind 描述的指令（例如 RIP 相对的
                // 栈保护读取），无法证明这段字节可以搬迁。
                break;
            }

            consumed = endOffset;
            if (consumed >= JumpSize)
            {
                return consumed <= MaxPatchSize
                    ? consumed
                    : throw new InvalidDataException($"解析器序言的可搬迁长度为 {consumed} 字节，超出上限");
            }
        }

        throw new InvalidDataException($"解析器入口只有 {consumed} 字节可以安全搬迁，放不下 {JumpSize} 字节绝对跳转");
    }

    private static int SlotsFor(byte operation, byte operationInfo) =>
        operation switch
        {
            UwopPushNonvol or UwopAllocSmall or UwopSetFpreg or UwopPushMachframe => 1,
            UwopAllocLarge => operationInfo == 0 ? 2 : 3,
            UwopSaveNonvol or UwopSaveXmm128 => 2,
            UwopSaveNonvolFar or UwopSaveXmm128Far => 3,
            _ => 0,
        };

    /// <summary>
    /// 核对入口处的实际机器码是否正是该 unwind 操作应有的编码，并返回其长度。
    /// 只接受 push 和栈分配；其余操作一律拒绝，因为无法在不做完整反汇编的前提下
    /// 证明它们可以搬迁。
    /// </summary>
    private static bool TryMeasureRelocatable(
        ReadOnlySpan<byte> code,
        byte operation,
        byte operationInfo,
        out int length
    )
    {
        switch (operation)
        {
            case UwopPushNonvol when operationInfo >= 8:
                length = 2;
                return code.Length >= length
                    && code[0] == 0x41
                    && code[1] == (byte)(0x50 + operationInfo - 8);

            case UwopPushNonvol:
                length = 1;
                return code.Length >= length && code[0] == (byte)(0x50 + operationInfo);

            // sub rsp, imm8
            case UwopAllocSmall:
                length = 4;
                return code.Length >= length && code[0] == 0x48 && code[1] == 0x83 && code[2] == 0xEC;

            // sub rsp, imm32
            case UwopAllocLarge:
                length = 7;
                return code.Length >= length && code[0] == 0x48 && code[1] == 0x81 && code[2] == 0xEC;

            default:
                length = 0;
                return false;
        }
    }

    private static byte[] ToLittleEndianBytes(uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}
