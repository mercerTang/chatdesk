using ChatServer.Models;

namespace ChatServer.Dtos;

/// <summary>上传成功后的文件描述；Url 可直接放进消息的 Markdown。</summary>
public record FileAssetDto(int Id, string Url, string Name, long Size, string ContentType);
