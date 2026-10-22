namespace ChatServer.Models;

/// <summary>上传的文件。磁盘只存随机名，原名与类型留在这里供下载时还原。</summary>
public class FileAsset
{
    public int Id { get; set; }

    /// <summary>磁盘相对路径（年月/随机名.扩展名），同时用作唯一索引。</summary>
    public string StoredName { get; set; } = string.Empty;

    public string OriginalName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long Size { get; set; }

    public int UploaderId { get; set; }
    public Account? Uploader { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
