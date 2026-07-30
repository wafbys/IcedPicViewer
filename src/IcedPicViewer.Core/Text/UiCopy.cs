// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core.Text;

/// <summary>
/// Shared user-visible Chinese copy for WinUI and Avalonia (dialogs + chrome).
/// Status bar uses <see cref="GalleryStatusFormatter"/> (also Chinese).
/// </summary>
public static class UiCopy
{
    // ── Buttons / common ─────────────────────────────────────────────
    public const string Ok = "确定";
    public const string Cancel = "取消";
    public const string GotIt = "知道了";
    public const string Delete = "删除";
    public const string OpenFolder = "打开文件夹";
    public const string Refresh = "刷新";
    public const string LoadMore = "加载更多";
    public const string Slideshow = "幻灯片";
    public const string Fullscreen = "全屏";
    public const string ExitFullscreen = "退出全屏";
    public const string FullscreenTooltip = "全屏 (F11)";
    public const string ExitFullscreenTooltip = "退出全屏 (F11)";
    public const string About = "关于";
    public const string Close = "关闭";
    public const string RevealInFolder = "打开文件位置";
    public const string Fit = "适应";
    public const string FitOneToOne = "1:1";
    public const string FitToggle = "适应 / 1:1";
    public const string Loop = "循环";
    public const string Shuffle = "随机";
    public const string Interval = "间隔";
    public const string SecondsSuffix = "秒";
    public const string Location = "位置";
    public const string Volume = "音量";
    public const string Play = "播放";
    public const string Pause = "暂停";
    public const string StopSlideshow = "停止幻灯片";
    public const string SelectFolderHint = "选择文件夹开始浏览";
    public const string UnknownSize = "未知";
    public const string FileNotFound = "找不到文件";
    public const string NotFoundPrefix = "未找到：";
    public const string UnknownError = "未知错误";
    public const string ArchiveUnsupportedOrCorrupt = "不支持或已损坏的压缩包";
    public const string ArchiveFileMissing = "文件不存在";
    public const string ArchiveIoError = "读写错误";
    public const string ArchiveAccessDenied = "无访问权限";
    public const string ArchiveUnsupportedFormat = "不支持的压缩格式";
    public const string LibVlcUnavailable =
        "LibVLC 不可用（VideoLAN.LibVLC.Windows 未正确加载）。无法播放此编码格式。";
    public static string LibVlcOpenFailed(string path)
        => $"LibVLC 无法打开：{path}";

    // ── Dialogs ──────────────────────────────────────────────────────
    public const string CannotDeleteTitle = "无法删除";
    public const string ConfirmDeleteTitle = "确认删除";

    public static string ArchiveDeleteMessage(string itemName, string archiveFileName)
        => $"压缩包内的媒体 \"{itemName}\" 不支持删除。\n\n" +
           $"如需删除，请在文件资源管理器中处理整个压缩包（{archiveFileName}）。";

    public static string ArchiveDeleteMessageSimple()
        => "压缩包内的媒体不能从本应用删除。请在资源管理器中处理整个压缩包。";

    public static string PermanentDeleteConfirm(string itemName)
        => $"确定要永久删除 \"{itemName}\" 吗？此操作无法撤销。";

    public static string NetworkPermanentDeleteConfirm(string path)
        => $"网络路径文件将永久删除，无法进回收站：\n{path}\n\n确定删除？";
}
