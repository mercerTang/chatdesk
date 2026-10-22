using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ChatServer.Dtos;

namespace ChatServer.Tests;

/// <summary>图片与附件上传：鉴权、类型白名单、大小上限、内容与扩展名一致性。</summary>
public class FileApiTests : IClassFixture<ChatServerFactory>
{
    private readonly ChatServerFactory _factory;

    public FileApiTests(ChatServerFactory factory)
    {
        _factory = factory;
    }

    // 1x1 透明 PNG 的最小合法字节
    private static byte[] PngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static MultipartFormDataContent CreateForm(byte[] bytes, string fileName, string contentType)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "file", fileName);
        return form;
    }

    [Fact]
    public async Task Upload_RequiresAuthentication()
    {
        using var form = CreateForm(PngBytes(), "a.png", "image/png");
        var response = await _factory.CreateClient().PostAsync("/api/files", form);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_ThenDownload_ReturnsSameBytes()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        var client = _factory.CreateClient(agent.Token);
        var bytes = PngBytes();

        using var form = CreateForm(bytes, "截图.png", "image/png");
        var upload = await client.PostAsync("/api/files", form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        var dto = (await upload.Content.ReadFromJsonAsync<FileAssetDto>())!;
        Assert.Equal("截图.png", dto.Name);
        Assert.StartsWith("/api/files/", dto.Url);

        var download = await _factory.CreateClient().GetAsync(dto.Url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Upload_RejectsUnsupportedExtension()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        using var form = CreateForm("MZ"u8.ToArray(), "virus.exe", "application/octet-stream");
        var response = await _factory.CreateClient(agent.Token).PostAsync("/api/files", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_RejectsOversizedFile()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        var big = new byte[10 * 1024 * 1024 + 1];
        using var form = CreateForm(big, "big.bin", "application/octet-stream");
        var response = await _factory.CreateClient(agent.Token).PostAsync("/api/files", form);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Upload_RejectsImageWithFakeContent()
    {
        var agent = await TestApi.LoginAgentAsync(_factory.CreateClient(), "agent1");
        using var form = CreateForm("not a png"u8.ToArray(), "fake.png", "image/png");
        var response = await _factory.CreateClient(agent.Token).PostAsync("/api/files", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
