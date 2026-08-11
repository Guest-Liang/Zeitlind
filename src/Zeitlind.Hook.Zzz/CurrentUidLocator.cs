using System.Buffers.Binary;
using System.Text;
using Zeitlind.Hook.Common;

namespace Zeitlind.Hook.Zzz;

/// <summary>
/// 从 UID getter 的机器码中恢复出的对象布局，供只读 reader 使用。
/// </summary>
internal readonly record struct CurrentUidObjectLayout(
    int ClassInitializedFlagOffset,
    int FirstClassLinkOffset,
    int SecondClassOffset,
    int StaticInstanceSlotOffset,
    int CachedServiceOffset,
    int UidOffset
)
{
    public override string ToString()
    {
        return $"class-init +0x{ClassInitializedFlagOffset:X}, "
            + $"class-link +0x{FirstClassLinkOffset:X}/+0x{SecondClassOffset:X}, "
            + $"static-slot +0x{StaticInstanceSlotOffset:X}, "
            + $"service +0x{CachedServiceOffset:X}, UID +0x{UidOffset:X}";
    }
}

/// <summary>
/// 当前玩家 UID 的运行时 RootSlot。RVA 只用于诊断；读取时始终使用已经解析出的地址。
/// </summary>
internal readonly record struct CurrentUidLocation(
    nint RootSlotAddress,
    uint RootSlotRva,
    uint ServiceTypeSlotRva,
    CurrentUidObjectLayout ObjectLayout,
    int EquivalentPathCount
);

/// <summary>
/// 从 GameAssembly.dll 的代码结构中定位当前玩家 UID 所依赖的运行时 RootSlot。
///
/// 定位锚定 3.1 getter正常路径中的寄存器流，从已经核对过的内存操作数中恢复并验证对象布局：
///
/// class -> first link -> second class -> static owner -> UID service -> uint32 UID
///
/// 同一 getter 可能存在原生与 HybridCLR 等价代码路径，所以允许多个代码命中；
/// 但它们必须全部归一到同一个根全局槽、服务类型槽和对象布局，否则失败关闭。
/// </summary>
internal static unsafe class CurrentUidLocator
{
    public const int LocatorVersion = 2;

    private const int CandidateLength = 0x8E;
    private const int MarkerOffset = 0x07;
    private const int ServiceLoadOffset = 0x68;
    private const int RipRelativeInstructionLength = 7;
    private const int MaximumCandidates = 64;

    private static ReadOnlySpan<byte> Marker => [0x48, 0x8B, 0x30, 0xF6, 0x86, 0xCC, 0x00, 0x00, 0x00, 0x01];

    private static readonly CurrentUidObjectLayout Version31ObjectLayout = new(0xCC, 0x68, 0x10, 0x60, 0x38, 0x40);

    private readonly record struct Candidate(
        uint CodeRva,
        uint RootSlotRva,
        uint ServiceTypeSlotRva,
        CurrentUidObjectLayout ObjectLayout
    );

    public static CurrentUidLocation Locate(nint moduleBase)
    {
        var pe = PeImage.Open(moduleBase, "GameAssembly.dll");
        var image = pe.Image;
        var candidates = new List<Candidate>();

        ScanExecutableSections(pe, candidates);

        if (candidates.Count == 0)
        {
            throw new InvalidDataException("找不到符合当前玩家 UID getter 对象布局的代码路径；游戏结构可能已经改变");
        }

        var selected = candidates[0];
        foreach (var candidate in candidates)
        {
            if (
                candidate.RootSlotRva != selected.RootSlotRva
                || candidate.ServiceTypeSlotRva != selected.ServiceTypeSlotRva
                || candidate.ObjectLayout != selected.ObjectLayout
            )
            {
                throw new InvalidDataException(DescribeAmbiguousCandidates(candidates));
            }
        }

        return new CurrentUidLocation(
            (nint)(image + selected.RootSlotRva),
            selected.RootSlotRva,
            selected.ServiceTypeSlotRva,
            selected.ObjectLayout,
            candidates.Count
        );
    }

    private static void ScanExecutableSections(PeImage pe, List<Candidate> candidates)
    {
        for (var sectionIndex = 0; sectionIndex < pe.SectionCount; sectionIndex++)
        {
            var section = pe.GetSection(sectionIndex);
            if (
                (section.Characteristics & NativeMethods.ImageScnMemExecute) == 0
                || section.VirtualSize < CandidateLength
                || section.VirtualAddress >= pe.ImageSize
            )
            {
                continue;
            }

            var safeSize = pe.GetMappedSize(section);
            var span = new ReadOnlySpan<byte>(pe.Image + section.VirtualAddress, checked((int)safeSize));
            ScanSection(span, section.VirtualAddress, pe, candidates);
        }
    }

