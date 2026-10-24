namespace MotionVfx;

internal static class AuthorLinks
{
    // UI buttons select a fixed site; the desktop bridge never executes an
    // arbitrary URI or a local path supplied by a resource or mailbox payload.
    public static string Url(string? site) => site switch
    {
        "bilibili" => "https://space.bilibili.com/437668597?spm_id_from=333.1007.0.0",
        "caimogu" => "https://www.caimogu.cc/user/1472999.html",
        _ => throw new ArgumentException("未知作者链接。")
    };
}
