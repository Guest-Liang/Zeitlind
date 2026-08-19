using Zeitlind.Core.Interop;
using Zeitlind.Hook.Common;

namespace Zeitlind.Hook.Genshin;

internal readonly unsafe struct GenshinPacketDecompress
{
    private static ReadOnlySpan<byte> CallSitePattern =>
        [
            0x48,
            0x8B,
            0x0D, // mov rcx, [rip+disp32]
            0x00,
            0x00,
            0x00,
            0x00,
            0x48,
            0x8B,
            0x15, // mov rdx, [rip+disp32]
            0x00,
            0x00,
            0x00,
            0x00,
            0x4C,
            0x63,
            0xCE, // movsxd r9, esi
            0x48,
            0x89,
            0x7C,
            0x24,
            0x28, // mov [rsp+28h], rdi
            0x48,
            0x89,
            0x44,
            0x24,
            0x20, // mov [rsp+20h], rax
            0xE8, // call rel32
            0x00,
            0x00,
            0x00,
            0x00,
            0x85,
            0xC0, // test eax, eax
        ];

    private readonly delegate* unmanaged<nint, nint, byte*, uint, void*, uint, byte> _decompress;
    private readonly nint _tcpStatePtr;
    private readonly nint _sharedInfoPtr;

    private GenshinPacketDecompress(
        delegate* unmanaged<nint, nint, byte*, uint, void*, uint, byte> decompress,
        nint tcpStatePtr,
        nint sharedInfoPtr
    )
    {
        _decompress = decompress;
        _tcpStatePtr = tcpStatePtr;
        _sharedInfoPtr = sharedInfoPtr;
    }

    public static GenshinPacketDecompress Locate(nint moduleBase, ParserLocation parser)
    {
        if (moduleBase == 0 || parser.Address == 0)
        {
            throw new InvalidDataException("原神解析器地址无效，无法定位解压函数");
        }

        var pe = PeImage.Open(moduleBase, "YuanShen.exe");
        var parserRva = parser.Rva;
        if (!pe.ContainsRange(parserRva, 34))
        {
            throw new InvalidDataException("原神解析器范围过小，无法扫描解压调用");
        }

        var functionEnd = FindFunctionEnd(pe, parserRva);
        var functionLength = checked((int)(functionEnd - parserRva));
        var body = new ReadOnlySpan<byte>(pe.GetPointer(parserRva, (uint)functionLength, "原神解析器"), functionLength);
        var pattern = CallSitePattern;
        var hit = -1;
        for (var offset = 0; offset <= body.Length - pattern.Length; offset++)
        {
            if (!IsCallSite(body.Slice(offset, pattern.Length)))
            {
                continue;
            }

            if (hit >= 0)
            {
                throw new InvalidDataException("原神解析器中找到多处解压调用，需要重新分析当前版本");
            }

            hit = offset;
        }

        if (hit < 0)
        {
            throw new InvalidDataException("原神解析器中找不到解压调用，网络压缩实现可能已经改变");
        }

        var site = (byte*)parser.Address + hit;
        var tcpStatePtr = ReadRipRelative(site, 3);
        var sharedInfoPtr = ReadRipRelative(site + 7, 3);
        var call = site + 27;
        var decompress = (nint)(call + 5 + *(int*)(call + 1));
        var image = (byte*)moduleBase;
        var imageSize = (nuint)pe.ImageSize;
        var pointerSize = (nuint)sizeof(nint);
        if (
            tcpStatePtr < (nint)image
            || sharedInfoPtr < (nint)image
            || decompress < (nint)image
            || (nuint)(tcpStatePtr - (nint)image) > imageSize - pointerSize
            || (nuint)(sharedInfoPtr - (nint)image) > imageSize - pointerSize
            || (nuint)(decompress - (nint)image) >= imageSize
        )
        {
            throw new InvalidDataException("原神解压函数或上下文指针超出 YuanShen.exe 映像");
        }

        return new GenshinPacketDecompress(
            (delegate* unmanaged<nint, nint, byte*, uint, void*, uint, byte>)decompress,
            tcpStatePtr,
            sharedInfoPtr
        );
    }

    public bool TryDecompress(ReadOnlySpan<byte> source, uint unzipLength, out byte[] destination)
    {
        destination = [];
        if (
            _decompress == null
            || unzipLength == 0
            || unzipLength > HookProtocol.MaximumPacketBodyLength
            || source.Length == 0
        )
        {
            return false;
        }

        var tcpState = *(nint*)_tcpStatePtr;
        var sharedInfo = *(nint*)_sharedInfoPtr;
        if (tcpState == 0 || sharedInfo == 0)
        {
            return false;
        }

        var output = GC.AllocateUninitializedArray<byte>(checked((int)unzipLength));
        fixed (byte* sourcePointer = source)
        fixed (byte* destinationPointer = output)
        {
            if (
                _decompress(tcpState, sharedInfo, sourcePointer, (uint)source.Length, destinationPointer, unzipLength)
                == 0
            )
            {
                return false;
            }
        }

        destination = output;
        return true;
    }

    private static bool IsCallSite(ReadOnlySpan<byte> window)
    {
        var pattern = CallSitePattern;
        for (var index = 0; index < pattern.Length; index++)
        {
            if (index is 3 or 4 or 5 or 6 or 10 or 11 or 12 or 13 or 28 or 29 or 30 or 31)
            {
                continue;
            }

            if (window[index] != pattern[index])
            {
                return false;
            }
        }

        return true;
    }

    private static nint ReadRipRelative(byte* instruction, int displacementOffset)
    {
        var displacement = *(int*)(instruction + displacementOffset);
        return (nint)(instruction + 7 + displacement);
    }

    private static uint FindFunctionEnd(PeImage pe, uint parserRva)
    {
        if (!pe.TryGetDataDirectory(3, out var exceptionRva, out var exceptionSize) || exceptionSize < 12)
        {
            throw new InvalidDataException("YuanShen.exe 没有异常目录，无法确定解析器长度");
        }

        var functionTable = pe.GetPointer(exceptionRva, exceptionSize, "异常目录");
        var functionCount = checked((int)(exceptionSize / 12));
        var low = 0;
        var high = functionCount - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var entry = functionTable + (middle * 12);
            var begin = *(uint*)entry;
            var end = *(uint*)(entry + 4);
            if (parserRva < begin)
            {
                high = middle - 1;
                continue;
            }

            if (parserRva >= end)
            {
                low = middle + 1;
                continue;
            }

            return end;
        }

        throw new InvalidDataException("异常目录中找不到原神解析器边界");
    }
}
