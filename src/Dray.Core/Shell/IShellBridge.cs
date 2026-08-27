namespace Dray.Core.Shell;

/// <summary>Which button the user chose in a native confirmation.</summary>
public enum ConfirmResult
{
    Cancel,
    Confirm,
}

/// <summary>
/// A destructive confirmation. Names what will be lost rather than asking "Are you sure?".
/// </summary>
/// <param name="Title">What is about to happen, e.g. "Remove 3 volumes?".</param>
/// <param name="Body">What will be lost, specifically, including anything unrecoverable.</param>
/// <param name="ConfirmLabel">The verb, e.g. "Remove". Never "OK".</param>
/// <param name="TypeToConfirm">
/// When set, the user must type this exact text before confirming. Required for irreversible bulk
/// operations — pruning, or deleting a volume that still has data in it.
/// </param>
public sealed record DestructiveConfirm(
    string Title,
    string Body,
    string ConfirmLabel,
    string? TypeToConfirm = null);

/// <summary>Options for a native file or folder picker.</summary>
public sealed record FilePickerOptions(
    string Title,
    string? SuggestedName = null,
    IReadOnlyList<string>? Extensions = null);

/// <summary>Severity of a transient notification.</summary>
public enum NoticeKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// The native surface a page can reach: dialogs, pickers, the file manager, notifications.
/// <para>
/// A platform chrome manager takes this and the current <see cref="PageChrome"/>, and nothing else.
/// Anything feature-shaped appearing on this interface is a design smell — see
/// docs/NATIVE-SHELL.md section 2.5.
/// </para>
/// </summary>
public interface IShellBridge
{
    /// <summary>
    /// Native confirmation for a destructive action — an <c>NSAlert</c> sheet on macOS, a
    /// <c>ContentDialog</c> on Windows, an <c>AdwMessageDialog</c> on GTK.
    /// </summary>
    Task<ConfirmResult> ConfirmDestructiveAsync(DestructiveConfirm request, CancellationToken ct = default);

    Task<string?> PickFileAsync(FilePickerOptions options, CancellationToken ct = default);

    Task<string?> PickFolderAsync(string title, CancellationToken ct = default);

    Task<string?> SaveFileAsync(FilePickerOptions options, CancellationToken ct = default);

    /// <summary>Reveal a path in Finder, Explorer, or the desktop's file manager.</summary>
    Task RevealInFileManagerAsync(string path, CancellationToken ct = default);

    /// <summary>Open a URL in the user's browser. Never inside the app's own WebView.</summary>
    Task OpenExternalAsync(string url, CancellationToken ct = default);

    Task WriteClipboardAsync(string text, CancellationToken ct = default);

    /// <summary>A native notification, for work that finished while the app was in the background.</summary>
    Task NotifyAsync(string title, string? body, NoticeKind kind = NoticeKind.Info, CancellationToken ct = default);

    /// <summary>Running-container count on the menu-bar item or taskbar. Null clears it.</summary>
    void SetBadge(int? count);
}