    private static void ScanSection(ReadOnlySpan<byte> section, uint sectionRva, PeImage pe, List<Candidate> candidates)
    {
        var searchOffset = 0;
        while (searchOffset <= section.Length - Marker.Length)
        {
            var relative = section[searchOffset..].IndexOf(Marker);
            if (relative < 0)
            {
                return;
            }

            var markerOffset = searchOffset + relative;
            var candidateOffset = markerOffset - MarkerOffset;
            if (
                candidateOffset >= 0
                && candidateOffset <= section.Length - CandidateLength
                && TryDecodeUidGetterPath(section, candidateOffset, out var objectLayout)
            )
            {
                var codeRva = sectionRva + (uint)candidateOffset;
                if (
                    TryDecodeRipTarget(
                        section,
                        candidateOffset,
                        codeRva,
                        instructionOffset: 0,
                        pe.ImageSize,
                        out var rootSlotRva
                    )
                    && TryDecodeRipTarget(
                        section,
                        candidateOffset,
                        codeRva,
                        ServiceLoadOffset,
                        pe.ImageSize,
                        out var serviceTypeSlotRva
                    )
                    && IsWritableDataSlot(pe, rootSlotRva)
                    && IsWritableDataSlot(pe, serviceTypeSlotRva)
                )
                {
                    candidates.Add(new Candidate(codeRva, rootSlotRva, serviceTypeSlotRva, objectLayout));
                    if (candidates.Count > MaximumCandidates)
                    {
                        throw new InvalidDataException("当前玩家 UID getter 的结构命中数量异常，拒绝继续定位");
                    }
                }
            }

            searchOffset = markerOffset + 1;
        }
    }

    private static bool TryDecodeUidGetterPath(
        ReadOnlySpan<byte> code,
        int start,
        out CurrentUidObjectLayout objectLayout
    )
    {
        objectLayout = default;

        // RIP 相对地址、call/jump 位移会随重编译变化，故只核对操作码、寄存器流、
        // 两处 IL2CPP 初始化标志偏移以及 getter 的对象字段偏移直接从指令操作数解出。
        if (
            !Matches(code, start + 0x00, [0x48, 0x8B, 0x05])
            || !Matches(code, start + 0x07, [0x48, 0x8B, 0x30, 0xF6, 0x86])
            || code[start + 0x10] != 1
            || !Matches(code, start + 0x11, [0x0F, 0x84])
            || !Matches(code, start + 0x17, [0x48, 0x8B, 0x46])
            || !Matches(code, start + 0x1B, [0x48, 0x8B, 0x78])
            || !Matches(code, start + 0x1F, [0x48, 0x85, 0xFF, 0x0F, 0x84])
            || !Matches(code, start + 0x28, [0xF6, 0x87])
            || code[start + 0x2E] != 1
            || !Matches(code, start + 0x2F, [0x0F, 0x84])
            || !Matches(code, start + 0x35, [0x48, 0x8B, 0x47])
            || !Matches(code, start + 0x39, [0x48, 0x8B, 0x30, 0x48, 0x85, 0xF6, 0x0F, 0x84])
            || !Matches(code, start + 0x45, [0x80, 0x3D])
            || code[start + 0x4B] != 0
            || !Matches(code, start + 0x4C, [0x0F, 0x84])
            || !Matches(code, start + 0x52, [0x80, 0x3D])
            || code[start + 0x58] != 0
            || !Matches(code, start + 0x59, [0x0F, 0x85])
            || !Matches(code, start + 0x5F, [0x48, 0x8B, 0x46])
            || !Matches(code, start + 0x63, [0x48, 0x85, 0xC0, 0x75])
            || !Matches(code, start + 0x68, [0x48, 0x8B, 0x15])
            || !Matches(code, start + 0x6F, [0x48, 0x89, 0xF1, 0xE8])
            || !Matches(code, start + 0x77, [0x48, 0x89, 0x46])
            || !Matches(code, start + 0x7B, [0x48, 0x85, 0xC0, 0x0F, 0x84])
            || !Matches(code, start + 0x84, [0x8B, 0x40])
            || !Matches(code, start + 0x87, [0x48, 0x83, 0xC4, 0x28, 0x5F, 0x5E, 0xC3])
        )
        {
            return false;
        }

        var classInitializedFlagOffset = BinaryPrimitives.ReadInt32LittleEndian(code.Slice(start + 0x0C, sizeof(int)));
        var secondClassInitializedFlagOffset = BinaryPrimitives.ReadInt32LittleEndian(
            code.Slice(start + 0x2A, sizeof(int))
        );
        var firstClassLinkOffset = (sbyte)code[start + 0x1A];
        var secondClassOffset = (sbyte)code[start + 0x1E];
        var staticInstanceSlotOffset = (sbyte)code[start + 0x38];
        var cachedServiceOffset = (sbyte)code[start + 0x62];
        var cachedServiceStoreOffset = (sbyte)code[start + 0x7A];
        var uidOffset = (sbyte)code[start + 0x86];

        if (
            classInitializedFlagOffset != secondClassInitializedFlagOffset
            || !IsSaneFieldOffset(classInitializedFlagOffset, sizeof(int), 0x400)
            || !IsSaneFieldOffset(firstClassLinkOffset, sizeof(nint), sbyte.MaxValue)
            || !IsSaneFieldOffset(secondClassOffset, sizeof(nint), sbyte.MaxValue)
            || !IsSaneFieldOffset(staticInstanceSlotOffset, sizeof(nint), sbyte.MaxValue)
            || !IsSaneFieldOffset(cachedServiceOffset, sizeof(nint), sbyte.MaxValue)
            || cachedServiceOffset != cachedServiceStoreOffset
            || !IsSaneFieldOffset(uidOffset, sizeof(uint), sbyte.MaxValue)
        )
        {
            return false;
        }

        objectLayout = new CurrentUidObjectLayout(
            classInitializedFlagOffset,
            firstClassLinkOffset,
            secondClassOffset,
            staticInstanceSlotOffset,
            cachedServiceOffset,
            uidOffset
        );
        return objectLayout == Version31ObjectLayout;
    }

