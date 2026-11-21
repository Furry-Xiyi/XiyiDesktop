using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Buffers;
using System.Text;
using System.Xml;

namespace XiyiDesktop.DesktopItems;

internal enum ShellNewContentKind
{
    NullFile,
    FileName,
    Data,
    GeneratedOffice,
    Shortcut
}

internal sealed record ShellNewTemplate(
    string Extension,
    string ResourceKey,
    string DefaultNameResourceKey,
    string IconGlyph,
    ShellNewContentKind ContentKind,
    string? TemplatePath = null,
    byte[]? Data = null,
    string? ShellMenuText = null);

internal sealed class ShellNewDestinationConflictException : IOException
{
    public ShellNewDestinationConflictException(string path, Exception innerException)
        : base($"A desktop item already exists at '{path}'.", innerException)
    {
    }
}

/// <summary>
/// Discovers only ShellNew registrations that can be materialized as real files.
/// Shell handlers and command registrations are deliberately excluded.
/// </summary>
internal static class ShellNewTemplateCatalog
{
    private static readonly Lazy<IReadOnlyList<ShellNewTemplate>> SupportedTemplates =
        new(DiscoverSupportedTemplates, isThreadSafe: true);

    private static readonly (string Extension, string ResourceKey, string DefaultNameResourceKey, string IconGlyph)[] Candidates =
    new[]
    {
        (".lnk", "DesktopContext_NewShortcut.Text", "DesktopContext_NewShortcutName", "\uE8A7"),
        (".bmp", "DesktopContext_NewBitmap.Text", "DesktopContext_NewBitmapName", "\uEB9F"),
        (".docx", "DesktopContext_NewWord.Text", "DesktopContext_NewWordName", "\uE8A5"),
        (".pptx", "DesktopContext_NewPowerPoint.Text", "DesktopContext_NewPowerPointName", "\uE8A5"),
        (".txt", "DesktopContext_NewTextDocument.Text", "DesktopContext_NewTextDocumentName", "\uE8A5"),
        (".xlsx", "DesktopContext_NewExcel.Text", "DesktopContext_NewExcelName", "\uE8A5"),
        (".zip", "DesktopContext_NewCompressedFolder.Text", "DesktopContext_NewCompressedFolderName", "\uE8B7")
    };

    public static IReadOnlyList<ShellNewTemplate> GetSupportedTemplates() => SupportedTemplates.Value;

    private static IReadOnlyList<ShellNewTemplate> DiscoverSupportedTemplates()
    {
        var templates = new List<ShellNewTemplate>();
        foreach (var candidate in Candidates)
        {
            // A plain text document is safely creatable as an empty file, even on a
            // system that does not register a ShellNew class for .txt.
            if (string.Equals(candidate.Extension, ".txt", StringComparison.OrdinalIgnoreCase))
            {
                templates.Add(new ShellNewTemplate(
                    candidate.Extension,
                    candidate.ResourceKey,
                    candidate.DefaultNameResourceKey,
                    candidate.IconGlyph,
                    ShellNewContentKind.NullFile));
                continue;
            }

            var registeredTemplate = ReadRegisteredTemplate(candidate.Extension);
            if (registeredTemplate is not null)
            {
                templates.Add(registeredTemplate with
                {
                    ResourceKey = candidate.ResourceKey,
                    DefaultNameResourceKey = candidate.DefaultNameResourceKey,
                    IconGlyph = candidate.IconGlyph
                });
            }
        }

        return templates;
    }

