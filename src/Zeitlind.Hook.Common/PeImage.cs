namespace Zeitlind.Hook.Common;

public readonly record struct PeSection(uint VirtualSize, uint VirtualAddress, uint Characteristics);

public readonly unsafe struct PeImage
{
    private readonly byte* sectionTable;

    private PeImage(byte* image, byte* optionalHeader, byte* sectionTable, int sectionCount, uint imageSize)
    {
        Image = image;
        OptionalHeader = optionalHeader;
        this.sectionTable = sectionTable;
        SectionCount = sectionCount;
        ImageSize = imageSize;
    }

    public byte* Image { get; }

    public byte* OptionalHeader { get; }

    public int SectionCount { get; }

    public uint ImageSize { get; }

    public PeSection GetSection(int index)
    {
        if ((uint)index >= (uint)SectionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var section = sectionTable + (index * 40);
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
        var image = (byte*)moduleBase;
        if (*(ushort*)image != 0x5A4D)
        {
            throw new InvalidDataException($"{moduleName} 不含有效的 DOS 头");
        }

        var ntOffset = *(int*)(image + 0x3C);
        if (ntOffset <= 0 || *(uint*)(image + ntOffset) != 0x0000_4550)
        {
            throw new InvalidDataException($"{moduleName} 不含有效的 PE 头");
        }

        var fileHeader = image + ntOffset + sizeof(uint);
        var sectionCount = *(ushort*)(fileHeader + 2);
        var optionalHeaderSize = *(ushort*)(fileHeader + 16);
        var optionalHeader = fileHeader + 20;
        if (*(ushort*)optionalHeader != 0x020B)
        {
            throw new InvalidDataException($"{moduleName} 不是 PE32+ 映像");
        }

        var imageSize = *(uint*)(optionalHeader + 56);
        var sectionTable = optionalHeader + optionalHeaderSize;
        return new PeImage(image, optionalHeader, sectionTable, sectionCount, imageSize);
    }
}
