using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ChatServer.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChatServer.Tests;

/// <summary>每个测试类一个独立宿主 + 独立 SQLite 临时库，互不干扰。</summary>
public class ChatServerFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"chatserver-tests-{Guid.NewGuid():N}.db");

    private readonly string _uploadsPath =
        Path.Combine(Path.GetTempPath(), $"chatserver-uploads-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath}");
        builder.UseSetting("Serilog:FileLogging", "false");
        builder.UseSetting("Files:UploadRoot", _uploadsPath);
        builder.UseEnvironment("Development");
    }

    /// <summary>带 Bearer 令牌的客户端。</summary>
    public HttpClient CreateClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 临时文件清理失败不影响测试结论
            }
        }

        try
        {
            if (Directory.Exists(_uploadsPath))
            {
                Directory.Delete(_uploadsPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // 同上
        }
    }
}