    public static void Create(ShellNewTemplate template, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!string.Equals(Path.GetExtension(destinationPath), template.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The file extension does not match the selected ShellNew template.", nameof(destinationPath));

        var destinationCreated = false;
        try
        {
            FileStream destinationStream;
            try
            {
                destinationStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException exception) when (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                throw new ShellNewDestinationConflictException(destinationPath, exception);
            }

            using var destination = destinationStream;
            destinationCreated = true;
            switch (template.ContentKind)
            {
                case ShellNewContentKind.NullFile:
                    if (!string.Equals(template.Extension, ".txt", StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException("Only empty text files are accepted as NullFile templates.");
                    break;
                case ShellNewContentKind.FileName:
                    if (string.IsNullOrWhiteSpace(template.TemplatePath))
                        throw new InvalidDataException("The ShellNew template file path is missing.");
                    using (var source = new FileStream(template.TemplatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        source.CopyTo(destination);
                    break;
                case ShellNewContentKind.Data:
                    if (template.Data is null)
                        throw new InvalidDataException("The ShellNew data template is missing.");
                    destination.Write(template.Data);
                    break;
                case ShellNewContentKind.GeneratedOffice:
                    WriteBlankOfficeDocument(destination, template.Extension);
                    break;
                case ShellNewContentKind.Shortcut:
                    throw new NotSupportedException("Shortcut templates require a target and must use DesktopShortcutService.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(template));
            }
        }
        catch
        {
            if (destinationCreated)
            {
                try { File.Delete(destinationPath); } catch { }
            }
            throw;
        }
    }

    private static ShellNewTemplate? ReadRegisteredTemplate(string extension)
    {
        try
        {
            using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension, writable: false);
            var template = ReadTemplateFromClassTree(extensionKey, extension);
            if (template is not null)
                return template;

            var progId = extensionKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(progId))
                return null;

            using var progIdKey = Registry.ClassesRoot.OpenSubKey(progId, writable: false);
            return ReadTemplateFromClassTree(progIdKey, extension);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to inspect ShellNew registration for {extension}: {exception.Message}");
            return null;
        }
    }

    private static ShellNewTemplate? ReadTemplateFromClassTree(RegistryKey? classKey, string extension)
    {
        var template = ReadTemplateFromKey(classKey, extension);
        if (template is not null || classKey is null)
            return template;

        // Windows file associations commonly nest the class-specific ShellNew key
        // under the extension key, e.g. .docx\Word.Document.12\ShellNew.
        foreach (var childName in classKey.GetSubKeyNames())
        {
            using var child = classKey.OpenSubKey(childName, writable: false);
            template = ReadTemplateFromKey(child, extension);
            if (template is not null)
                return template;
        }

        return null;
    }

    private static ShellNewTemplate? ReadTemplateFromKey(RegistryKey? classKey, string extension)
    {
        using var shellNew = classKey?.OpenSubKey("ShellNew", writable: false);
        if (shellNew is null)
            return null;

        // Shell's .lnk template is provided by a handler. The app creates a real
        // shortcut with its own target prompt instead of copying an empty file.
        if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) &&
            shellNew.GetValueNames().Contains("Handler", StringComparer.OrdinalIgnoreCase))
        {
            return new ShellNewTemplate(extension, string.Empty, string.Empty, string.Empty, ShellNewContentKind.Shortcut);
        }

        // FileName contains a real template file. Resolve only concrete existing files;
        // command-style and indirect resource strings are not executed.
        if (TryReadTemplateFile(shellNew, extension, out var templatePath))
            return new ShellNewTemplate(extension, string.Empty, string.Empty, string.Empty, ShellNewContentKind.FileName, templatePath);

        // Office Click-to-Run can register zero-byte VFS ShellNew placeholders for
        // Word and PowerPoint. Keep those entries available by creating a valid,
        // minimal OOXML package instead of hiding the menu items or copying an
        // unusable empty file.
        if (shellNew.GetValueNames().Contains("FileName", StringComparer.OrdinalIgnoreCase) &&
            (string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(extension, ".pptx", StringComparison.OrdinalIgnoreCase)))
        {
            return new ShellNewTemplate(extension, string.Empty, string.Empty, string.Empty, ShellNewContentKind.GeneratedOffice);
        }

        if (TryReadTemplateData(shellNew, extension, out var data))
            return new ShellNewTemplate(extension, string.Empty, string.Empty, string.Empty, ShellNewContentKind.Data, Data: data);

        // NullFile has defined useful content only for plain text in this catalog.
        if (string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) &&
            shellNew.GetValueNames().Contains("NullFile", StringComparer.OrdinalIgnoreCase) &&
            shellNew.GetValueKind("NullFile") is RegistryValueKind.String or RegistryValueKind.None)
        {
            return new ShellNewTemplate(extension, string.Empty, string.Empty, string.Empty, ShellNewContentKind.NullFile);
        }

        // Handler and Command are intentionally unsupported here. They require invoking
        // the registered Shell extension/command rather than making an empty file.
        return null;
    }

    private static bool TryReadTemplateFile(RegistryKey shellNew, string extension, out string templatePath)
    {
        templatePath = string.Empty;
        if (!shellNew.GetValueNames().Contains("FileName", StringComparer.OrdinalIgnoreCase))
            return false;

        var rawPath = shellNew.GetValue("FileName", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrWhiteSpace(rawPath))
            return false;

        var expandedPath = Environment.ExpandEnvironmentVariables(rawPath.Trim());
        var candidates = Path.IsPathRooted(expandedPath)
            ? new[] { expandedPath }
            : new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew", expandedPath),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), expandedPath),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), expandedPath)
            };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!HasValidContent(extension, stream))
                    continue;
                templatePath = candidate;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to validate ShellNew file {candidate}: {exception.Message}");
            }
        }

        return false;
    }

    private static bool TryReadTemplateData(RegistryKey shellNew, string extension, out byte[] data)
    {
        data = [];
        if (!shellNew.GetValueNames().Contains("Data", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var kind = shellNew.GetValueKind("Data");
        var rawValue = shellNew.GetValue("Data", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        data = kind switch
        {
            RegistryValueKind.Binary when rawValue is byte[] bytes => bytes,
            RegistryValueKind.String when rawValue is string text => Encoding.UTF8.GetBytes(text),
            _ => []
        };

        using var dataStream = new MemoryStream(data, writable: false);
        if (data.Length == 0 || !HasValidContent(extension, dataStream))
        {
            data = [];
            return false;
        }

        return true;
    }

    private static bool HasValidContent(string extension, Stream stream)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".txt":
                return true;
            case ".bmp":
                return HasBitmapHeader(stream);
            case ".lnk":
                return HasShellLinkHeader(stream);
            case ".zip":
            case ".docx":
            case ".pptx":
            case ".xlsx":
                return HasZipContent(stream, extension);
            default:
                return false;
        }
    }

    private static void WriteBlankOfficeDocument(Stream destination, string extension)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        if (string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase))
        {
            WriteEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);
            WriteEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "word/document.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                    <w:p/>
                    <w:sectPr>
                      <w:pgSz w:w="12240" w:h="15840"/>
                      <w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720" w:gutter="0"/>
                    </w:sectPr>
                  </w:body>
                </w:document>
                """);
            return;
        }

        if (!string.Equals(extension, ".pptx", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Generated Office templates are not supported for '{extension}'.");

        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
              <Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/>
              <Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/>
              <Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
              <Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/>
            </Types>
            """);
        WriteEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "ppt/presentation.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:presentation xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
              <p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst>
              <p:sldIdLst><p:sldId id="256" r:id="rId2"/></p:sldIdLst>
              <p:sldSz cx="9144000" cy="5143500" type="wide"/>
              <p:notesSz cx="6858000" cy="9144000"/>
              <p:defaultTextStyle><a:defPPr><a:defRPr lang="zh-CN"/></a:defPPr></p:defaultTextStyle>
            </p:presentation>
            """);
        WriteEntry(archive, "ppt/_rels/presentation.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="slideMasters/slideMaster1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "ppt/slideMasters/slideMaster1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sldMaster xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
              <p:cSld><p:spTree>
                <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
              </p:spTree></p:cSld>
              <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
              <p:sldLayoutIdLst><p:sldLayoutId id="1" r:id="rId1"/></p:sldLayoutIdLst>
              <p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles>
            </p:sldMaster>
            """);
        WriteEntry(archive, "ppt/slideMasters/_rels/slideMaster1.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="../theme/theme1.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "ppt/slideLayouts/slideLayout1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sldLayout xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" type="blank" preserve="1">
              <p:cSld name="Blank"><p:spTree>
                <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
              </p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
            </p:sldLayout>
            """);
        WriteEntry(archive, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="../slideMasters/slideMaster1.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "ppt/slides/slide1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
              <p:cSld><p:spTree>
                <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
              </p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
            </p:sld>
            """);
        WriteEntry(archive, "ppt/slides/_rels/slide1.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "ppt/theme/theme1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Office Theme">
              <a:themeElements>
                <a:clrScheme name="Office"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="1F497D"/></a:dk2><a:lt2><a:srgbClr val="EEECE1"/></a:lt2><a:accent1><a:srgbClr val="4F81BD"/></a:accent1><a:accent2><a:srgbClr val="C0504D"/></a:accent2><a:accent3><a:srgbClr val="9BBB59"/></a:accent3><a:accent4><a:srgbClr val="8064A2"/></a:accent4><a:accent5><a:srgbClr val="4BACC6"/></a:accent5><a:accent6><a:srgbClr val="F79646"/></a:accent6><a:hlink><a:srgbClr val="0000FF"/></a:hlink><a:folHlink><a:srgbClr val="800080"/></a:folHlink></a:clrScheme>
                <a:fontScheme name="Office"><a:majorFont><a:latin typeface="Calibri"/></a:majorFont><a:minorFont><a:latin typeface="Calibri"/></a:minorFont></a:fontScheme>
                <a:fmtScheme name="Office"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:gradFill rotWithShape="1"><a:gsLst><a:gs pos="0"><a:schemeClr val="phClr"><a:tint val="50000"/><a:satMod val="300000"/></a:schemeClr></a:gs><a:gs pos="100000"><a:schemeClr val="phClr"><a:shade val="50000"/><a:satMod val="200000"/></a:schemeClr></a:gs></a:gsLst><a:lin ang="16200000" scaled="1"/></a:gradFill><a:gradFill rotWithShape="1"><a:gsLst><a:gs pos="0"><a:schemeClr val="phClr"><a:tint val="80000"/><a:satMod val="300000"/></a:schemeClr></a:gs><a:gs pos="100000"><a:schemeClr val="phClr"><a:shade val="30000"/><a:satMod val="200000"/></a:schemeClr></a:gs></a:gsLst><a:lin ang="16200000" scaled="1"/></a:gradFill></a:fillStyleLst><a:lnStyleLst><a:ln w="6350" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln><a:ln w="12700" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln><a:ln w="19050" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"><a:tint val="95000"/><a:satMod val="170000"/></a:schemeClr></a:solidFill><a:solidFill><a:schemeClr val="phClr"><a:shade val="90000"/><a:satMod val="150000"/></a:schemeClr></a:solidFill></a:bgFillStyleLst></a:fmtScheme>
              </a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/>
            </a:theme>
            """);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static bool HasBitmapHeader(Stream stream)
    {
        if (stream.Length < 54)
            return false;

        Span<byte> header = stackalloc byte[54];
        if (stream.Read(header) != header.Length || header[0] != (byte)'B' || header[1] != (byte)'M')
            return false;

        var pixelOffset = BitConverter.ToUInt32(header.Slice(10, 4));
        var dibHeaderSize = BitConverter.ToUInt32(header.Slice(14, 4));
        var width = BitConverter.ToInt32(header.Slice(18, 4));
        var height = BitConverter.ToInt32(header.Slice(22, 4));
        return dibHeaderSize >= 40 && pixelOffset >= 14 + dibHeaderSize && pixelOffset <= stream.Length &&
               width > 0 && height != 0;
    }

    private static bool HasShellLinkHeader(Stream stream)
    {
        if (stream.Length < 76)
            return false;

        Span<byte> header = stackalloc byte[76];
        if (stream.Read(header) != header.Length || BitConverter.ToUInt32(header[..4]) != 0x4C)
            return false;

        ReadOnlySpan<byte> shellLinkClassId = [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];
        var flags = BitConverter.ToUInt32(header.Slice(20, 4));
        return header.Slice(4, 16).SequenceEqual(shellLinkClassId) && (flags & 0x00000003) != 0;
    }

    private static bool HasZipContent(Stream stream, string extension)
    {
        const int maximumTemplateSize = 64 * 1024 * 1024;
        using var seekableSnapshot = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            // Some packaged Office VFS files throw on end-relative Seek even though
            // FileStream.CanSeek is true. Read once sequentially and parse the copy.
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (seekableSnapshot.Length + bytesRead > maximumTemplateSize)
                    return false;

                seekableSnapshot.Write(buffer, 0, bytesRead);
            }

            // An empty ZIP archive still needs an end-of-central-directory record.
            // Reject files that cannot possibly be a ZIP before ZipArchive inspects
            // the stream's end position (notably zero-byte Office VFS placeholders).
            if (seekableSnapshot.Length < 22)
                return false;

            seekableSnapshot.Position = 0;
            using var archive = new ZipArchive(seekableSnapshot, ZipArchiveMode.Read, leaveOpen: true);
            if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
                return true;

            var requiredEntry = extension.ToLowerInvariant() switch
            {
                ".docx" => "word/document.xml",
                ".pptx" => "ppt/presentation.xml",
                ".xlsx" => "xl/workbook.xml",
                _ => string.Empty
            };
            var contentTypes = archive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
            var mainPart = archive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName, requiredEntry, StringComparison.OrdinalIgnoreCase));
            if (contentTypes is null || mainPart is null || !IsValidXmlPart(contentTypes))
                return false;

            var expectedRoot = extension.ToLowerInvariant() switch
            {
                ".docx" => "document",
                ".pptx" => "presentation",
                ".xlsx" => "workbook",
                _ => string.Empty
            };
            return IsValidXmlPart(mainPart, expectedRoot);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsValidXmlPart(ZipArchiveEntry entry, string? expectedRootName = null)
    {
        try
        {
            using var content = entry.Open();
            using var reader = XmlReader.Create(content, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true
            });

            string? actualRootName = null;
            while (reader.Read())
            {
                if (actualRootName is null && reader.NodeType == XmlNodeType.Element)
                    actualRootName = reader.LocalName;
            }

            return actualRootName is not null &&
                   (expectedRootName is null || string.Equals(actualRootName, expectedRootName, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is XmlException or IOException or InvalidDataException)
        {
            return false;
        }

        return false;
    }
}