    private static bool IsSaneFieldOffset(int offset, int alignment, int maximum)
    {
        return offset > 0 && offset <= maximum && offset % alignment == 0;
    }

    private static bool Matches(ReadOnlySpan<byte> code, int offset, ReadOnlySpan<byte> expected)
    {
        return offset >= 0
            && offset <= code.Length - expected.Length
            && code.Slice(offset, expected.Length).SequenceEqual(expected);
    }

    private static bool TryDecodeRipTarget(
        ReadOnlySpan<byte> section,
        int candidateOffset,
        uint candidateRva,
        int instructionOffset,
        uint imageSize,
        out uint targetRva
    )
    {
        var displacementOffset = candidateOffset + instructionOffset + 3;
        var displacement = BinaryPrimitives.ReadInt32LittleEndian(section.Slice(displacementOffset, sizeof(int)));
        var target = (long)candidateRva + instructionOffset + RipRelativeInstructionLength + displacement;
        if (target < 0 || target > imageSize - sizeof(nint))
        {
            targetRva = 0;
            return false;
        }

        targetRva = (uint)target;
        return true;
    }

    private static bool IsWritableDataSlot(PeImage pe, uint rva)
    {
        for (var sectionIndex = 0; sectionIndex < pe.SectionCount; sectionIndex++)
        {
            var section = pe.GetSection(sectionIndex);
            if (
                (section.Characteristics & NativeMethods.ImageScnMemRead) == 0
                || (section.Characteristics & NativeMethods.ImageScnMemWrite) == 0
                || (section.Characteristics & NativeMethods.ImageScnMemExecute) != 0
                || section.VirtualAddress >= pe.ImageSize
            )
            {
                continue;
            }

            var sectionEnd = (ulong)section.VirtualAddress + pe.GetMappedSize(section);
            if (rva >= section.VirtualAddress && (ulong)rva + (uint)sizeof(nint) <= sectionEnd)
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeAmbiguousCandidates(List<Candidate> candidates)
    {
        var description = new StringBuilder();
        foreach (var candidate in candidates)
        {
            if (description.Length != 0)
            {
                description.Append('、');
            }

            description.Append(
                $"代码 RVA 0x{candidate.CodeRva:X} → "
                    + $"RootSlot 0x{candidate.RootSlotRva:X} / "
                    + $"ServiceTypeSlot 0x{candidate.ServiceTypeSlotRva:X} / "
                    + $"布局 {candidate.ObjectLayout}"
            );
        }

        return $"找到多个不同的当前玩家 UID getter 目标，无法安全选择：{description}";
    }
}
