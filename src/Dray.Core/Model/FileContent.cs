using System.Text;

namespace Dray.Core.Model;

/// <summary>Why a file cannot be opened in the editor.</summary>
public enum FileOpenRefusal
{
    None,

    /// <summary>Bigger than the editor is willing to hold.</summary>
    TooLarge,

    /// <summary>Contains bytes no text encoding explains. Showing it as text would be a lie.</summary>
    Binary,
}

/// <summary>
/// One file's contents, decoded for editing — or the reason it was not.
/// <para>
/// Refusing is a real outcome rather than an error. A 900 MB database file and a JPEG are both
/// perfectly valid things to find in a container, and neither belongs in a text editor; saying so
/// plainly is better than opening a window of replacement characters.
/// </para>
/// </summary>
public sealed record FileContent
{
    /// <summary>
    /// Above this, Dray declines to open a file.
    /// <para>
    /// Monaco is comfortable well past this, but the whole file crosses a socket, is base64'd
    /// through JS interop, and is held three times over in the process. Four megabytes of config
    /// is already far more than anyone edits by hand.
    /// </para>
    /// </summary>
    public const long MaxEditableBytes = 4 * 1024 * 1024;

    public required string Path { get; init; }

    public string Text { get; init; } = "";

    public long SizeBytes { get; init; }

    public FileOpenRefusal Refusal { get; init; } = FileOpenRefusal.None;

    /// <summary>
    /// Whether the file ended without a trailing newline.
    /// <para>
    /// Preserved on save. Most editors quietly add one, which turns "I changed one value" into a
    /// two-line diff and makes Dray look like it did something it did not.
    /// </para>
    /// </summary>
    public bool HadTrailingNewline { get; init; } = true;

    /// <summary>Whether the original used CRLF, so a save does not silently rewrite line endings.</summary>
    public bool UsedCrlf { get; init; }

    public bool CanEdit => Refusal == FileOpenRefusal.None;

    /// <summary>The Monaco language id for this path, from its extension or its name.</summary>
    public string Language => LanguageFor(Path);

    public static FileContent Refuse(string path, long size, FileOpenRefusal refusal)
        => new() { Path = path, SizeBytes = size, Refusal = refusal };

    /// <summary>
    /// Decode bytes into an editable file, or refuse them.
    /// </summary>
    public static FileContent Decode(string path, byte[] bytes)
    {
        if (bytes.LongLength > MaxEditableBytes)
            return Refuse(path, bytes.LongLength, FileOpenRefusal.TooLarge);

        if (LooksBinary(bytes))
            return Refuse(path, bytes.LongLength, FileOpenRefusal.Binary);

        // UTF-8 covers essentially every config file in a container image. A BOM is stripped for
        // display and put back on save.
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        return new FileContent
        {
            Path = path,
            Text = text.Replace("\r\n", "\n", StringComparison.Ordinal),
            SizeBytes = bytes.LongLength,
            HadTrailingNewline = text.EndsWith('\n'),
            UsedCrlf = text.Contains("\r\n", StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// Re-encode edited text, restoring whatever line ending and trailing newline the file had.
    /// </summary>
    public byte[] Encode(string edited)
    {
        var text = edited.Replace("\r\n", "\n", StringComparison.Ordinal);

        // The editor may have added or removed the final newline; the original file's convention
        // wins, so saving an unchanged file produces identical bytes.
        text = HadTrailingNewline
            ? text.EndsWith('\n') ? text : text + "\n"
            : text.TrimEnd('\n');

        if (UsedCrlf) text = text.Replace("\n", "\r\n", StringComparison.Ordinal);

        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Whether these bytes are text.
    /// <para>
    /// A NUL byte is the signal every tool from <c>grep</c> to <c>git</c> uses, and it is right far
    /// more often than any heuristic worth the extra code. Only the head is examined, because a
    /// binary file declares itself immediately and reading further is wasted on a large one.
    /// </para>
    /// </summary>
    internal static bool LooksBinary(byte[] bytes)
    {
        var window = Math.Min(bytes.Length, 8000);

        for (var i = 0; i < window; i++)
        {
            if (bytes[i] == 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Which syntax to highlight with.
    /// <para>
    /// By extension where there is one, and by whole filename where there is not — the files most
    /// worth editing in a container are <c>Dockerfile</c>, <c>.env</c> and <c>nginx.conf</c>, none
    /// of which have a useful extension.
    /// </para>
    /// </summary>
    public static string LanguageFor(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];

        var byName = name.ToLowerInvariant() switch
        {
            "dockerfile" or "containerfile" => "dockerfile",
            "makefile" => "makefile",
            ".env" => "ini",
            "nginx.conf" => "ini",
            ".gitignore" or ".dockerignore" => "plaintext",
            "hosts" or "hostname" or "resolv.conf" or "passwd" or "group" or "fstab" => "plaintext",
            _ => null,
        };

        if (byName is not null) return byName;

        // ".env.production" and friends.
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)) return "ini";

        var dot = name.LastIndexOf('.');
        if (dot < 0) return "plaintext";

        return name[(dot + 1)..].ToLowerInvariant() switch
        {
            "json" => "json",
            "yml" or "yaml" => "yaml",
            "xml" or "plist" or "csproj" or "props" or "targets" => "xml",
            "sh" or "bash" or "zsh" or "profile" or "bashrc" => "shell",
            "conf" or "cfg" or "ini" or "properties" or "toml" => "ini",
            "md" or "markdown" => "markdown",
            "sql" => "sql",
            "js" or "mjs" or "cjs" => "javascript",
            "ts" => "typescript",
            "css" => "css",
            "html" or "htm" => "html",
            "py" => "python",
            "rb" => "ruby",
            "go" => "go",
            "rs" => "rust",
            "cs" => "csharp",
            "java" => "java",
            "php" => "php",
            "lua" => "lua",
            "log" or "txt" => "plaintext",
            _ => "plaintext",
        };
    }
}
