// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>The groups the treemap colors files by. The group comes from the file extension only; no file is opened.</summary>
public enum DiskFileKind { Folder, Documents, Images, Video, Audio, Archives, Code, Programs, SystemData, Other, Unlisted, Unreadable }

public static class DiskFileKinds
{
    private static readonly Dictionary<string, DiskFileKind> ByExtension = Build();

    private static Dictionary<string, DiskFileKind> Build()
    {
        var map = new Dictionary<string, DiskFileKind>(StringComparer.Ordinal);
        void Add(DiskFileKind kind, string extensions)
        {
            foreach (var e in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map[e] = kind;
        }
        Add(DiskFileKind.Documents, ".doc .docx .docm .dot .dotx .xls .xlsx .xlsm .xlsb .ppt .pptx .pps .ppsx .pdf .txt .rtf .odt .ods .odp .csv .md .epub .mobi .xps .one .pub .vsd .vsdx .log");
        Add(DiskFileKind.Images, ".jpg .jpeg .png .gif .bmp .tif .tiff .webp .heic .heif .svg .ico .psd .raw .cr2 .nef .arw .dng .avif .jfif");
        Add(DiskFileKind.Video, ".mp4 .mkv .avi .mov .wmv .flv .webm .m4v .mpg .mpeg .ts .m2ts .vob .3gp");
        Add(DiskFileKind.Audio, ".mp3 .wav .flac .aac .ogg .oga .wma .m4a .opus .aiff .mid .midi");
        Add(DiskFileKind.Archives, ".zip .rar .7z .tar .gz .tgz .bz2 .xz .cab .iso .img .vhd .vhdx .vmdk .wim .esd .msi .msix .appx .msp .dmg .zst");
        Add(DiskFileKind.Code, ".cs .vb .fs .c .h .cpp .hpp .cc .java .kt .py .js .jsx .mjs .ts .tsx .json .xml .xaml .html .htm .css .scss .ps1 .psm1 .bat .cmd .sh .go .rs .rb .php .sql .yml .yaml .toml .ini .config .csproj .sln .slnx .pdb .obj .lib .o .a .class .jar .map");
        Add(DiskFileKind.Programs, ".exe .dll .ocx .scr .com .msc .cpl .ax .winmd .node .so");
        Add(DiskFileKind.SystemData, ".sys .dat .db .sqlite .sqlite3 .mui .etl .evtx .dmp .tmp .bak .cache .regtrans-ms .blf .manifest .cat .inf .mum .nls .ttf .otf .ttc .fon .lnk .pf .edb .jrs .chk");
        return map;
    }

    public static DiskFileKind Classify(DiskNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Kind switch
        {
            DiskNodeKind.Directory => DiskFileKind.Folder,
            DiskNodeKind.Aggregate => DiskFileKind.Unlisted,
            DiskNodeKind.Unreadable => DiskFileKind.Unreadable,
            _ => ByExtension.TryGetValue(node.Extension, out var kind) ? kind : DiskFileKind.Other,
        };
    }
}
