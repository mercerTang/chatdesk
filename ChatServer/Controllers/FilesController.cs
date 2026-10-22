using ChatServer.Data;
using ChatServer.Dtos;
using ChatServer.Models;
using ChatServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatServer.Controllers;

/// <summary>
/// 图片与附件上传。文件落在 uploads/年月/随机名，读取只认随机名；
/// 图片内联返回，其它按附件下载。
/// </summary>
[ApiController]
[Route("api/files")]
public class FilesController : ControllerBase
{
    /// <summary>单文件上限。</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private static readonly Dictionary<string, string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".zip"] = "application/zip",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    };

    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FilesController> _logger;

    public FilesController(
        AppDbContext db,
        IWebHostEnvironment env,
        IConfiguration configuration,
        ILogger<FilesController> logger)
    {
        _db = db;
        _env = env;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>上传单个文件；返回可直接写入消息的 URL。</summary>
    [HttpPost]
    [Authorize]
    [RequestSizeLimit(MaxFileBytes + 64 * 1024)]
    public async Task<ActionResult<FileAssetDto>> Upload(
        [FromForm] IFormFile? file, CancellationToken ct)
    {
        var uploaderId = User.GetAccountId();
        if (uploaderId == 0)
        {
            return Unauthorized(new { error = "登录状态无效" });
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "请选择要上传的文件" });
        }

        if (file.Length > MaxFileBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "文件不能超过 10 MB" });
        }

        var originalName = NormalizeName(file.FileName);
        var extension = Path.GetExtension(originalName);
        if (string.IsNullOrEmpty(extension) || !AllowedExtensions.TryGetValue(extension, out var contentType))
        {
            return BadRequest(new { error = "不支持的文件类型" });
        }

        // 图片按文件头再核一次，避免改扩展名绕过
        if (contentType.StartsWith("image/", StringComparison.Ordinal) && !await LooksLikeImageAsync(file, extension, ct))
        {
            return BadRequest(new { error = "图片内容与扩展名不符" });
        }

        var folder = DateTimeOffset.UtcNow.ToLocalTime().ToString("yyyyMM");
        var storedName = $"{folder}/{Guid.NewGuid():N}{extension}";
        var absolute = Path.Combine(UploadRoot(), storedName);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        await using (var target = System.IO.File.Create(absolute))
        {
            await file.CopyToAsync(target, ct);
        }

        var asset = new FileAsset
        {
            StoredName = storedName,
            OriginalName = originalName,
            ContentType = contentType,
            Size = file.Length,
            UploaderId = uploaderId
        };
        _db.FileAssets.Add(asset);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "文件 {Name}（{Size} 字节）已上传，ID {Id}", originalName, file.Length, asset.Id);

        return CreatedAtAction(
            nameof(Download),
            new { id = asset.Id },
            new FileAssetDto(asset.Id, $"/api/files/{asset.Id}", originalName, asset.Size, contentType));
    }

    /// <summary>读取文件：图片内联，其它按附件下载。</summary>
    [HttpGet("{id:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> Download(int id, CancellationToken ct)
    {
        var asset = await _db.FileAssets.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (asset is null)
        {
            return NotFound();
        }

        var absolute = Path.Combine(UploadRoot(), asset.StoredName);
        if (!System.IO.File.Exists(absolute))
        {
            return NotFound();
        }

        var isImage = asset.ContentType.StartsWith("image/", StringComparison.Ordinal);
        return isImage
            ? PhysicalFile(absolute, asset.ContentType, enableRangeProcessing: true)
            : PhysicalFile(absolute, asset.ContentType, asset.OriginalName, enableRangeProcessing: true);
    }

    private string UploadRoot()
    {
        // 测试用 Files:UploadRoot 指定临时目录，避免写入项目目录
        var configured = _configuration["Files:UploadRoot"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(_env.ContentRootPath, "uploads")
            : configured;
    }

    private static string NormalizeName(string raw)
    {
        var name = Path.GetFileName(raw.Replace('\\', '/'));
        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, '_');
        }
        name = name.Trim();
        if (name.Length == 0)
        {
            name = "file";
        }
        return name.Length > 120 ? name[^120..] : name;
    }

    private static async Task<bool> LooksLikeImageAsync(IFormFile file, string extension, CancellationToken ct)
    {
        var buffer = new byte[12];
        await using var stream = file.OpenReadStream();
        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
        if (read < 4)
        {
            return false;
        }

        return extension.ToLowerInvariant() switch
        {
            ".png" => buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47,
            ".jpg" or ".jpeg" => buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF,
            ".gif" => buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46,
            ".webp" => read >= 12
                && buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46
                && buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50,
            _ => false
        };
    }
}
