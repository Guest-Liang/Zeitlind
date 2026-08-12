namespace Zeitlind.Hook.Common;

public readonly record struct PeSection(uint VirtualSize, uint VirtualAddress, uint Characteristics);

public readonly unsafe struct PeImage
{
    private const int MaximumSectionCount = 96;
    private const uint MinimumOptionalHeaderSize = 112;

    private readonly byte* _optionalHeader;
    private readonly uint _optionalHeaderSize;
    private readonly byte* _sectionTable;

    private PeImage(
        byte* image,
        byte* optionalHeader,
        uint optionalHeaderSize,
        byte* sectionTable,
        int sectionCount,
        uint imageSize
    )
    {
        Image = image;
        _optionalHeader = optionalHeader;
        _optionalHeaderSize = optionalHeaderSize;
        _sectionTable = sectionTable;
        SectionCount = sectionCount;
        ImageSize = imageSize;
    }

    public byte* Image { get; }

    public int SectionCount { get; }

    public uint ImageSize { get; }

    public bool ContainsRange(uint rva, uint size)
    {
        return rva <= ImageSize && size <= ImageSize - rva;
    }

    public byte* GetPointer(uint rva, uint size, string description)
    {
        if (!ContainsRange(rva, size))
        {
            throw new InvalidDataException($"{description} 超出 GameAssembly.dll 映像边界");
        }

        return Image + rva;
    }

    public bool TryGetDataDirectory(int index, out uint rva, out uint size)
    {
        rva = 0;
        size = 0;
        if (index < 0 || _optionalHeaderSize < MinimumOptionalHeaderSize)
        {
            return false;
        }

        var directoryCount = *(uint*)(_optionalHeader + 108);
        var offset = checked(112U + checked((uint)index * 8U));
        if ((uint)index >= directoryCount || offset > _optionalHeaderSize || 8U > _optionalHeaderSize - offset)
        {
            return false;
        }

        var directory = _optionalHeader + offset;
        rva = *(uint*)directory;
        size = *(uint*)(directory + 4);
        return true;
    }

    public PeSection GetSection(int index)
    {
        if ((uint)index >= (uint)SectionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var section = _sectionTable + (index * 40);
        return new PeSection(*(uint*)(section + 8), *(uint*)(section + 12), *(uint*)(section + 36));
    }

    public uint GetMappedSize(PeSection section)
    {
        return section.VirtualAddress < ImageSize
            ? Math.Min(section.VirtualSize, ImageSize - section.VirtualAddress)
            : 0;
    }

    public static PeImage Open(nint moduleBase, string moduleName)
    {
        if (moduleBase == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(moduleBase));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        if (
            !NativeMethods.GetModuleInformation(
                NativeMethods.GetCurrentProcess(),
                moduleBase,
                out var moduleInformation,
                checked((uint)sizeof(NativeMethods.ModuleInformation))
            )
            || moduleInformation.BaseOfDll != moduleBase
            || moduleInformation.SizeOfImage < 64
        )
        {
            throw new InvalidDataException($"无法确定 {moduleName} 的已映射边界");
        }

        var mappedSize = moduleInformation.SizeOfImage;
        var image = (byte*)moduleBase;
        if (*(ushort*)image != 0x5A4D)
        {
            throw new InvalidDataException($"{moduleName} 不含有效的 DOS 头");
        }

        var ntOffsetValue = *(int*)(image + 0x3C);
        if (ntOffsetValue <= 0)
        {
            throw new InvalidDataException($"{moduleName} 的 PE 头偏移无效");
        }

        var ntOffset = checked((uint)ntOffsetValue);
        if (!ContainsRange(mappedSize, ntOffset, 24) || *(uint*)(image + ntOffset) != 0x0000_4550)
        {
            throw new InvalidDataException($"{moduleName} 不含有效的 PE 头");
        }

        var fileHeaderOffset = ntOffset + sizeof(uint);
        var fileHeader = image + fileHeaderOffset;
        var sectionCount = *(ushort*)(fileHeader + 2);
        var optionalHeaderSize = *(ushort*)(fileHeader + 16);
        if (sectionCount is 0 or > MaximumSectionCount || optionalHeaderSize < MinimumOptionalHeaderSize)
        {
            throw new InvalidDataException($"{moduleName} 的 PE 头尺寸无效");
        }

        var optionalHeaderOffset = checked(fileHeaderOffset + 20U);
        if (!ContainsRange(mappedSize, optionalHeaderOffset, optionalHeaderSize))
        {
            throw new InvalidDataException($"{moduleName} 的可选头超出映像边界");
        }

        var optionalHeader = image + optionalHeaderOffset;
        if (*(ushort*)optionalHeader != 0x020B)
        {
            throw new InvalidDataException($"{moduleName} 不是 PE32+ 映像");
        }

        var imageSize = *(uint*)(optionalHeader + 56);
        if (imageSize < 64 || imageSize > mappedSize)
        {
            throw new InvalidDataException($"{moduleName} 的 SizeOfImage 无效");
        }

        var sectionTableOffset = checked(optionalHeaderOffset + optionalHeaderSize);
        var sectionTableSize = checked((uint)sectionCount * 40U);
        if (!ContainsRange(imageSize, sectionTableOffset, sectionTableSize))
        {
            throw new InvalidDataException($"{moduleName} 的节表超出映像边界");
        }

        return new PeImage(
            image,
            optionalHeader,
            optionalHeaderSize,
            image + sectionTableOffset,
            sectionCount,
            imageSize
        );
    }

    private static bool ContainsRange(uint totalSize, uint offset, uint size)
    {
        return offset <= totalSize && size <= totalSize - offset;
    }
}
